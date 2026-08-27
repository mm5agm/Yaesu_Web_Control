using System.ComponentModel;
using System.Text;
using RadioWebControl.Core.Services.Cw;
using Yaesu_Web_Control.Services.Audio;

namespace Yaesu_Web_Control.Services.Cw
{
    /// <summary>
    /// The CW reader as the application sees it: start it, poll it for text.
    ///
    /// This is the piece that has been missing. Core has had a decoder since
    /// the CW reader work began, but nothing in either application ever fed it
    /// - only the CwBench harness and the test suite did, which is why the
    /// filter-width mapping added in CwDecoderOptions had no caller. This
    /// service is that caller.
    ///
    /// What it takes from the radio, rather than guessing:
    ///
    ///   pitch   The operator's own CW pitch (KP), which is the tone they
    ///           tuned the signal onto. Decoding at a pitch the operator is
    ///           not using means hunting for a tone that is not there.
    ///   width   The IF filter width (SH), converted to Hz by YaesuIfWidth
    ///           because the SH code means different things in different
    ///           modes. That sets how far either side of the pitch the
    ///           detector may hunt: a signal outside the passband is one the
    ///           operator cannot hear, so it is not a signal to lock onto.
    ///
    /// When the width is not known - AM or FM, the radio's own default code 0,
    /// an unrecognised model - the decoder keeps its default search window
    /// rather than being handed a guess. Refusing to answer is better than
    /// answering wrongly, and the status this service reports says which
    /// happened so the UI can show it.
    /// </summary>
    public sealed class CwReaderService : IDisposable
    {
        /// <summary>
        /// How much decoded text to keep. Roughly twenty minutes of solid copy
        /// at contest speed - enough to scroll back through an over, not so
        /// much that it grows without limit on a receiver left running.
        /// </summary>
        private const int MaxTextLength = 8000;

        private readonly BridgeCwAudioSource _source;
        private readonly AudioSessionManager _sessions;
        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly ILogger<CwReaderService> _logger;

        private readonly object _gate = new();
        private readonly StringBuilder _text = new();

        private CwDecoderEngine? _engine;
        private string _radioModel = "";
        private long _totalChars;

        // What the engine was built with, so a state change can be compared
        // against it and the engine left alone when nothing that matters moved.
        private double _pitchHz;
        private double _searchWindowHz;
        private int? _filterWidthHz;

        public CwReaderService(
            BridgeCwAudioSource source,
            AudioSessionManager sessions,
            RadioStateService state,
            ISettingsService settings,
            ILogger<CwReaderService> logger)
        {
            _source = source;
            _sessions = sessions;
            _state = state;
            _settings = settings;
            _logger = logger;
        }

        public bool IsRunning { get; private set; }

        public async Task StartAsync(CancellationToken ct = default)
        {
            if (IsRunning) return;

            var settings = await _settings.GetSettingsAsync();
            _radioModel = settings.RadioModel ?? "";

            lock (_gate)
            {
                BuildEngine();
                _state.PropertyChanged += OnRadioStateChanged;
                IsRunning = true;
            }

            await _source.StartAsync(ct);

            _logger.LogInformation(
                "CW reader started: pitch {Pitch} Hz, filter {Filter}, search window +/-{Search} Hz",
                _pitchHz,
                _filterWidthHz is int w ? $"{w} Hz" : "unknown",
                _searchWindowHz);
        }

        public async Task StopAsync(CancellationToken ct = default)
        {
            if (!IsRunning) return;

            lock (_gate)
            {
                _state.PropertyChanged -= OnRadioStateChanged;
                IsRunning = false;
            }

            await _source.StopAsync(ct);

            // Emit the part-built character, so the last letter of the last
            // over is not silently swallowed by stopping.
            lock (_gate)
            {
                string tail = _engine?.Flush() ?? "";
                if (tail.Length > 0) AppendLocked(tail);

                _engine?.Detach();
                _engine = null;
            }

            _logger.LogInformation("CW reader stopped");
        }

        /// <summary>Discard the decoded text, leaving the decoder running.</summary>
        public void ClearText()
        {
            lock (_gate)
            {
                _text.Clear();
                // _totalChars is NOT reset: it is the client's cursor, and
                // rewinding it would make an old cursor look current.
            }
        }

