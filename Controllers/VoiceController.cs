using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Yaesu_Web_Control.Models;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Voice;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// HTTP entry points for the on-screen mic button. The frontend POSTs
    /// /api/voice/start on mousedown and /api/voice/stop on mouseup; status
    /// updates flow back over SignalR (the <c>VoiceStatusUpdate</c> event)
    /// rather than HTTP, so the button can react to mid-recognition state
    /// changes without polling.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public sealed class VoiceController : ControllerBase
    {
        private readonly VoiceControlService _voice;
        private readonly VoicePhraseStore _phraseStore;
        private readonly ISettingsService _settings;
        private readonly ILogger<VoiceController> _logger;

        public VoiceController(VoiceControlService voice, VoicePhraseStore phraseStore, ISettingsService settings, ILogger<VoiceController> logger)
        {
            _voice = voice;
            _phraseStore = phraseStore;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// <paramref name="dryRun"/> (§6.5) is set by the Settings "Test this
        /// pack" modal's dry-run toggle — recognition and intent matching run
        /// normally but no CAT command is sent. The navbar mic button always
        /// calls this with dryRun=false (the default). <paramref name="vfo"/>
        /// ("A" or "B") is which VFO's mic button on the Index page was
        /// pressed; forwarded through to IntentDispatcher so the recognised
        /// command lands on the right receiver.
        /// </summary>
        [HttpPost("start")]
        public async Task<IActionResult> Start([FromQuery] bool dryRun = false, [FromQuery] string vfo = "A")
        {
            var ok = await _voice.StartListeningAsync(dryRun, vfo);
            return ok
                ? Ok(_voice.CurrentStatus)
                : StatusCode(503, _voice.CurrentStatus);
        }

        [HttpPost("stop")]
        public async Task<IActionResult> Stop()
        {
            await _voice.StopListeningAsync();
            return Ok(_voice.CurrentStatus);
        }

        [HttpGet("status")]
        public IActionResult Status() => Ok(_voice.CurrentStatus);

        /// <summary>
        /// Recording-device discovery for the Settings microphone picker. Lists
        /// the WaveIn devices Windows exposes right now plus which one is
        /// currently selected (empty = Windows default). System.Speech can't
        /// target a device by name, so YWC captures the chosen one itself — see
        /// MicrophoneCapture / VoiceControlService.ApplyInputDevice.
        /// </summary>
        [HttpGet("microphones")]
        public async Task<IActionResult> GetMicrophones()
        {
            var selected = (await _settings.GetSettingsAsync()).VoiceInputDeviceName ?? "";
            var devices = MicrophoneCapture.ListInputDevices()
                .Select(d => new
                {
                    name = d.Name,
                    isSelected = string.Equals(d.Name, selected, StringComparison.OrdinalIgnoreCase),
                    present = true,
                })
                .ToArray();

            // If the saved device isn't in the live list (unplugged), still
            // report it so the picker can show it selected-but-missing rather
            // than silently reverting to the default in the UI.
            bool selectedPresent = string.IsNullOrEmpty(selected) ||
                devices.Any(d => d.isSelected);

            return Ok(new
            {
                devices,
                selected,
                selectedPresent,
                usingDefault = string.IsNullOrEmpty(selected),
            });
        }

        public record SetMicrophoneRequest(string? Name);

        /// <summary>
        /// Persists the chosen recording device and rebinds the live SAPI
        /// engine to it — no restart needed. An empty/blank name selects the
        /// Windows default device.
        /// </summary>
        [HttpPost("microphone")]
        public async Task<IActionResult> SetMicrophone([FromBody] SetMicrophoneRequest request)
        {
            var name = request?.Name?.Trim() ?? "";
            var settings = await _settings.GetSettingsAsync();
            settings.VoiceInputDeviceName = name;
            await _settings.SaveSettingsAsync(settings);

            _voice.ApplyInputDevice(string.IsNullOrEmpty(name) ? null : name);
            _logger.LogInformation("[Voice] Microphone set to {Name}", string.IsNullOrEmpty(name) ? "(Windows default)" : name);
            return Ok(new { ok = true, name });
        }

        /// <summary>
        /// Playback-device discovery for the Settings speaker picker. Lists the
        /// WaveOut devices Windows exposes right now plus which one is currently
        /// selected (empty = Windows default). System.Speech can't target an
        /// output device by name, so YWC renders the confirmation and plays it to
        /// the chosen device itself — see AudioOutput / VoiceTtsService.
        /// </summary>
        [HttpGet("speakers")]
        public async Task<IActionResult> GetSpeakers()
        {
            var selected = (await _settings.GetSettingsAsync()).VoiceOutputDeviceName ?? "";
            var devices = AudioOutput.ListOutputDevices()
                .Select(d => new
                {
                    name = d.Name,
                    isSelected = string.Equals(d.Name, selected, StringComparison.OrdinalIgnoreCase),
                    present = true,
                })
                .ToArray();

            bool selectedPresent = string.IsNullOrEmpty(selected) ||
                devices.Any(d => d.isSelected);

            return Ok(new
            {
                devices,
                selected,
                selectedPresent,
                usingDefault = string.IsNullOrEmpty(selected),
            });
        }

        public record SetSpeakerRequest(string? Name);

        /// <summary>
        /// Persists the chosen playback device for spoken confirmations and
        /// applies it live — no restart needed. An empty/blank name selects the
        /// Windows default device.
        /// </summary>
        [HttpPost("speaker")]
        public async Task<IActionResult> SetSpeaker([FromBody] SetSpeakerRequest request)
        {
            var name = request?.Name?.Trim() ?? "";
            var settings = await _settings.GetSettingsAsync();
            settings.VoiceOutputDeviceName = name;
            await _settings.SaveSettingsAsync(settings);

            _voice.ApplyOutputDevice(string.IsNullOrEmpty(name) ? null : name);
            _logger.LogInformation("[Voice] Speaker set to {Name}", string.IsNullOrEmpty(name) ? "(Windows default)" : name);
            return Ok(new { ok = true, name });
        }

        /// <summary>
        /// Speak a fixed test phrase through the currently-selected speaker so
        /// the operator can confirm they'll actually hear confirmations. Used by
        /// the "Test" button next to the Settings speaker picker.
        /// </summary>
        [HttpPost("speaker-test")]
        public IActionResult TestSpeaker()
        {
            _voice.TestSpeak("Voice confirmation test. If you can hear this, announcements will play on this device.");
            return Ok(new { ok = true });
        }

        public record TryPhraseRequest(List<string> Phrases);

        /// <summary>
        /// §2.5 per-row "Try it": tests a single command's phrase list in
        /// isolation, without saving anything. Listens for up to 3 seconds
        /// and reports what SAPI heard and whether it matched one of the
        /// supplied phrases.
        /// </summary>
        [HttpPost("try-phrase")]
        public async Task<IActionResult> TryPhrase([FromBody] TryPhraseRequest request)
        {
            if (request?.Phrases == null || request.Phrases.Count == 0)
                return BadRequest(new { error = "No phrases to test." });

            var (matched, heard, confidence, error) = await _voice.TryPhraseAsync(request.Phrases, TimeSpan.FromSeconds(3));
            if (error != null)
                return StatusCode(503, new { error });

            return Ok(new { matched, heard, confidence });
        }

        /// <summary>
        /// §6.6 diagnostics panel: active locale, its installed pack version,
        /// whether Windows actually has a matching SAPI recogniser, and the
        /// confidence score of the last recognition (MinConfidence=0.6f is
        /// enforced in VoiceControlService but was previously never shown).
        /// </summary>
        [HttpGet("diagnostics")]
        public IActionResult Diagnostics()
        {
            var culture = _voice.ActiveCulture;
            var meta = _phraseStore.LoadMetadata(culture);
            bool hasRecognizer;
            try
            {
                hasRecognizer = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers()
                    .Any(r => string.Equals(r.Culture.Name, culture, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                hasRecognizer = false;
            }

            var status = _voice.CurrentStatus;
            return Ok(new
            {
                activeCulture = culture,
                packVersion = meta?.Version,
                hasWindowsRecognizer = hasRecognizer,
                engineState = status.State.ToString(),
                lastHeard = status.LastHeard,
                lastIntent = status.LastIntent,
                lastConfidence = status.Confidence,
                minConfidence = 0.6f,
            });
        }

        /// <summary>
        /// Opens the user grammars folder in Windows Explorer. Used by the
        /// "Open user grammars folder" button in Settings -> Voice Control.
        /// The folder is created if missing so the user lands in a real
        /// location even on a fresh install.
        /// </summary>
        [HttpPost("open-grammars-folder")]
        public IActionResult OpenGrammarsFolder()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var path = Path.Combine(appData, "MM5AGM", "Yaesu Web Control", "Grammars");
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
                return Ok(new { path });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Failed to open grammars folder");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Extracts voice-related log lines from today's YWC log file. Used by
        /// the "Voice Control Log" panel on the Diagnostics page. A bug
        /// reporter clicks the panel, copies the output, pastes it into a
        /// GitHub issue -- without ever having to know the log file lives at
        /// %APPDATA%\MM5AGM\Yaesu Web Control\logs\ywc-YYYYMMDD.log or that
        /// they need to grep it. The full log can grow to many MB; this
        /// endpoint reads only the tail and filters server-side so the
        /// reporter sees a focused, copy-pastable list.
        ///
        /// `lines` query param caps the returned count (default 200, max 2000).
        /// Patterns matched: lines containing "[Voice]" or "[IntentDispatcher]".
        /// Lines are returned newest-last to read like a normal log file.
        /// </summary>
        private static readonly long[] _validNudgeSteps = [10, 100, 1_000, 10_000, 100_000];

        /// <summary>
        /// Updates the voice nudge step size without a full Settings round-trip.
        /// Called by each VFO's step-size selector on the Index page on change.
        /// </summary>
        [HttpPost("nudge-step")]
        public async Task<IActionResult> SetNudgeStep([FromBody] NudgeStepRequest request)
        {
            if (!_validNudgeSteps.Contains(request.StepHz))
                return BadRequest(new { error = "Invalid step size." });

            var settings = await _settings.GetSettingsAsync();
            var isB = string.Equals(request.Vfo, "B", StringComparison.OrdinalIgnoreCase);
            if (isB) settings.VoiceNudgeStepHzB = request.StepHz;
            else     settings.VoiceNudgeStepHzA = request.StepHz;
            await _settings.SaveSettingsAsync(settings);
            _logger.LogInformation("[Voice] Nudge step updated to {Step} Hz via API (VFO {Vfo})", request.StepHz, isB ? "B" : "A");
            return Ok(new { ok = true });
        }

        public record NudgeStepRequest(long StepHz, string Vfo = "A");

        /// <summary>
        /// Language discovery for the Settings switcher (§4.1): every
        /// installed pack (Grammars\&lt;culture&gt;\ on disk, plus en-GB which is
        /// always selectable via the built-in defaults even before its first
        /// save), cross-referenced against the Windows SAPI recognisers
        /// actually available on this machine, plus which one is active.
        /// </summary>
        [HttpGet("locales")]
        public async Task<IActionResult> GetLocales()
        {
            var settings = await _settings.GetSettingsAsync();
            var activeLocale = string.IsNullOrWhiteSpace(settings.VoiceActiveLocale)
                ? VoicePhraseStore.DefaultCulture
                : settings.VoiceActiveLocale;

            var cultures = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { VoicePhraseStore.DefaultCulture };
            foreach (var c in _phraseStore.ListInstalledCultures()) cultures.Add(c);
            cultures.Add(activeLocale); // in case Settings points at a folder that's since been removed

            HashSet<string> recognizerCultures;
            try
            {
                recognizerCultures = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers()
                    .Select(r => r.Culture.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                recognizerCultures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var locales = cultures.Select(culture =>
            {
                string displayName;
                try { displayName = new System.Globalization.CultureInfo(culture).NativeName; }
                catch { displayName = culture; }

                return new
                {
                    culture,
                    displayName,
                    builtIn = string.Equals(culture, VoicePhraseStore.DefaultCulture, StringComparison.OrdinalIgnoreCase),
                    installed = _phraseStore.IsInstalled(culture) || string.Equals(culture, VoicePhraseStore.DefaultCulture, StringComparison.OrdinalIgnoreCase),
                    hasWindowsRecognizer = recognizerCultures.Contains(culture),
                    isActive = string.Equals(culture, activeLocale, StringComparison.OrdinalIgnoreCase),
                };
            }).ToArray();

            return Ok(new { locales, activeLocale });
        }

        public record SwitchLocaleRequest(string Culture);

        /// <summary>
        /// Switches the active recognition locale (§4.2). Persists the choice
        /// and hot-swaps the live SAPI engine — no restart needed. Succeeds
        /// (200) even if Windows has no recogniser for the target locale
        /// installed; <c>hasWindowsRecognizer: false</c> in the response
        /// tells the UI to show the mismatch banner (§4.5) rather than
        /// treating the switch itself as failed.
        /// </summary>
        [HttpPost("locale")]
        public async Task<IActionResult> SwitchLocale([FromBody] SwitchLocaleRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Culture))
                return BadRequest(new { error = "No culture supplied." });

            var engineReady = await _voice.SwitchLocaleAsync(request.Culture);
            _logger.LogInformation("[Voice] Active locale switched to {Culture} (engineReady={Ready})", request.Culture, engineReady);

            bool hasRecognizer;
            try
            {
                hasRecognizer = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers()
                    .Any(r => string.Equals(r.Culture.Name, request.Culture, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                hasRecognizer = false;
            }

            return Ok(new { ok = true, culture = request.Culture, hasWindowsRecognizer = hasRecognizer, status = _voice.CurrentStatus });
        }

        /// <summary>Returns the current voice phrases configuration (user file or built-in defaults).</summary>
        [HttpGet("phrases")]
        public IActionResult GetPhrases()
        {
            var config = _phraseStore.Load();
            return Ok(config);
        }

        /// <summary>
        /// Display-ready, grouped list of available voice commands for the
        /// right-click mic-button help popup. Generated from the live phrase
        /// config for the active locale, so it always matches what recognition
        /// actually accepts (including any user-customised phrases).
        /// </summary>
        [HttpGet("help")]
        public IActionResult GetHelp()
        {
            var culture = _voice.ActiveCulture;
            var config = _phraseStore.Load(culture);
            return Ok(VoiceHelpBuilder.Build(config, culture));
        }

        /// <summary>
        /// Validates a voice phrases configuration without saving it. Used by
        /// the Settings editor's "Validate" button and by SavePhrases below.
        /// </summary>
        [HttpPost("phrases/validate")]
        public async Task<IActionResult> ValidatePhrases([FromBody] VoicePhrasesConfig config)
        {
            if (config == null)
                return BadRequest(new { error = "No configuration supplied." });

            var advancedMode = (await _settings.GetSettingsAsync()).VoiceAdvancedModeEnabled;
            var issues = VoicePhraseValidator.Validate(config, advancedMode);
            return Ok(new
            {
                errors = issues.Where(i => i.Severity == ValidationSeverity.Error)
                    .Select(i => new { i.Path, i.Message }),
                warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning)
                    .Select(i => new { i.Path, i.Message }),
            });
        }

        /// <summary>
        /// Saves a new voice phrases configuration and hot-reloads the SAPI
        /// grammar so changes take effect immediately without an app restart.
        /// Validates first — a config with blocking errors (e.g. a macro
        /// with no CAT string) is rejected with 422 and never written to
        /// disk; warnings don't block the save but are returned alongside
        /// the success response so the UI can still show them.
        /// </summary>
        [HttpPost("phrases")]
        public async Task<IActionResult> SavePhrases([FromBody] VoicePhrasesConfig config)
        {
            if (config == null)
                return BadRequest(new { error = "No configuration supplied." });

            var advancedMode = (await _settings.GetSettingsAsync()).VoiceAdvancedModeEnabled;
            var issues = VoicePhraseValidator.Validate(config, advancedMode);
            var errors = issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                _logger.LogWarning("[Voice] Rejected phrases save — {Count} validation error(s)", errors.Count);
                return UnprocessableEntity(new
                {
                    error = "Fix the errors below before saving.",
                    errors = errors.Select(i => new { i.Path, i.Message }),
                });
            }

            try
            {
                _phraseStore.Save(config);
                // A real save supersedes any in-progress draft (§2.5).
                _phraseStore.ClearDraft();
                // The editor only ever edits en-GB (per-locale editing is a
                // later phase) -- only hot-reload the live engine if en-GB is
                // actually what's loaded, otherwise this would reload
                // whatever locale IS active with no relation to what was
                // just saved.
                if (string.Equals(_voice.ActiveCulture, VoicePhraseStore.DefaultCulture, StringComparison.OrdinalIgnoreCase))
                    _voice.ReloadGrammar();
                _logger.LogInformation("[Voice] Phrases saved and grammar reloaded.");
                var warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning)
                    .Select(i => new { i.Path, i.Message });
                return Ok(new { ok = true, warnings });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Failed to save phrases");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Returns the built-in default phrases so the UI can offer a Reset button.</summary>
        [HttpGet("phrases/defaults")]
        public IActionResult GetDefaultPhrases()
        {
            return Ok(VoicePhraseStore.BuildDefaults());
        }

        /// <summary>
        /// §2.5 autosave: the editor POSTs here on every change (debounced
        /// client-side). Never validated or hot-reloaded -- it's a crash/
        /// refresh safety net, not a save.
        /// </summary>
        [HttpPost("phrases/draft")]
        public IActionResult SaveDraft([FromBody] VoicePhrasesConfig config, [FromQuery] string culture = VoicePhraseStore.DefaultCulture)
        {
            if (config == null) return BadRequest(new { error = "No configuration supplied." });
            _phraseStore.SaveDraft(config, culture);
            return Ok(new { ok = true });
        }

        /// <summary>Returns the pending draft, if any, so the editor can offer to restore it on load.</summary>
        [HttpGet("phrases/draft")]
        public IActionResult GetDraft([FromQuery] string culture = VoicePhraseStore.DefaultCulture)
        {
            var draft = _phraseStore.LoadDraft(culture);
            return draft == null ? Ok(new { hasDraft = false }) : Ok(new { hasDraft = true, config = draft });
        }

        /// <summary>Discards the pending draft (user chose "discard" over "restore").</summary>
        [HttpPost("phrases/draft/discard")]
        public IActionResult DiscardDraft([FromQuery] string culture = VoicePhraseStore.DefaultCulture)
        {
            _phraseStore.ClearDraft(culture);
            return Ok(new { ok = true });
        }

        /// <summary>Returns the pack metadata (author/version/description) for the Language Pack panel in Settings.</summary>
        [HttpGet("phrases/metadata")]
        public IActionResult GetMetadata([FromQuery] string culture = VoicePhraseStore.DefaultCulture)
        {
            var meta = _phraseStore.LoadMetadata(culture) ?? new VoicePackMetadata { Locale = culture };
            return Ok(meta);
        }

        public record UpdateMetadataRequest(string Author, string Description);

        /// <summary>Updates the author/description fields, remembered locally so Export doesn't need retyping.</summary>
        [HttpPost("phrases/metadata")]
        public IActionResult SaveMetadata([FromBody] UpdateMetadataRequest request, [FromQuery] string culture = VoicePhraseStore.DefaultCulture)
        {
            var meta = _phraseStore.LoadMetadata(culture) ?? new VoicePackMetadata { Locale = culture };
            meta.Author = request.Author ?? "";
            meta.Description = request.Description ?? "";
            meta.Locale = culture;
            meta.DateModified = DateTimeOffset.UtcNow;
            _phraseStore.SaveMetadata(meta, culture);
            return Ok(meta);
        }

        /// <summary>
        /// Bundles the current phrases, a freshly-regenerated SRGS reference
        /// copy, and metadata into a YWC-VoicePack-&lt;culture&gt;-v&lt;version&gt;.zip
        /// for the user to share or attach to a GitHub Discussion post — see
        /// docs/VoiceControl/language-pack-manager-design.md §3.1. Exporting
        /// increments the stored pack version and re-saves the installed
        /// copy so the on-disk files never disagree with what was exported.
        /// </summary>
        [HttpGet("phrases/export")]
        public IActionResult ExportPhrases([FromQuery] string culture = VoicePhraseStore.DefaultCulture)
        {
            try
            {
                var config = _phraseStore.Load(culture);
                _phraseStore.Save(config, culture);

                var meta = _phraseStore.LoadMetadata(culture) ?? new VoicePackMetadata { Locale = culture };
                meta.Locale = culture;
                meta.Version += 1;
                meta.DateModified = DateTimeOffset.UtcNow;
                _phraseStore.SaveMetadata(meta, culture);

                var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(config, jsonOpts);
                var srgs = VoiceGrammar.GenerateSrgs(config, culture);
                var metaJson = JsonSerializer.Serialize(meta, jsonOpts);

                using var ms = new MemoryStream();
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteZipEntry(zip, $"Commands.{culture}.json", json);
                    WriteZipEntry(zip, $"Commands.{culture}.srgs", srgs);
                    WriteZipEntry(zip, $"Commands.{culture}.meta.json", metaJson);
                }

                _logger.LogInformation("[Voice] Exported language pack {Culture} v{Version}", culture, meta.Version);
                return File(ms.ToArray(), "application/zip", $"YWC-VoicePack-{culture}-v{meta.Version}.zip");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Failed to export language pack");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private static void WriteZipEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }

        // ── Version history (§3.3) ──────────────────────────────────────
        // VoicePhraseStore.Save() snapshots the pre-overwrite copy of a pack
        // on every save/import, capped at 5 per locale. Restoring is not a
        // special code path -- it re-runs the same validate -> Save pipeline
        // as installing an imported ZIP, just sourced from a local snapshot
        // instead of an upload.

        /// <summary>Newest-first list of retained snapshots for a locale, for the Settings.cshtml "Version history" panel.</summary>
        [HttpGet("phrases/history")]
        public IActionResult GetHistory([FromQuery] string culture = VoicePhraseStore.DefaultCulture)
        {
            var snapshots = _phraseStore.ListHistory(culture)
                .Select(s => new
                {
                    snapshotId = s.SnapshotId,
                    author = s.Meta?.Author,
                    description = s.Meta?.Description,
                    version = s.Meta?.Version,
                    dateModified = s.Meta?.DateModified,
                });
            return Ok(snapshots);
        }

        public record RestoreHistoryRequest(string Culture, string SnapshotId);

        /// <summary>
        /// Restores a locale to a previously-snapshotted state. Re-validates
        /// the archived config against current rules (a snapshot saved before
        /// a validator rule tightened could otherwise reintroduce something
        /// now rejected) before writing. The snapshot's own metadata
        /// (author/description/version) is reinstated as-is -- a restore is
        /// not a new edit, so it must not bump the version number.
        /// </summary>
        [HttpPost("phrases/history/restore")]
        public async Task<IActionResult> RestoreHistory([FromBody] RestoreHistoryRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Culture) || string.IsNullOrWhiteSpace(request.SnapshotId))
                return BadRequest(new { error = "Culture and snapshotId are required." });

            var config = _phraseStore.LoadHistorySnapshot(request.Culture, request.SnapshotId);
            if (config == null)
                return NotFound(new { error = "That history snapshot no longer exists." });

            // Must be read before Save() below -- Save() snapshots the
            // pre-restore state and prunes to 5, and if this snapshot is
            // currently the oldest of the 5, that same prune would delete
            // the very folder we're restoring from.
            var snapshotMeta = _phraseStore.LoadHistorySnapshotMetadata(request.Culture, request.SnapshotId)
                ?? new VoicePackMetadata { Locale = request.Culture };

            var advancedMode = (await _settings.GetSettingsAsync()).VoiceAdvancedModeEnabled;
            var issues = VoicePhraseValidator.Validate(config, advancedMode);
            var errors = issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                _logger.LogWarning("[Voice] Rejected history restore for {Culture}/{Snapshot} — {Count} validation error(s)",
                    request.Culture, request.SnapshotId, errors.Count);
                return UnprocessableEntity(new
                {
                    error = "This snapshot no longer validates against current rules — fix the errors below or pick a different version.",
                    errors = errors.Select(i => new { i.Path, i.Message }),
                });
            }

            try
            {
                // Save() snapshots whatever is currently installed before
                // overwriting it, so restoring is itself undoable.
                _phraseStore.Save(config, request.Culture);

                snapshotMeta.Locale = request.Culture;
                _phraseStore.SaveMetadata(snapshotMeta, request.Culture);

                if (string.Equals(request.Culture, _voice.ActiveCulture, StringComparison.OrdinalIgnoreCase))
                    _voice.ReloadGrammar();

                _logger.LogInformation("[Voice] Restored {Culture} to snapshot {Snapshot}", request.Culture, request.SnapshotId);

                var warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning).Select(i => new { i.Path, i.Message });
                return Ok(new { ok = true, warnings });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Failed to restore history snapshot {Culture}/{Snapshot}", request.Culture, request.SnapshotId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Import (§3.2) ────────────────────────────────────────────────
        // Two-step: /import/preview extracts + validates WITHOUT writing
        // anything to disk, returning a report the client can render before
        // committing. /import/install re-validates (never trust a client-
        // only check) and actually writes the pack. The parsed config
        // travels preview -> install in the request body rather than a
        // server-side session, so there's no temp-file cleanup to get wrong.

        /// <summary>
        /// Stage 1 of import: extracts an uploaded pack ZIP, parses its JSON
        /// (+ optional meta.json/srgs), runs Stage A + Stage B validation
        /// without installing anything, and returns a preview report -- pack
        /// metadata, a per-category phrase-count summary with sample
        /// phrases, and whether a Windows speech recognizer is available for
        /// this locale (§3.2 point 4).
        /// </summary>
        [HttpPost("phrases/import/preview")]
        [RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> PreviewImport(IFormFile pack)
        {
            if (pack == null || pack.Length == 0)
                return BadRequest(new { error = "No file uploaded." });

            string culture;
            VoicePhrasesConfig? config;
            VoicePackMetadata? meta = null;
            string? srgsError = null;
            bool srgsIncluded = false;

            try
            {
                using var zipStream = pack.OpenReadStream();
                using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

                var jsonEntry = zip.Entries.FirstOrDefault(e =>
                    e.Name.StartsWith("Commands.", StringComparison.OrdinalIgnoreCase) &&
                    e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                    !e.Name.Contains(".meta.", StringComparison.OrdinalIgnoreCase));
                if (jsonEntry == null || jsonEntry.Name.Length <= "Commands.".Length + ".json".Length)
                    return UnprocessableEntity(new { error = "ZIP doesn't contain a Commands.<culture>.json file." });

                culture = jsonEntry.Name["Commands.".Length..^".json".Length];

                using (var reader = new StreamReader(jsonEntry.Open()))
                    config = JsonSerializer.Deserialize<VoicePhrasesConfig>(reader.ReadToEnd());

                if (config == null)
                    return UnprocessableEntity(new { error = $"Commands.{culture}.json is not valid JSON." });

                var metaEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase));
                if (metaEntry != null)
                {
                    using var mr = new StreamReader(metaEntry.Open());
                    try { meta = JsonSerializer.Deserialize<VoicePackMetadata>(mr.ReadToEnd()); }
                    catch { meta = null; }
                }

                // Foreign SRGS is never loaded for behaviour (§1.2/§5.2) --
                // this is a structural well-formedness check only, purely to
                // warn the user their reference copy will be regenerated.
                var srgsEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".srgs", StringComparison.OrdinalIgnoreCase));
                if (srgsEntry != null)
                {
                    srgsIncluded = true;
                    try
                    {
                        using var sr = new StreamReader(srgsEntry.Open());
                        System.Xml.Linq.XDocument.Parse(sr.ReadToEnd());
                    }
                    catch (Exception ex)
                    {
                        srgsError = ex.Message;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IOException)
            {
                // ZipArchive throws different exception types depending on
                // how the stream fails to look like a ZIP (InvalidDataException
                // for a recognisable-but-corrupt archive, ArgumentOutOfRangeException
                // when it seeks past the start of stream hunting for an end-of-
                // central-directory record in a file that isn't a ZIP at all).
                return UnprocessableEntity(new { error = "That file isn't a valid ZIP archive." });
            }

            var issues = new List<ValidationIssue>();

            // Locale mismatch is a hard error (§3.3) -- a silently mis-tagged
            // pack would install under the wrong language and quietly break
            // recognition with no obvious cause.
            if (meta != null && !string.IsNullOrWhiteSpace(meta.Locale) &&
                !string.Equals(meta.Locale, culture, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "meta.locale",
                    $"meta.json says locale '{meta.Locale}' but the files are named for '{culture}' — refusing to install under a mismatched locale."));
            }

            if (srgsError != null)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, "srgs",
                    $"Commands.{culture}.srgs isn't well-formed XML ({srgsError}). It will be regenerated from the JSON on install; the original is never used for behaviour."));
            }

            var advancedMode = (await _settings.GetSettingsAsync()).VoiceAdvancedModeEnabled;
            issues.AddRange(VoicePhraseValidator.Validate(config, advancedMode));

            bool hasRecognizer;
            try
            {
                hasRecognizer = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers()
                    .Any(r => string.Equals(r.Culture.Name, culture, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                hasRecognizer = false;
            }

            var alreadyInstalled = _phraseStore.IsInstalled(culture);

            // Custom Command name collisions (§3.4) -- computed up front so
            // the preview can show the "keep mine / take theirs" picker
            // before the user even picks Merge, rather than discovering
            // collisions only after clicking Install.
            object? collisions = null;
            if (alreadyInstalled)
            {
                var existing = _phraseStore.Load(culture);
                var existingByName = existing.Macros.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
                collisions = (config.Macros ?? new())
                    .Where(m => existingByName.ContainsKey(m.Name))
                    .Select(m =>
                    {
                        var mine = existingByName[m.Name];
                        return new
                        {
                            name = m.Name,
                            mine = new { mine.Phrases, mine.Cat, mine.Category },
                            theirs = new { m.Phrases, m.Cat, m.Category },
                        };
                    })
                    .ToArray();
            }

            return Ok(new
            {
                culture,
                meta,
                errors = issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => new { i.Path, i.Message }),
                warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning).Select(i => new { i.Path, i.Message }),
                categories = BuildCategorySummary(config),
                alreadyInstalled,
                collisions,
                hasWindowsRecognizer = hasRecognizer,
                srgsIncluded,
                config,
            });
        }

        public record ImportInstallRequest(string Culture, VoicePhrasesConfig Config, VoicePackMetadata? Meta, string? MergeStrategy, Dictionary<string, string>? CollisionResolutions);

        /// <summary>
        /// Stage 2 of import: re-validates (never trusts the preview's
        /// client-held result) and writes the pack. "Replace" (default)
        /// overwrites the installed pack for this culture outright. "Merge"
        /// keeps every existing Custom Command, adds every imported one that
        /// doesn't collide by name, and for each name collision asks the
        /// caller which side to keep via CollisionResolutions ("mine" or
        /// "theirs") -- an unresolved collision defaults to "mine" so a
        /// command is never silently overwritten (§3.4).
        /// SRGS is always regenerated from the (possibly merged) JSON, never
        /// taken from the imported ZIP verbatim (§1.2, §3.2 point 6).
        /// </summary>
        [HttpPost("phrases/import/install")]
        public async Task<IActionResult> InstallImport([FromBody] ImportInstallRequest request)
        {
            if (request?.Config == null || string.IsNullOrWhiteSpace(request.Culture))
                return BadRequest(new { error = "No configuration supplied." });

            var culture = request.Culture;
            var config = request.Config;
            var collisionsResolved = new List<object>();

            if (string.Equals(request.MergeStrategy, "merge", StringComparison.OrdinalIgnoreCase) && _phraseStore.IsInstalled(culture))
            {
                var existing = _phraseStore.Load(culture);
                var existingByName = existing.Macros.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
                var importedByName = (config.Macros ?? new()).ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
                var resolutions = request.CollisionResolutions ?? new Dictionary<string, string>();

                var merged = new List<MacroDefinition>();
                foreach (var name in existingByName.Keys)
                {
                    if (importedByName.TryGetValue(name, out var theirs))
                    {
                        var takeTheirs = resolutions.TryGetValue(name, out var choice) &&
                                          string.Equals(choice, "theirs", StringComparison.OrdinalIgnoreCase);
                        merged.Add(takeTheirs ? theirs : existingByName[name]);
                        collisionsResolved.Add(new { name, kept = takeTheirs ? "theirs" : "mine" });
                    }
                    else
                    {
                        merged.Add(existingByName[name]);
                    }
                }
                foreach (var name in importedByName.Keys)
                {
                    if (!existingByName.ContainsKey(name))
                        merged.Add(importedByName[name]);
                }
                config.Macros = merged;
            }

            var advancedMode = (await _settings.GetSettingsAsync()).VoiceAdvancedModeEnabled;
            var issues = VoicePhraseValidator.Validate(config, advancedMode);
            var errors = issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                _logger.LogWarning("[Voice] Rejected pack import for {Culture} — {Count} validation error(s)", culture, errors.Count);
                return UnprocessableEntity(new
                {
                    error = "Fix the errors below before installing.",
                    errors = errors.Select(i => new { i.Path, i.Message }),
                });
            }

            try
            {
                _phraseStore.Save(config, culture);

                var meta = request.Meta ?? _phraseStore.LoadMetadata(culture) ?? new VoicePackMetadata { Locale = culture };
                meta.Locale = culture;
                meta.DateModified = DateTimeOffset.UtcNow;
                _phraseStore.SaveMetadata(meta, culture);

                // Only one locale is ever actually loaded into the live
                // engine (§4.3, a platform limitation) -- hot-reload only
                // when the imported culture is the one currently active.
                if (string.Equals(culture, _voice.ActiveCulture, StringComparison.OrdinalIgnoreCase))
                    _voice.ReloadGrammar();

                _logger.LogInformation("[Voice] Imported language pack {Culture} ({Strategy})", culture, request.MergeStrategy ?? "replace");

                var warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning).Select(i => new { i.Path, i.Message });
                return Ok(new { ok = true, warnings, collisionsResolved });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Failed to install imported language pack {Culture}", culture);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Mirrors the CORE_CATEGORIES / SIMPLE_CATEGORY_OF mapping in
        // Settings.cshtml's editor -- kept in sync by hand since one lives in
        // JS (editing) and one in C# (this read-only preview summary).
        private static readonly Dictionary<string, string> SimpleCategoryOf = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SwapVFO"] = "Tuning", ["NudgeUp"] = "Tuning", ["NudgeDown"] = "Tuning",
            ["BandUp"] = "Tuning", ["BandDown"] = "Tuning",
            ["NudgeIfWidthUp"] = "Radio Controls", ["NudgeIfWidthDown"] = "Radio Controls",
            ["TxOn"] = "Transmit", ["TxOff"] = "Transmit", ["SplitOn"] = "Transmit", ["SplitOff"] = "Transmit",
            ["StatusFrequency"] = "Status & Help", ["StatusMode"] = "Status & Help",
            ["StatusBand"] = "Status & Help", ["Help"] = "Status & Help",
        };

        private static object BuildCategorySummary(VoicePhrasesConfig cfg)
        {
            var groups = new Dictionary<string, (int Commands, int Phrases, List<string> Samples)>();

            void Add(string category, IReadOnlyList<string>? phrases)
            {
                if (phrases == null || phrases.Count == 0) return;
                if (!groups.TryGetValue(category, out var g)) g = (0, 0, new List<string>());
                g.Commands++;
                g.Phrases += phrases.Count;
                foreach (var p in phrases)
                {
                    if (g.Samples.Count >= 3) break;
                    if (!string.IsNullOrWhiteSpace(p)) g.Samples.Add(p);
                }
                groups[category] = g;
            }

            foreach (var (key, phrases) in cfg.SimpleCommands ?? new())
                Add(SimpleCategoryOf.GetValueOrDefault(key, "Tuning"), phrases);

            Add("Tuning", cfg.SetBand?.Triggers);
            Add("Tuning", cfg.SetNudgeStep?.Triggers);
            Add("Tuning", cfg.SetFrequency?.Triggers);
            Add("Radio Controls", cfg.SetMode?.Triggers);
            Add("Radio Controls", cfg.SetAfGain?.Triggers);
            Add("Radio Controls", cfg.SetAttenuator?.Triggers);
            Add("Radio Controls", cfg.SetPreamp?.Triggers);
            Add("Radio Controls", cfg.SetAgc?.Triggers);

            foreach (var m in cfg.Macros ?? new())
                Add(string.IsNullOrWhiteSpace(m.Category) ? "Macros" : m.Category, m.Phrases);

            return groups
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new
                {
                    name = kv.Key,
                    commandCount = kv.Value.Commands,
                    phraseCount = kv.Value.Phrases,
                    samplePhrases = kv.Value.Samples,
                })
                .ToArray();
        }

        [HttpGet("log")]
        public IActionResult VoiceLog([FromQuery] int lines = 200)
        {
            if (lines < 1) lines = 1;
            if (lines > 2000) lines = 2000;

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var logDir = Path.Combine(appData, "MM5AGM", "Yaesu Web Control", "logs");
                if (!Directory.Exists(logDir))
                    return Ok(new { lines = Array.Empty<string>(), source = (string?)null, note = "Log folder doesn't exist yet." });

                // Pick today's file, falling back to whatever is newest if the
                // run started on a previous day and hasn't rolled over yet.
                var todayName = $"ywc-{DateTime.Now:yyyyMMdd}.log";
                var todayPath = Path.Combine(logDir, todayName);
                string? sourcePath = System.IO.File.Exists(todayPath)
                    ? todayPath
                    : new DirectoryInfo(logDir)
                        .GetFiles("ywc-*.log")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault()?.FullName;

                if (sourcePath == null)
                    return Ok(new { lines = Array.Empty<string>(), source = (string?)null, note = "No log files found." });

                // Read the tail. 4 MB is generous -- typical voice sessions
                // generate tens of KB of [Voice] lines amongst hundreds of KB
                // of meter polling. Reading the whole file would work but is
                // wasteful on long-running sessions where the log can be 50+ MB.
                const long tailBytes = 4 * 1024 * 1024;
                string content;
                using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > tailBytes)
                        fs.Seek(-tailBytes, SeekOrigin.End);
                    using var reader = new StreamReader(fs);
                    content = reader.ReadToEnd();
                }

                var matched = content
                    .Split('\n')
                    .Where(l => l.Contains("[Voice]") || l.Contains("[IntentDispatcher]"))
                    .Select(l => l.TrimEnd('\r'))
                    .ToList();

                // If we read mid-file, the first matched line might be a
                // partial -- drop it to keep the output clean.
                if (matched.Count > 0 && matched.Count > lines)
                    matched.RemoveAt(0);

                var tail = matched.Count > lines
                    ? matched.GetRange(matched.Count - lines, lines)
                    : matched;

                return Ok(new
                {
                    source = Path.GetFileName(sourcePath),
                    totalMatched = matched.Count,
                    returned = tail.Count,
                    lines = tail,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Failed to extract voice log");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
