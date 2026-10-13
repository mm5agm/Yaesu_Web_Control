using System.ComponentModel;
using System.Text;
using RadioWebControl.Core.Services.Rtty;
using Yaesu_Web_Control.Services.Audio;

namespace Yaesu_Web_Control.Services.Rtty
{
    /// <summary>
    /// The RTTY reader: receive audio in, text out. Everything that decides
    /// what the characters are belongs to Core's
    /// <see cref="RttyDemodulator"/> and is shared with Icom Web Control -
    /// the two tone filters, the start-edge timing, the ITA2 tables. What is
    /// local, and the only reason this class exists, is the wiring: where the
    /// audio comes from, where the settings come from, and how the browser
    /// gets the text.
    ///
    /// <para><b>The audio.</b> A capture hold on
    /// <see cref="RadioAudioBridgeService"/>, the same reference-counted hold
    /// the CW reader and the RTTY tuner take, which is what lets all three run
    /// at once. The frames are taken straight off the bridge rather than
    /// through <see cref="Cw.BridgeCwAudioSource"/>: that wrapper is one
    /// consumer with one queue and one IsRunning, so a second reader sharing it
    /// would have the frames pulled out from under it the moment the CW reader
    /// stopped. Decoding happens on the bridge's callback thread rather than
    /// through a second queue, as the tuner's filters do: two tone magnitudes
    /// and a handful of comparisons per sample is a few tens of microseconds a
    /// frame, nothing like the CW decoder's cost.</para>
    ///
    /// <para><b>The settings are the tuner's, not a second copy.</b> Mark,
    /// shift, reverse and speed are read from <see cref="RttyTunerService"/>
    /// every time they are needed. That is the whole argument for the tuner
    /// and the reader sharing one panel: the operator tunes the cross, or
    /// presses Auto, and the decoder is already listening in the right place -
    /// there is no second set of boxes to keep in step, and no way for the
    /// figure on screen to disagree with what is being decoded.</para>
    ///
    /// <para><b>It does not touch the radio.</b> Not the mode, not the filter,
    /// not the dial. The tuner's <see cref="RttyTunerModeService"/> owns all
    /// of that - it puts the radio into a mode that can carry a tone pair,
    /// widens the filter if it has to, holds the mode while the panel is open
    /// and puts everything back afterwards. A reader that also wrote the mode
    /// would be a second writer racing the first for no gain, since it decodes
    /// whatever audio it is given either way.</para>
    ///
    /// <para><b>What the text is worth.</b> The same caveat as the CW reader,
    /// and for the same reason: on a clean signal this is close to perfect,
    /// and on a marginal one it prints plausible-looking rubbish with no
    /// outward sign of the difference. <see cref="RttyReaderSnapshot.Activity"/>
    /// is the honest answer to "is anything there" - about 0.8 for a real
    /// station and around a third for noise - and it is on screen so that the
    /// operator can tell the two cases apart. Nothing is hidden on the
    /// strength of it.</para>
    /// </summary>
    public sealed class RttyReaderService : IDisposable
    {
        /// <summary>
        /// Characters kept for the browser. Larger than the CW reader's 8,000:
        /// a RTTY weather bulletin runs to pages and arrives at six characters
        /// a second, so 8,000 would roll over inside half an hour of listening
        /// to DDK9.
        /// </summary>
        private const int MaxTextLength = 24000;

        private readonly RadioAudioBridgeService _bridge;
        private readonly RttyTunerService _tuner;
        private readonly RadioStateService _state;
        private readonly ILogger<RttyReaderService> _logger;
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();

        private RttyDemodulator? _demod;
        private long _totalChars;
        private bool _holdsCapture;
        private string? _captureError;

        // What the demodulator in hand was built with, so a mode or settings
        // change can be compared against it and a rebuild skipped when nothing
        // that matters moved. A rebuild throws away the character being
        // assembled, so doing it needlessly costs copy.
        private double _builtMarkHz;
        private double _builtSpaceHz;
        private double _builtBaud;

        // The operator's, not the tuner's: which figures table to print above
        // the shift, and whether a space drops back to letters. Kept here
        // rather than in the page for the same reason the tuner keeps mark and
        // shift - a reload must not silently change what is being decoded.
        private RttyFigureSet _figures = RttyFigureSet.Ita2;
        private bool _usos = true;

        public RttyReaderService(RadioAudioBridgeService bridge,
                                 RttyTunerService tuner,
                                 RadioStateService state,
                                 ILogger<RttyReaderService> logger)
        {
            _bridge = bridge;
            _tuner = tuner;
            _state = state;
            _logger = logger;
        }

        public bool IsRunning { get; private set; }