        /// <summary>
        /// Everything the UI needs in one read, including the text decoded
        /// since the caller's cursor.
        /// </summary>
        /// <param name="since">
        /// The caller's cursor from its previous snapshot, or 0 for everything
        /// still held. A cursor older than the retained text returns what is
        /// left, not an error, along with Truncated so the caller can say so.
        /// </param>
        public CwReaderSnapshot Snapshot(long since)
        {
            lock (_gate)
            {
                long oldest = _totalChars - _text.Length;
                bool truncated = since < oldest;
                long from = Math.Max(since, oldest);

                string text = from >= _totalChars
                    ? ""
                    : _text.ToString((int)(from - oldest), (int)(_totalChars - from));

                return new CwReaderSnapshot
                {
                    Running            = IsRunning,
                    AudioSessionActive = _sessions.HasActiveSession,
                    AudioDevicesOpen   = _source.AudioDevicesOpen,
                    Text             = text,
                    Cursor           = _totalChars,
                    Truncated        = truncated,
                    PitchHz          = _pitchHz,
                    FilterWidthHz    = _filterWidthHz,
                    SearchWindowHz   = _searchWindowHz,
                    Mode             = _state.ModeA,
                    WordsPerMinute   = _engine?.WordsPerMinute ?? 0,
                    ToneHz           = _engine?.ToneHz ?? 0,
                    SnrDb            = _engine?.SnrDb ?? 0,
                    SignalPresent    = _engine?.SignalPresent ?? false,
                    IsLocked         = _engine?.IsLocked ?? false,
                    ZeroInOffsetHz   = _engine?.ZeroInOffsetHz(IsLowerSideband(_state.ModeA)),
                    Readability      = (_engine?.Readability ?? CwReadability.Unknown).ToString(),
                    DroppedFrames    = _source.DroppedFrames,
                };
            }
        }

        // ---- engine lifecycle ----------------------------------------------

        /// <summary>Caller holds _gate.</summary>
        private void BuildEngine()
        {
            _pitchHz = PitchHzFromRadio();
            _filterWidthHz = FilterWidthHzFromRadio();

            // The one line this whole exercise was for: the radio's own filter
            // decides how far the detector may hunt.
            _searchWindowHz = _filterWidthHz is int hz
                ? CwDecoderOptions.SearchWindowForFilterWidth(hz)
                : new CwDecoderOptions().SearchWindowHz;

            _engine?.Detach();
            _engine = new CwDecoderEngine(new CwDecoderOptions
            {
                InputSampleRate = _source.SampleRate,
                PitchHz         = _pitchHz,
                SearchWindowHz  = _searchWindowHz,
            });
            _engine.TextDecoded += OnTextDecoded;
            _engine.Attach(_source);
        }

        /// <summary>
        /// KP is a code, 0-75, for 300-1050 Hz in 10 Hz steps.
        /// </summary>
        private double PitchHzFromRadio() => 300.0 + (Math.Clamp(_state.CwPitch, 0, 75) * 10.0);

        /// <summary>
        /// Which way a tuning correction has to go. On CW-L the audio tone
        /// moves opposite to the dial, so the offset the reader suggests has
        /// to be negated or it sends the operator the wrong way. CW-R is the
        /// same reversed sideband under another name.
        /// </summary>
        private static bool IsLowerSideband(string? mode) =>
            mode is "CW-L" or "CW-R";

        /// <summary>
        /// Null when the radio has not said, which is a real answer: code 0 is
        /// the radio's own mode-dependent default and it does not report what
        /// that resolved to, and AM/FM have no IF width at all.
        /// </summary>
        private int? FilterWidthHzFromRadio()
            => YaesuIfWidth.HzForCode(_radioModel, _state.ModeA, _state.IfWidthA);

        private void OnRadioStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Only the three the decoder is built from. Everything else the
            // radio reports changes constantly and must not rebuild anything.
            if (e.PropertyName is not (nameof(RadioStateService.CwPitch)
                                    or nameof(RadioStateService.IfWidthA)
                                    or nameof(RadioStateService.ModeA)))
                return;

