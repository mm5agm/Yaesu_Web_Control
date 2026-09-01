using System.ComponentModel;
using System.Globalization;
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
        private CwTranscriptWriter? _transcript;
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
                StartTranscript();
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

                _transcript?.Dispose();
                _transcript = null;
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
        /// Phasor points for the tuning aid, drained from the engine's ring.
        ///
        /// Deliberately not folded into Snapshot: these arrive 200 a second and
        /// only matter while the aid is on screen, so the display polls for
        /// them separately and the main reader poll stays small.
        /// </summary>
        /// <summary>
        /// A snapshot of the passband for the spectrum display.
        ///
        /// Unlike the phasor this carries no cursor: it is a picture of now,
        /// not a stream, so a caller that misses a poll has missed nothing.
        /// </summary>
        public CwSpectrumView Spectrum()
        {
            lock (_gate)
            {
                var eng = _engine;
                if (eng is null)
                    return new CwSpectrumView { PitchHz = _pitchHz, BinHz = 0.0 };

                var f = eng.Spectrum();
                return new CwSpectrumView
                {
                    FirstHz       = f.FirstHz,
                    BinHz         = f.BinHz,
                    Db            = f.Db ?? Array.Empty<double>(),
                    PitchHz       = f.PitchHz,
                    ToneHz        = f.ToneHz,
                    Confidence    = f.Confidence,
                    SignalPresent = f.SignalPresent,
                };
            }
        }

        public CwPhasorFrame Phasor(long since)
        {
            lock (_gate)
            {
                var eng = _engine;
                if (eng is null)
                    return new CwPhasorFrame { Cursor = 0, PitchHz = _pitchHz };

                var pts = eng.PhasorSince(since, out long cursor);

                // Flattened to a plain number array. At 200 points a second an
                // object per point costs several times the bytes for nothing
                // the display can use.
                var xy = new double[pts.Length * 2];
                var key = new bool[pts.Length];
                for (int k = 0; k < pts.Length; k++)
                {
                    xy[k * 2]     = pts[k].I;
                    xy[k * 2 + 1] = pts[k].Q;
                    key[k]        = pts[k].KeyDown;
                }

                return new CwPhasorFrame
                {
                    Cursor      = cursor,
                    PitchHz     = _pitchHz,
                    ToneHz      = eng.ToneHz,
                    Confidence  = eng.Confidence,
                    SignalPresent = eng.SignalPresent,
                    Points      = xy,
                    KeyDown     = key,
                };
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
                    CaptureError       = _source.CaptureError,
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
                    TranscriptPath   = _transcript?.Path,
                };
            }
        }

        // ---- transcript ----------------------------------------------------

        /// <summary>
        /// Where this session's transcript is being written, or null if nothing
        /// has been decoded yet - the file is not created until it has content.
        /// </summary>
        public string? TranscriptPath
        {
            get { lock (_gate) return _transcript?.Path; }
        }

        /// <summary>Caller holds _gate.</summary>
        private void StartTranscript()
        {
            try
            {
                _transcript?.Dispose();
                _transcript = new CwTranscriptWriter(TranscriptDirectory());
                _transcript.Note(TranscriptHeader());
            }
            catch (Exception ex)
            {
                // A transcript that cannot be written is worth a line in the
                // log and nothing more. The reader's job is to decode; losing
                // the file must not stop the operator reading the screen.
                _logger.LogWarning(ex, "CW transcript could not be started");
                _transcript = null;
            }
        }

        /// <summary>Caller holds _gate.</summary>
        private string TranscriptHeader()
        {
            var bits = new List<string>();
            if (_state.FrequencyA > 0)
                bits.Add((_state.FrequencyA / 1_000_000.0).ToString("F6", CultureInfo.InvariantCulture) + " MHz");
            if (!string.IsNullOrWhiteSpace(_state.ModeA)) bits.Add(_state.ModeA!);
            bits.Add("pitch " + _pitchHz.ToString("F0", CultureInfo.InvariantCulture) + " Hz");
            bits.Add(_filterWidthHz is int w
                ? "filter " + w.ToString(CultureInfo.InvariantCulture) + " Hz"
                : "filter unknown");
            return string.Join(", ", bits);
        }

        /// <summary>
        /// Transcripts live in their own folder under the app data directory
        /// rather than loose beside radio_state.json. One file per reader
        /// session accumulates quickly, and mixed in with the operator's
        /// settings and state files they would bury them.
        /// </summary>
        private static string TranscriptDirectory() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "CW Transcripts");

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

                // A new line in the transcript, with its own timestamp, so it
                // is visible afterwards that the operator moved the pitch or
                // the filter at that point - which is usually why the copy
                // either improved or fell apart.
                _transcript?.Break();
                _transcript?.Note(TranscriptHeader());
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

            // The transcript is the copy that survives the operator not
            // pressing save, so it is written from here rather than from the
            // UI: everything the reader decodes passes through this line, and
            // nothing the UI does can cause a character to miss it.
            _transcript?.Append(text);

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
        /// True when a browser is holding the remote-audio WebSocket.
        ///
        /// This used to be a prerequisite: the decoder heard nothing until
        /// somebody pressed connect on the Remote Audio bar, and the panel had
        /// to explain that. It no longer is - the reader asks the bridge for
        /// capture directly (RX only, so the radio's TX endpoint stays free for
        /// WSJT-X). Kept because it is still worth showing: it says whether the
        /// audio the decoder is reading is also being streamed to a browser.
        /// </summary>
        public bool AudioSessionActive { get; init; }

        /// <summary>
        /// Why capture could not be opened, or null if it opened. Non-null is a
        /// prerequisite the operator has to fix - almost always that the radio's
        /// RX device has not been chosen in Settings.
        /// </summary>
        public string? CaptureError { get; init; }

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

        /// <summary>
        /// The file this session's copy is being written to, or null while
        /// nothing has been decoded. Shown so the operator knows the copy is
        /// being kept without having to go looking for it.
        /// </summary>
        public string? TranscriptPath { get; init; }
    }

    /// <summary>One poll's worth of the tuning figure.</summary>
    /// <summary>
    /// The passband as the reader sees it, for the tuning display.
    /// </summary>
    public sealed class CwSpectrumView
    {
        /// <summary>Centre frequency of the first bin, Hz.</summary>
        public double FirstHz { get; init; }

        /// <summary>Bin spacing, Hz.</summary>
        public double BinHz { get; init; }

        /// <summary>One value per bin, dB above the median of the span. Empty before the first frame.</summary>
        public double[] Db { get; init; } = Array.Empty<double>();

        /// <summary>Where the operator asked to listen - the marker on the display.</summary>
        public double PitchHz { get; init; }

        /// <summary>Where the reader believes the tone is.</summary>
        public double ToneHz { get; init; }

        public double Confidence { get; init; }
        public bool   SignalPresent { get; init; }
    }

    public sealed class CwPhasorFrame
    {
        /// <summary>Pass back as "since" on the next poll.</summary>
        public long Cursor { get; init; }

        /// <summary>The pitch the figure is referenced to, Hz.</summary>
        public double PitchHz { get; init; }

        /// <summary>Where the detector thinks the tone actually is, Hz.</summary>
        public double ToneHz { get; init; }

        public double Confidence { get; init; }
        public bool   SignalPresent { get; init; }

        /// <summary>I,Q,I,Q... oldest first.</summary>
        public double[] Points { get; init; } = Array.Empty<double>();

        /// <summary>One flag per point, so the display can lift the pen between characters.</summary>
        public bool[] KeyDown { get; init; } = Array.Empty<bool>();
    }
}