        /// <summary>
        /// Start decoding. <paramref name="figures"/> and <paramref name="usos"/>
        /// are remembered when given, so a later start that says nothing keeps
        /// what the operator last chose.
        /// </summary>
        public async Task StartAsync(RttyFigureSet? figures = null,
                                     bool? usos = null,
                                     CancellationToken ct = default)
        {
            bool alreadyRunning;
            lock (_gate)
            {
                if (figures is { } f) _figures = f;
                if (usos is { } u) _usos = u;

                // Rebuild either way: running already only means the alphabet
                // may have changed under us, and a dropdown that did nothing
                // mid-session would be worse than a lost character.
                BuildLocked(force: true);

                alreadyRunning = IsRunning;
                if (!alreadyRunning)
                {
                    _state.PropertyChanged += OnRadioStateChanged;
                    _tuner.ListeningChanged += OnListeningChanged;
                    _bridge.RxFrameCaptured += OnRxFrame;
                    IsRunning = true;
                }
            }

            if (alreadyRunning) return;

            // The hold is taken even when the device will not open, so that the
            // release always balances; the error reaches the operator through
            // the snapshot's CaptureError rather than as an exception.
            var error = await _bridge.AcquireCaptureAsync();
            lock (_gate)
            {
                _captureError = error;
                _holdsCapture = error == null;
            }

            if (error != null)
                _logger.LogWarning("RTTY reader running but capture could not open: {Error}", error);
            else
                _logger.LogInformation(
                    "RTTY reader started: mark {Mark} Hz, space {Space} Hz, {Baud} baud, {Set}{Usos}",
                    _builtMarkHz, _builtSpaceHz, _builtBaud, _figures,
                    _usos ? ", unshift on space" : "");
        }

        /// <summary>
        /// Stop decoding. The text stays: an operator pressing Stop at the end
        /// of a bulletin wants to read what came out of it, not watch it go.
        /// </summary>
        public Task StopAsync(CancellationToken ct = default)
        {
            bool release;
            lock (_gate)
            {
                if (!IsRunning) return Task.CompletedTask;
                _bridge.RxFrameCaptured -= OnRxFrame;
                _state.PropertyChanged -= OnRadioStateChanged;
                _tuner.ListeningChanged -= OnListeningChanged;
                _demod = null;
                IsRunning = false;
                _captureError = null;
                release = _holdsCapture;
                _holdsCapture = false;
            }

            if (release) _bridge.ReleaseCapture();
            _logger.LogInformation("RTTY reader stopped");
            return Task.CompletedTask;
        }

        /// <summary>Discard the text, leaving the decoder running.</summary>
        public void ClearText()
        {
            lock (_gate)
            {
                _text.Clear();
                // _totalChars is deliberately not wound back: it is the cursor
                // the browser polls with, and resetting it would make the next
                // poll replay text that has just been cleared.
            }
        }

        /// <summary>
        /// Everything the panel needs in one read, including the text decoded
        /// since the caller's cursor.
        /// </summary>
        /// <param name="since">
        /// The caller's cursor from its previous snapshot, or 0 for everything
        /// still held. A cursor older than the retained text returns what is
        /// left, with Truncated set, rather than an error.
        /// </param>
        public RttyReaderSnapshot Snapshot(long since)
        {
            var listening = _tuner.Listening;
            var (mark, space) = RttyTunerService.TonesFor(
                _state.ModeA, listening.MarkHz, listening.ShiftHz, listening.Reverse);

            lock (_gate)
            {
                long oldest = _totalChars - _text.Length;
                bool truncated = since < oldest;
                long from = Math.Max(since, oldest);

                string text = from >= _totalChars
                    ? ""
                    : _text.ToString((int)(from - oldest), (int)(_totalChars - from));

                return new RttyReaderSnapshot
                {
                    Running          = IsRunning,
                    AudioDevicesOpen = _bridge.DevicesOpen,
                    CaptureError     = _captureError,
                    // Null here, and not an oversight: the audio arrives over
                    // the browser's audio bridge, so there is no local device
                    // to name. The property is kept so the JSON the shared
                    // panel reads has the same shape in both apps.
                    AudioDeviceName  = null,
                    Text             = text,
                    Cursor           = _totalChars,
                    Truncated        = truncated,
                    Mode             = _state.ModeA ?? "",
                    // Running, report what the decoder is actually listening to
                    // rather than what it would be rebuilt with: if those two
                    // ever disagree, the panel should show the one that is
                    // producing the text on screen.
                    MarkHz           = IsRunning ? _builtMarkHz : mark,
                    SpaceHz          = IsRunning ? _builtSpaceHz : space,
                    ShiftHz          = listening.ShiftHz,
                    Reverse          = listening.Reverse,
                    Baud             = IsRunning ? _builtBaud : listening.Baud,
                    Figures          = _figures.ToString(),
                    Usos             = _usos,
                    Activity         = Math.Round(_demod?.SignalActivity ?? 0, 3),
                    Squelch          = Math.Round(_demod?.Squelch ?? 0, 3),
                    Characters       = _demod?.Characters ?? 0,
                    FramingErrors    = _demod?.FramingErrors ?? 0,
                    InFigures        = _demod?.InFigures ?? false,
                };
            }
        }