            lock (_gate)
            {
                if (!IsRunning || _engine is null) return;

                double pitch = PitchHzFromRadio();
                int? width = FilterWidthHzFromRadio();
                if (pitch == _pitchHz && width == _filterWidthHz) return;

                // Rebuilding costs the decoder its learned speed and tone, so
                // it happens only when the operator has actually moved one of
                // these - not on every CAT poll that reports the same value.
                _logger.LogInformation(
                    "CW reader reconfiguring: pitch {OldPitch}->{NewPitch} Hz, filter {OldWidth}->{NewWidth}",
                    _pitchHz, pitch,
                    _filterWidthHz?.ToString() ?? "unknown",
                    width?.ToString() ?? "unknown");

                string tail = _engine.Flush();
                if (tail.Length > 0) AppendLocked(tail);

                BuildEngine();
            }
        }

        private void OnTextDecoded(string text)
        {
            lock (_gate) AppendLocked(text);
        }

        /// <summary>Caller holds _gate.</summary>
        private void AppendLocked(string text)
        {
            _text.Append(text);
            _totalChars += text.Length;

            if (_text.Length > MaxTextLength)
                _text.Remove(0, _text.Length - MaxTextLength);
        }

        public void Dispose()
        {
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CW reader disposal");
            }
        }
    }

    /// <summary>One read of the reader's state, for the UI.</summary>
    public sealed class CwReaderSnapshot
    {
        public bool Running { get; init; }

        /// <summary>
        /// False means no browser is holding the remote-audio WebSocket, so
        /// nothing has asked the bridge to open a device and the decoder is
        /// being fed silence. This is not the same as the Remote Audio
        /// *setting*: enabling it in Settings only reveals the Remote Audio
        /// bar. Something still has to press connect, and until it does this
        /// stays false. Distinguishing the two is the whole point of
        /// carrying it - a reader that says "start remote audio" to an
        /// operator who already has it enabled sends them looking in the
        /// wrong place.
        /// </summary>
        public bool AudioSessionActive { get; init; }

        /// <summary>
        /// False means the audio bridge has no capture device open, so there
        /// is nothing to decode however healthy the rest looks. With a
        /// session active this means the devices failed to open, which is a
        /// real fault; with no session it simply means nobody has connected.
        /// </summary>
        public bool AudioDevicesOpen { get; init; }

        public string Text { get; init; } = "";

        /// <summary>Pass back as "since" on the next poll.</summary>
        public long Cursor { get; init; }

        /// <summary>Text was dropped between the caller's cursor and this read.</summary>
        public bool Truncated { get; init; }

        public double PitchHz { get; init; }

        /// <summary>Null when the radio has not reported a width the decoder can use.</summary>
        public int? FilterWidthHz { get; init; }

        public double SearchWindowHz { get; init; }
        public string? Mode { get; init; }
        public double WordsPerMinute { get; init; }
        public double ToneHz { get; init; }
        public double SnrDb { get; init; }
        public bool SignalPresent { get; init; }
        public bool IsLocked { get; init; }

        /// <summary>
        /// How far to move the dial, in Hz, to bring the signal onto the
        /// operator's own CW pitch - positive meaning tune up. Null when the
        /// tone confidence is too low to be worth acting on, or when the
        /// offset is so large that the detector has almost certainly locked
        /// onto a different signal.
        ///
        /// On 2026-08-27 a station on 12m was reported as very quiet. It was
        /// 120 Hz above the pitch, out on the skirt of a 300 Hz filter, and
        /// everything needed to say so was already being computed - it simply
        /// never reached the panel. That is what this carries.
        /// </summary>
        public long? ZeroInOffsetHz { get; init; }

        /// <summary>
        /// Whether the marks arriving can be Morse at all: Unknown, Readable,
        /// Chatter or Jumbled. Not the same question as the tone confidence,
        /// and the two disagree exactly when it matters - a detector
        /// chattering on a near-threshold carrier tracks the tone perfectly
        /// and copies nothing.
        ///
        /// The reader shows no text in Chatter or Jumbled, so without this the
        /// panel would sit blank with a healthy SNR beside it and no
        /// explanation, which is the reading that sent the operator looking
        /// for a fault in the radio in the first place.
        /// </summary>
        public string Readability { get; init; } = "Unknown";
        public long DroppedFrames { get; init; }
    }
}
