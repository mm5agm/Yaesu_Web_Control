using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Speech.Recognition;
using System.Speech.AudioFormat;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// In-process voice control via Windows SAPI 5 / System.Speech.Recognition.
    /// Replaces the parked Alexa work — see docs/VoiceControl/v1-plan.md.
    /// </summary>
    ///
    /// <remarks>
    /// Lifecycle:
    /// <list type="bullet">
    /// <item><c>StartAsync</c> (hosted service): attempts to construct the
    /// SAPI recogniser for <c>Settings.VoiceActiveLocale</c> (default
    /// en-GB) and load its installed pack. Failures are logged but
    /// non-fatal — the rest of YWC still runs.</item>
    /// <item><c>SwitchLocaleAsync</c> (called by VoiceController when the
    /// user changes the language switcher in Settings): disposes the
    /// current engine and reconstructs it for the new culture — see §4.2.</item>
    /// <item><c>StartListeningAsync</c> (called by VoiceController when the
    /// on-screen PTT button is pressed): wires audio input + begins
    /// recognition. State -> Listening.</item>
    /// <item>SAPI fires <c>SpeechRecognized</c> when a phrase matches a
    /// grammar rule. The handler extracts the semantic <c>intent</c> tag
    /// and any parameters, then hands off to <see cref="IntentDispatcher"/>.</item>
    /// <item><c>StopListening</c>: ends recognition. State -> Idle.</item>
    /// </list>
    /// SAPI's <c>SpeechRecognitionEngine</c> is not thread-safe. All engine
    /// operations are serialised through <see cref="_engineLock"/>.
    /// </remarks>
    public sealed class VoiceControlService : BackgroundService
    {
        private readonly ILogger<VoiceControlService> _logger;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly IntentDispatcher _intentDispatcher;
        private readonly IWebHostEnvironment _env;
        private readonly ISettingsService _settings;
        private readonly VoiceTtsService _tts;
        private readonly VoicePhraseStore _phraseStore;

        private readonly object _engineLock = new();
        private SpeechRecognitionEngine? _engine;

        // Serializes §2.5 "Try it" tests end-to-end (grammar swap through
        // Recognize through restore). _engineLock alone isn't enough here --
        // it's only held for the brief swap, not for the several seconds
        // Recognize() blocks. Two Try It clicks close together used to
        // interleave: the second call's UnloadAllGrammars()/LoadGrammar()
        // would silently replace the first call's still-in-flight test
        // grammar, so both recognitions ended up matching against whichever
        // row was tested second (observed as unrelated phrases both
        // reporting the other row's phrase as "heard").
        private readonly SemaphoreSlim _tryPhraseGate = new(1, 1);
        private bool _audioWired;
        private string _activeCulture = VoicePhraseStore.DefaultCulture;

        // Microphone selection (Settings → Voice Control). _configuredMicName is
        // the WaveIn product name the user picked, or null/empty for the Windows
        // default device. When a specific device is chosen we capture it via
        // _micStream and feed SAPI SetInputToAudioStream, because System.Speech
        // can't target a device by name. _boundMicIndex is the WaveIn index
        // currently bound (-1 = the default device is bound). See
        // MicrophoneCapture / EnsureAudioInput. All guarded by _engineLock.
        private volatile string? _configuredMicName;
        private MicrophoneStream? _micStream;
        private int _boundMicIndex = -1;

        // §6.5 "Test this pack" dry run -- set for the duration of a
        // StartListeningAsync(dryRun: true) session; OnSpeechRecognized reads
        // it to skip actually sending the matched CAT command.
        private bool _dryRun;

        // Which VFO's mic button started the current/last listening session
        // ("A" or "B") -- set by StartListeningAsync, read by
        // OnSpeechRecognized when handing off to IntentDispatcher, and
        // included in every status broadcast so each VFO's mic button knows
        // whether it's the one currently live (only one SAPI session at a
        // time, per _engineLock).
        private string _targetVfo = "A";

        /// <summary>The locale currently loaded into the live SAPI engine (or the last one attempted, if construction failed).</summary>
        public string ActiveCulture => _activeCulture;

        // Status is read by /api/voice/status; updated whenever state changes.
        // Volatile reference assignment is atomic in .NET so no lock needed
        // for the typical reader path.
        private VoiceStatusUpdate _status = new(VoiceState.Idle, null, null, null);
        public VoiceStatusUpdate CurrentStatus => _status;

        public VoiceControlService(
            ILogger<VoiceControlService> logger,
            IHubContext<RadioHub> hubContext,
            IntentDispatcher intentDispatcher,
            IWebHostEnvironment env,
            ISettingsService settings,
            VoiceTtsService tts,
            VoicePhraseStore phraseStore)
        {
            _logger = logger;
            _hubContext = hubContext;
            _intentDispatcher = intentDispatcher;
            _env = env;
            _settings = settings;
            _tts = tts;
            _phraseStore = phraseStore;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // Honour the Settings toggle. If voice control is disabled the
            // engine is never constructed -- no SAPI load, no mic permission
            // grab, no holding the recogniser in memory. The user can flip
            // the toggle in Settings and restart YWC to enable.
            var settings = await _settings.GetSettingsAsync();
            _configuredMicName = settings.VoiceInputDeviceName;
            _tts.ApplyOutputDevice(settings.VoiceOutputDeviceName);
            if (settings.VoiceControlEnabled)
            {
                TryInitialiseEngine(string.IsNullOrWhiteSpace(settings.VoiceActiveLocale)
                    ? VoicePhraseStore.DefaultCulture
                    : settings.VoiceActiveLocale);
            }
            else
            {
                _logger.LogInformation("[Voice] Disabled in Settings -- skipping SAPI engine construction.");
                UpdateStatus(VoiceState.Idle);
            }
            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            DisposeEngine();
            await base.StopAsync(cancellationToken);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Nothing to do in the background loop — voice control is
            // entirely event-driven (SpeechRecognized) once the engine is
            // running. Just park until shutdown.
            return Task.Delay(Timeout.Infinite, stoppingToken);
        }

        /// <summary>
        /// Begin recognising. Called by the API endpoint when the PTT button
        /// is pressed. Idempotent — calling while already listening is a no-op.
        /// <paramref name="dryRun"/> (§6.5) leaves recognition and intent
        /// matching untouched but suppresses the actual CAT send — used by
        /// the Settings "Test this pack" modal so a pack can be exercised
        /// without a radio connected or without changing its live state.
        /// <paramref name="vfo"/> ("A" or "B") is which mic button was
        /// pressed; forwarded to IntentDispatcher so per-VFO intents (set
        /// frequency, nudge, band up/down, etc.) land on the right receiver.
        /// </summary>
        public async Task<bool> StartListeningAsync(bool dryRun = false, string vfo = "A")
        {
            lock (_engineLock)
            {
                if (_engine == null)
                {
                    UpdateStatus(VoiceState.Error, error: $"Speech recogniser not available (check Windows {_activeCulture} speech pack)");
                    return false;
                }

                _dryRun = dryRun;
                _targetVfo = vfo;
                try
                {
                    // Bind the configured input (chosen mic or Windows default).
                    // Deferred until first start so YWC boots fine on machines
                    // without a microphone. For a chosen device we then drop any
                    // audio captured while idle so recognition starts clean.
                    EnsureAudioInput();
                    _micStream?.DiscardBuffered();
                    _engine.RecognizeAsync(RecognizeMode.Multiple);
                    UpdateStatus(VoiceState.Listening);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // Already recognising — fine, idempotent.
                    UpdateStatus(VoiceState.Listening);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Voice] Failed to start recogniser");
                    UpdateStatus(VoiceState.Error, error: ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// Stop recognising. Called by the API endpoint when the PTT button
        /// is released. Idempotent.
        /// </summary>
        public Task StopListeningAsync()
        {
            lock (_engineLock)
            {
                try
                {
                    _engine?.RecognizeAsyncStop();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Voice] RecognizeAsyncStop threw (non-fatal)");
                }
                _dryRun = false;
                UpdateStatus(VoiceState.Idle);
            }
            return Task.CompletedTask;
        }

        // ── Audio input binding ────────────────────────────────────────────
        // System.Speech can bind only to the Windows default device or to a raw
        // PCM stream. When the user picks a specific mic in Settings we capture
        // it ourselves (MicrophoneStream, 48 kHz/16-bit/mono) and feed SAPI via
        // SetInputToAudioStream; otherwise we use SetInputToDefaultAudioDevice.
        // All three methods below must be called with _engineLock held.

        private static SpeechAudioFormatInfo MicFormat() =>
            new(MicrophoneCapture.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

        /// <summary>
        /// Idempotently bind the configured input. Latches like the original
        /// default-device behaviour: once the correct device is bound, repeated
        /// PTT starts don't re-open it. A configured mic that's since been
        /// unplugged (index -1) falls back to the Windows default.
        /// </summary>
        private void EnsureAudioInput()
        {
            if (_engine == null) return;

            int index = MicrophoneCapture.FindDeviceIndex(_configuredMicName);
            if (index < 0)
            {
                if (!_audioWired || _micStream != null)
                {
                    DisposeMicStream();
                    _engine.SetInputToDefaultAudioDevice();
                    _boundMicIndex = -1;
                    _audioWired = true;
                }
                return;
            }

            if (_micStream == null || _boundMicIndex != index)
            {
                DisposeMicStream();
                var stream = new MicrophoneStream(index, _logger);
                _engine.SetInputToAudioStream(stream, MicFormat());
                _micStream = stream;
                _boundMicIndex = index;
                _audioWired = true;
                _logger.LogInformation("[Voice] Listening on microphone '{Name}' (WaveIn #{Index})", _configuredMicName, index);
            }
        }

        /// <summary>
        /// Ensure the configured input is bound and discard any audio queued
        /// since the last recognition, so ambient noise / a previous utterance
        /// isn't replayed as the first match. For the default device that means
        /// SetInputToNull()+SetInputToDefaultAudioDevice(); for a chosen mic it
        /// clears the capture queue (or binds fresh, which is inherently clean).
        /// </summary>
        private void FlushAudioInput()
        {
            if (_engine == null) return;

            int index = MicrophoneCapture.FindDeviceIndex(_configuredMicName);
            if (index < 0)
            {
                DisposeMicStream();
                _engine.SetInputToNull();
                _engine.SetInputToDefaultAudioDevice();
                _boundMicIndex = -1;
                _audioWired = true;
                return;
            }

            if (_micStream == null || _boundMicIndex != index)
                EnsureAudioInput();          // fresh bind -> already clean
            else
                _micStream.DiscardBuffered();
        }

        private void DisposeMicStream()
        {
            if (_micStream == null) return;
            try { _engine?.SetInputToNull(); } catch { /* best-effort */ }
            try { _micStream.Dispose(); } catch { /* best-effort */ }
            _micStream = null;
            _boundMicIndex = -1;
        }

        /// <summary>
        /// Apply a newly-chosen input device (from the Settings picker) without
        /// an app restart. Persistence is the caller's job; this only updates
        /// the live engine. If currently listening, recognition is rebound to
        /// the new device immediately; otherwise the change takes effect on the
        /// next PTT press. Pass null/empty for the Windows default device.
        /// </summary>
        public void ApplyInputDevice(string? deviceName)
        {
            lock (_engineLock)
            {
                _configuredMicName = deviceName;
                bool wasListening = _status.State == VoiceState.Listening;
                if (_engine != null && wasListening)
                {
                    try { _engine.RecognizeAsyncCancel(); } catch { /* best-effort */ }
                }

                // Drop the current binding so the next EnsureAudioInput rebinds
                // to the new device.
                DisposeMicStream();
                _audioWired = false;

                if (_engine != null && wasListening)
                {
                    try
                    {
                        EnsureAudioInput();
                        _micStream?.DiscardBuffered();
                        _engine.RecognizeAsync(RecognizeMode.Multiple);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Voice] Failed to rebind input device '{Device}'", deviceName);
                        UpdateStatus(VoiceState.Error, error: ex.Message);
                    }
                }
            }
            _logger.LogInformation("[Voice] Input device set to '{Device}'",
                string.IsNullOrWhiteSpace(deviceName) ? "(Windows default)" : deviceName);
        }

        /// <summary>
        /// Apply a newly-chosen playback device for spoken confirmations (from
        /// the Settings picker). Delegates to the TTS service; persistence is the
        /// caller's job. Pass null/empty for the Windows default device.
        /// </summary>
        public void ApplyOutputDevice(string? deviceName) => _tts.ApplyOutputDevice(deviceName);

        /// <summary>Speak a phrase now — used by the Settings "Test" button so
        /// the operator can confirm the chosen speaker actually makes sound
        /// without having to issue a live voice command.</summary>
        public void TestSpeak(string text) => _tts.Speak(text);

        /// <summary>
        /// Reloads the grammar for the active culture without restarting the
        /// recogniser. Called by the API after the user saves the phrases
        /// editor. Safe to call while listening — stops, swaps grammar, restarts.
        /// </summary>
        public void ReloadGrammar()
        {
            lock (_engineLock)
            {
                if (_engine == null) return;
                try
                {
                    var wasListening = _status.State == VoiceState.Listening;
                    if (wasListening) _engine.RecognizeAsyncCancel();

                    var phraseCfg = _phraseStore.Load(_activeCulture);
                    var grammar = VoiceGrammar.Build(phraseCfg, _activeCulture);
                    _engine.UnloadAllGrammars();
                    _engine.LoadGrammar(grammar);
                    _logger.LogInformation("[Voice] Grammar reloaded for {Culture}", _activeCulture);

                    if (wasListening) _engine.RecognizeAsync(RecognizeMode.Multiple);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Voice] Failed to reload grammar");
                    UpdateStatus(VoiceState.Error, error: ex.Message);
                }
            }
        }

        /// <summary>
        /// Per-row "Try it" test (§2.5): briefly swaps the live engine's
        /// grammar for a one-off Choices grammar built from just the phrases
        /// being edited, listens for a single utterance up to
        /// <paramref name="timeout"/>, then restores whatever grammar/listen
        /// state was active before. Reuses the single live engine (rather
        /// than spinning up a second SpeechRecognitionEngine) so it can't
        /// fight the main recogniser over exclusive audio-device access.
        /// Returns (matched, heardText, error).
        /// </summary>
        public async Task<(bool Matched, string? Heard, float? Confidence, string? Error)> TryPhraseAsync(
            IReadOnlyList<string> phrases, TimeSpan timeout)
        {
            if (phrases == null || phrases.Count == 0)
                return (false, null, null, "No phrases to test.");

            // Serialize the whole test end-to-end -- see _tryPhraseGate's
            // doc comment for why _engineLock alone isn't sufficient here.
            await _tryPhraseGate.WaitAsync();
            try
            {
                SpeechRecognitionEngine? engine;
                bool wasListening;
                lock (_engineLock)
                {
                    engine = _engine;
                    if (engine == null)
                        return (false, null, null, $"Speech recogniser not available (check Windows {_activeCulture} speech pack)");

                    wasListening = _status.State == VoiceState.Listening;
                    if (wasListening) engine.RecognizeAsyncCancel();

                    try
                    {
                        // Re-wire the audio input immediately before every test,
                        // not just the first one. SAPI keeps capturing
                        // continuously once an input device is wired, even
                        // between explicit Recognize() calls -- so ambient
                        // noise, or the tail of what was said for a *previous*
                        // Try It test, sits queued in the engine's audio buffer
                        // and gets consumed as the start of the next
                        // Recognize() call, matching against a phrase the user
                        // never said this time. FlushAudioInput() discards that
                        // queue (whether the input is the Windows default device
                        // or a chosen mic) so each test only hears audio spoken
                        // after it starts.
                        FlushAudioInput();

                        engine.UnloadAllGrammars();
                        var builder = new System.Speech.Recognition.GrammarBuilder(
                            new System.Speech.Recognition.Choices(phrases.ToArray()))
                        {
                            Culture = new CultureInfo(_activeCulture),
                        };
                        engine.LoadGrammar(new System.Speech.Recognition.Grammar(builder));
                        _logger.LogInformation("[Voice] Try-it: testing phrases [{Phrases}], engine now has {GrammarCount} grammar(s) loaded",
                            string.Join(" | ", phrases), engine.Grammars.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Voice] Try-it: failed to build test grammar");
                        RestoreLiveGrammar(engine, wasListening);
                        return (false, null, null, ex.Message);
                    }
                }

                try
                {
                    // Recognize() blocks synchronously -- run it off the request
                    // thread. Not holding _engineLock here: it only blocks other
                    // engine operations, and this can legitimately take seconds.
                    // _tryPhraseGate (held for this whole method) is what
                    // actually keeps a second Try It from swapping the grammar
                    // out from under this call while it's blocked here.
                    var result = await Task.Run(() => engine.Recognize(timeout));
                    if (result == null)
                    {
                        _logger.LogInformation("[Voice] Try-it: Recognize() returned null (timed out / no speech)");
                        return (false, null, null, (string?)null);
                    }
                    _logger.LogInformation("[Voice] Try-it: Recognize() returned Text=\"{Text}\" Confidence={Confidence} Grammar={Grammar}",
                        result.Text, result.Confidence, result.Grammar?.Name);

                    // A small forced-choice grammar (just this row's phrases) has
                    // nowhere else to put ambient noise or an unrelated utterance
                    // -- SAPI still has to pick its closest-matching choice, so it
                    // very often returns a non-null result even for silence or a
                    // completely different phrase. Gate on the same MinConfidence
                    // threshold the live recogniser uses, or every Try It would
                    // report "heard" regardless of what was actually said.
                    bool matched = result.Confidence >= MinConfidence;
                    return (matched, result.Text, result.Confidence, (string?)null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Voice] Try-it: recognition failed");
                    return (false, null, null, ex.Message);
                }
                finally
                {
                    lock (_engineLock)
                    {
                        RestoreLiveGrammar(engine, wasListening);
                    }
                }
            }
            finally
            {
                _tryPhraseGate.Release();
            }
        }

        /// <summary>Must be called with _engineLock held.</summary>
        private void RestoreLiveGrammar(SpeechRecognitionEngine engine, bool resumeListening)
        {
            try
            {
                engine.UnloadAllGrammars();
                var phraseCfg = _phraseStore.Load(_activeCulture);
                engine.LoadGrammar(VoiceGrammar.Build(phraseCfg, _activeCulture));
                if (resumeListening)
                {
                    // Flush before resuming live listening too, so nothing
                    // spoken during the Try It test (or the silence after it)
                    // gets replayed into the live grammar as its first match.
                    FlushAudioInput();
                    engine.RecognizeAsync(RecognizeMode.Multiple);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Try-it: failed to restore live grammar");
                UpdateStatus(VoiceState.Error, error: ex.Message);
            }
        }

        /// <summary>
        /// Switches the live engine to a different installed locale (§4.2) —
        /// disposes the current SAPI engine (if any) and constructs a new one
        /// for <paramref name="culture"/>, persisting the choice to Settings
        /// so it's restored on next launch. Returns false if the Windows SAPI
        /// recogniser for that locale isn't installed; the pack can still be
        /// selected as "active" (so it's ready the moment the user installs
        /// the Windows speech pack), but the engine won't actually be
        /// listening until then — same non-fatal-failure pattern as startup.
        /// </summary>
        public async Task<bool> SwitchLocaleAsync(string culture)
        {
            DisposeEngine();

            var settings = await _settings.GetSettingsAsync();
            settings.VoiceActiveLocale = culture;
            await _settings.SaveSettingsAsync(settings);

            if (!settings.VoiceControlEnabled)
            {
                // Voice control itself is off -- record the choice but don't
                // spin up an engine (mirrors StartAsync's own gate).
                _activeCulture = culture;
                UpdateStatus(VoiceState.Idle);
                return true;
            }

            TryInitialiseEngine(culture);
            return _engine != null;
        }

        // -----------------------------------------------------------------

        private void TryInitialiseEngine(string culture)
        {
            _activeCulture = culture;
            try
            {
                var cultureInfo = new CultureInfo(culture);
                var available = SpeechRecognitionEngine.InstalledRecognizers()
                    .Any(r => r.Culture.Name.Equals(cultureInfo.Name, StringComparison.OrdinalIgnoreCase));
                if (!available)
                {
                    _logger.LogWarning(
                        "[Voice] {Culture} SAPI recogniser not installed on this machine. " +
                        "Install via Settings → Time & Language → Speech.", culture);
                    UpdateStatus(VoiceState.Error, error: $"{culture} Windows speech pack not installed");
                    return;
                }

                var engine = new SpeechRecognitionEngine(cultureInfo);
                engine.SpeechRecognized += OnSpeechRecognized;
                engine.SpeechRecognitionRejected += OnSpeechRejected;
                engine.RecognizeCompleted += OnRecognizeCompleted;

                // Grammar is built programmatically via VoiceGrammar.Build().
                // We can't load Commands.<culture>.srgs at runtime because
                // System.Speech on .NET 6+ throws PlatformNotSupportedException
                // from Grammar.LoadCfg -- the in-process SAPI 5 SRGS compiler
                // isn't shipped with the modern NuGet. The SRGS XML file is a
                // human-readable/exportable spec only; VoiceGrammar.cs mirrors
                // it using GrammarBuilder + Choices.
                var phraseCfg = _phraseStore.Load(culture);
                var grammar = VoiceGrammar.Build(phraseCfg, culture);
                engine.LoadGrammar(grammar);
                _engine = engine;
                _logger.LogInformation(
                    "[Voice] SAPI recogniser ready (culture={Culture}, grammar={Grammar})",
                    cultureInfo.Name, grammar.Name);
                UpdateStatus(VoiceState.Idle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Failed to initialise speech recogniser");
                UpdateStatus(VoiceState.Error, error: ex.Message);
            }
        }

        private void DisposeEngine()
        {
            lock (_engineLock)
            {
                try
                {
                    _engine?.RecognizeAsyncCancel();
                    DisposeMicStream();          // stop/release any captured mic
                    _engine?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Voice] Engine dispose threw (non-fatal)");
                }
                _engine = null;
                _audioWired = false;
            }
        }

        // Reject any recognition below this confidence. SAPI in command-and-
        // control mode will happily mis-fit arbitrary audio to the closest
        // grammar rule -- e.g. "nudge up" gets mapped to "mode U S B" --
        // which would silently fire a destructive CAT command. The default
        // confidence threshold is very permissive; we want commands that
        // change radio state to require a clear match. 0.6 is the
        // conservative starting point (SAPI confidences run 0.0 - 1.0 and a
        // good match typically sits at 0.85+). Tune downward if too many
        // legitimate phrases get rejected, upward if more misfits slip
        // through.
        //
        // Lowered 0.6 -> 0.5 for parity with IWC: on a quiet mic even correctly-
        // heard commands land in the 0.5-0.6 band (a starved signal depresses
        // SAPI's confidence), so 0.6 rejected good commands. The AGC boost above
        // is the primary lift; 0.5 catches the ones that remain borderline.
        private const float MinConfidence = 0.5f;

        private async void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            string heard = e.Result.Text;
            float confidence = e.Result.Confidence;

            if (confidence < MinConfidence)
            {
                _logger.LogInformation(
                    "[Voice] Low-confidence match ({Conf:F2}) for '{Heard}' -- ignoring",
                    confidence, heard);
                UpdateStatus(VoiceState.Unrecognised, heard: heard, confidence: confidence);
                return;
            }

            string? intent = TryGetSemanticString(e.Result.Semantics, "intent");

            if (intent == null)
            {
                _logger.LogInformation("[Voice] Heard '{Heard}' (no intent tag)", heard);
                UpdateStatus(VoiceState.Heard, heard: heard, confidence: confidence);
                return;
            }

            // Flatten semantic parameters (everything other than the intent
            // name itself) into a string-keyed dictionary for the dispatcher.
            var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in e.Result.Semantics)
            {
                if (string.Equals(key.Key, "intent", StringComparison.OrdinalIgnoreCase))
                    continue;
                args[key.Key] = key.Value?.Value ?? string.Empty;
            }

            // Normalise grammar-specific intents to the dispatcher's API.
            // The programmatic grammar can't compute hz from mhz/frac digits
            // at recognition time, and can't attach two semantic keys to a
            // single NudgeUp/NudgeDown phrase, so we patch those up here:
            //   * SetFrequency: derive args["hz"] from mhz_whole and the
            //     fractional digit words parsed out of `heard` text.
            //   * NudgeUp/NudgeDown: rewrite to intent="NudgeFrequency"
            //     with args["direction"] = ±1
            (intent, args) = NormaliseIntent(intent, args, heard);

            UpdateStatus(VoiceState.Heard, heard: heard, intent: intent, confidence: confidence);
            UpdateStatus(VoiceState.Executing, heard: heard, intent: intent, confidence: confidence);
            try
            {
                var result = await _intentDispatcher.DispatchAsync(intent, args, dryRun: _dryRun, vfo: _targetVfo);
                UpdateStatus(result.Success ? VoiceState.Idle : VoiceState.Error,
                             heard: heard, intent: intent, confidence: confidence,
                             error: result.Success ? null : "Command did not complete");

                // Spoken confirmation via TTS, if enabled in Settings. Phrase
                // template is "<intent description>, successful/unsuccessful"
                // so a listener hears the whole command echoed back along
                // with the outcome -- key for accessibility (Yuri W4YSW,
                // Thomas OZ1JTE) where the operator may not be watching the
                // screen for visual confirmation. In a dry run (§6.5) nothing
                // was actually sent, so the confirmation says so instead of
                // claiming success/failure it didn't earn.
                var settings = await _settings.GetSettingsAsync();
                if (!string.IsNullOrWhiteSpace(result.ConfirmationPhrase) &&
                    (settings.VoiceSpokenConfirmationEnabled || result.IsReadBack))
                {
                    // Read-back responses (status queries, help) speak the phrase directly.
                    // Command confirmations append ", successful" / ", unsuccessful".
                    var speech = result.IsReadBack
                        ? result.ConfirmationPhrase
                        : _dryRun
                            ? $"{result.ConfirmationPhrase}, dry run, not sent"
                            : $"{result.ConfirmationPhrase}, {(result.Success ? "successful" : "unsuccessful")}";
                    _tts.Speak(speech);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Intent dispatch failed for '{Intent}'", intent);
                UpdateStatus(VoiceState.Error, heard: heard, intent: intent, confidence: confidence, error: ex.Message);
            }
        }

        private static (string intent, Dictionary<string, object> args) NormaliseIntent(
            string intent, Dictionary<string, object> args, string heard)
        {
            if (string.Equals(intent, "SetFrequency", StringComparison.Ordinal))
            {
                long mhz = TryGetLong(args, "mhz_whole");
                args.Remove("mhz_whole");
                long fracHz = ParseFractionalHzFromText(heard);
                args["hz"] = mhz * 1_000_000 + fracHz;
                return (intent, args);
            }
            if (string.Equals(intent, "NudgeUp", StringComparison.Ordinal))
            {
                args["direction"] = 1L;
                return ("NudgeFrequency", args);
            }
            if (string.Equals(intent, "NudgeDown", StringComparison.Ordinal))
            {
                args["direction"] = -1L;
                return ("NudgeFrequency", args);
            }
            if (string.Equals(intent, "NudgeIfWidthUp", StringComparison.Ordinal))
            {
                args["direction"] = 1L;
                return ("NudgeIfWidth", args);
            }
            if (string.Equals(intent, "NudgeIfWidthDown", StringComparison.Ordinal))
            {
                args["direction"] = -1L;
                return ("NudgeIfWidth", args);
            }
            if (intent.StartsWith("SetAfGain:", StringComparison.Ordinal))
            {
                if (int.TryParse(intent["SetAfGain:".Length..], out var pct))
                    args["level"] = pct;
                return ("SetAfGain", args);
            }
            // Flat phrase-map intents: "SetMode:USB", "SetBand:80"
            if (intent.StartsWith("SetMode:", StringComparison.Ordinal))
            {
                args["mode"] = intent["SetMode:".Length..];
                return ("SetMode", args);
            }
            if (intent.StartsWith("SetBand:", StringComparison.Ordinal))
            {
                if (long.TryParse(intent.AsSpan("SetBand:".Length), out var metres))
                    args["metres"] = metres;
                return ("SetBand", args);
            }
            if (intent.StartsWith("SetNudgeStep:", StringComparison.Ordinal))
            {
                if (long.TryParse(intent.AsSpan("SetNudgeStep:".Length), out var step))
                    args["step"] = step;
                return ("SetNudgeStep", args);
            }
            if (intent.StartsWith("SetAttenuator:", StringComparison.Ordinal))
            {
                args["level"] = intent["SetAttenuator:".Length..];
                return ("SetAttenuator", args);
            }
            if (intent.StartsWith("SetPreamp:", StringComparison.Ordinal))
            {
                args["level"] = intent["SetPreamp:".Length..];
                return ("SetPreamp", args);
            }
            if (intent.StartsWith("SetAgc:", StringComparison.Ordinal))
            {
                args["speed"] = intent["SetAgc:".Length..];
                return ("SetAgc", args);
            }
            if (intent.StartsWith("Macro:", StringComparison.Ordinal))
            {
                var payload = intent["Macro:".Length..];
                var pipe = payload.IndexOf('|');
                if (pipe >= 0)
                {
                    args["macroName"] = payload[..pipe];
                    args["macroCat"]  = payload[(pipe + 1)..];
                }
                else
                {
                    args["macroName"] = payload;
                }
                return ("Macro", args);
            }
            return (intent, args);
        }

        // Maps digit-word -> Hz contribution by fractional position, most-
        // significant first. "fourteen point zero seven four" -> tokens after
        // "point": zero, seven, four.
        //   zero * 100_000 + seven * 10_000 + four * 1_000 = 74_000 Hz fractional.
        // Up to six fractional digits are read, so "one two three four five six"
        // resolves to 123_456 Hz — full 1 Hz voice tuning.
        private static readonly Dictionary<string, int> _digitWords =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["zero"]  = 0, ["oh"]    = 0,
                ["one"]   = 1, ["two"]   = 2, ["three"] = 3, ["four"]  = 4,
                ["five"]  = 5, ["six"]   = 6, ["seven"] = 7, ["eight"] = 8,
                ["nine"]  = 9,
            };

        private static long ParseFractionalHzFromText(string heard)
        {
            if (string.IsNullOrEmpty(heard)) return 0;
            var tokens = heard.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int pointIdx = Array.FindIndex(tokens,
                t => string.Equals(t, "point", StringComparison.OrdinalIgnoreCase));
            if (pointIdx < 0) return 0;

            long fracHz = 0;
            long multiplier = 100_000; // first frac digit = hundreds of kHz
            // multiplier >= 1 lets the loop consume up to six fractional digits
            // (100_000 .. 1 Hz); it then divides to 0 and the guard stops it.
            for (int i = pointIdx + 1; i < tokens.Length && multiplier >= 1; i++)
            {
                if (!_digitWords.TryGetValue(tokens[i], out var d)) break; // hit "megahertz" or other non-digit
                fracHz += d * multiplier;
                multiplier /= 10;
            }
            return fracHz;
        }

        private static long TryGetLong(IReadOnlyDictionary<string, object> args, string key)
            => args.TryGetValue(key, out var v) ? ConvertToLong(v) : 0L;

        private static long ConvertToLong(object? v)
        {
            try { return v == null ? 0L : Convert.ToInt64(v); }
            catch { return 0L; }
        }

        private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
        {
            // SAPI heard SOMETHING but it didn't match any grammar rule.
            // Useful diagnostic — surface the best alternative to the user.
            string? best = e.Result?.Alternates?.FirstOrDefault()?.Text;
            _logger.LogInformation("[Voice] Rejected (best alt: '{Best}')", best ?? "<none>");
            UpdateStatus(VoiceState.Unrecognised, heard: best);
        }

        private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
        {
            // Fires when RecognizeAsyncStop() finishes draining. Reset to Idle
            // so the UI button settles back to grey.
            if (_status.State == VoiceState.Listening)
                UpdateStatus(VoiceState.Idle);
        }

        private static string? TryGetSemanticString(SemanticValue? semantics, string key)
        {
            if (semantics == null) return null;
            if (semantics.ContainsKey(key))
                return semantics[key]?.Value?.ToString();
            return null;
        }

        private void UpdateStatus(
            VoiceState state,
            string? heard = null,
            string? intent = null,
            string? error = null,
            float? confidence = null)
        {
            // Preserve previous LastHeard/LastIntent/Confidence unless the
            // caller passes something new, so transient states
            // (Heard -> Executing -> Idle) keep showing the most recent phrase.
            var prev = _status;
            var update = new VoiceStatusUpdate(
                state,
                heard ?? prev.LastHeard,
                intent ?? prev.LastIntent,
                error,
                confidence ?? prev.Confidence,
                _dryRun,
                _targetVfo
            );
            _status = update;

            // Fire-and-forget SignalR broadcast — clients react asynchronously.
            // Failure to broadcast doesn't block voice processing.
            try
            {
                _hubContext.Clients.All.SendAsync("VoiceStatusUpdate", update);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Voice] SignalR broadcast failed (non-fatal)");
            }
        }
    }
}