        /// <summary>
        /// Build a demodulator for where the tones are now. Caller holds _gate.
        /// Without <paramref name="force"/> it is a no-op unless something it
        /// was built with has actually moved.
        /// </summary>
        private void BuildLocked(bool force)
        {
            var listening = _tuner.Listening;
            var (mark, space) = RttyTunerService.TonesFor(
                _state.ModeA, listening.MarkHz, listening.ShiftHz, listening.Reverse);

            if (!force && _demod != null
                && mark == _builtMarkHz && space == _builtSpaceHz && listening.Baud == _builtBaud)
                return;

            try
            {
                _demod = new RttyDemodulator(
                    AudioConstants.SampleRate, mark, space, listening.Baud, _figures, _usos);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // The tuner validates its own settings before it records them,
                // so this means a tone reached the audio range from some other
                // route. Keep whatever decoder we had rather than tearing the
                // session down around it.
                _logger.LogWarning(ex,
                    "RTTY reader: mark {Mark} Hz / space {Space} Hz at {Baud} baud cannot be decoded",
                    mark, space, listening.Baud);
                return;
            }

            _builtMarkHz = mark;
            _builtSpaceHz = space;
            _builtBaud = listening.Baud;
        }

        /// <summary>
        /// On the bridge's capture thread. Under the lock because a settings
        /// change can replace the demodulator from another thread, and
        /// <see cref="RttyDemodulator.Feed"/> is a state machine with a
        /// timebase - handing half a character to a new one is how a rebuild
        /// would turn into a corrupted line rather than a lost one.
        /// </summary>
        private void OnRxFrame(ReadOnlyMemory<float> frame)
        {
            try
            {
                lock (_gate)
                {
                    if (_demod == null) return;
                    var decoded = _demod.Feed(frame.Span);
                    if (decoded.Length > 0) AppendLocked(decoded);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RTTY reader frame threw - ignoring");
            }
        }

        /// <summary>Caller holds _gate.</summary>
        private void AppendLocked(string text)
        {
            _text.Append(text);
            _totalChars += text.Length;

            if (_text.Length > MaxTextLength)
                _text.Remove(0, _text.Length - MaxTextLength);
        }

        /// <summary>
        /// The radio's mode moved, which decides which side of the mark the
        /// space tone lands on - so RTTY to RTTY-R swaps the two filters even
        /// though the operator changed nothing.
        /// </summary>
        private void OnRadioStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(RadioStateService.ModeA)) return;
            Retune();
        }

        private void OnListeningChanged(RttyListening listening) => Retune();

        private void Retune()
        {
            lock (_gate)
            {
                if (!IsRunning) return;
                double wasMark = _builtMarkHz, wasSpace = _builtSpaceHz, wasBaud = _builtBaud;
                BuildLocked(force: false);
                if (wasMark != _builtMarkHz || wasSpace != _builtSpaceHz || wasBaud != _builtBaud)
                    _logger.LogInformation(
                        "RTTY reader retuned: mark {Mark} Hz, space {Space} Hz, {Baud} baud",
                        _builtMarkHz, _builtSpaceHz, _builtBaud);
            }
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogDebug(ex, "RTTY reader: stopping on shutdown threw"); }
        }
    }

    public sealed class RttyReaderSnapshot
    {
        public bool    Running          { get; init; }
        public bool    AudioDevicesOpen { get; init; }
        public string? CaptureError     { get; init; }
        public string? AudioDeviceName  { get; init; }

        /// <summary>Text decoded since the caller's cursor.</summary>
        public string  Text             { get; init; } = "";

        /// <summary>Send this back as <c>since</c> on the next poll.</summary>
        public long    Cursor           { get; init; }

        /// <summary>The buffer rolled over before the caller came back for it.</summary>
        public bool    Truncated        { get; init; }

        public string  Mode             { get; init; } = "";
        public double  MarkHz           { get; init; }
        public double  SpaceHz          { get; init; }
        public int     ShiftHz          { get; init; }
        public bool    Reverse          { get; init; }
        public double  Baud             { get; init; }
        public string  Figures          { get; init; } = "";
        public bool    Usos             { get; init; }

        /// <summary>
        /// How decisively one tone is beating the other, averaged over the last
        /// dozen bit times: about 0.8 for a clean station, around a third for
        /// noise. Shown next to the text because it is the only honest way to
        /// tell good copy from confident-looking rubbish.
        /// </summary>
        public double  Activity         { get; init; }

        /// <summary>The gate <see cref="Activity"/> has to clear to print.</summary>
        public double  Squelch          { get; init; }

        public long    Characters       { get; init; }
        public long    FramingErrors    { get; init; }
        public bool    InFigures        { get; init; }
    }
}
