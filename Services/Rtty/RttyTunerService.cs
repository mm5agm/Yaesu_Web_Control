using System.ComponentModel;
using RadioWebControl.Core.Services.Rtty;
using Yaesu_Web_Control.Services.Audio;

namespace Yaesu_Web_Control.Services.Rtty
{
    /// <summary>
    /// The RTTY tuning scope as the application sees it: feeds the received
    /// audio to Core's crossed-ellipse filters and hands the browser one sweep
    /// at a time.
    ///
    /// The part that is Yaesu's is where the two filters go. The mark tone is
    /// the operator's (2125 Hz unless they say otherwise), and which side of it
    /// the space tone falls in the audio depends on the radio's mode:
    ///
    ///   RTTY-L   the dial is on mark and the radio demodulates lower
    ///            sideband about it, so space - 170 Hz below mark on the air -
    ///            comes out 170 Hz ABOVE mark in the audio. Measured on the
    ///            FTdx101MP 2026-09-23: a carrier stepped below the RTTY-L dial
    ///            rose in pitch Hz for Hz from 2125.
    ///   RTTY-U   upper sideband, so space comes out below mark. Not measured.
    ///   anything else - DATA-L, DATA-U, LSB, USB - is AFSK, where the software
    ///            makes the tones, and RTTY software puts space above mark
    ///            (2125 / 2295) by default.
    ///
    /// Reverse flips the space tone to the other side of mark, for REV polarity
    /// on the radio or reversed tones in the software.
    ///
    /// It opens no device of its own: it takes a capture hold on the audio
    /// bridge, the same reference-counted hold the CW reader takes, so the two
    /// can run together.
    ///
    /// The mode is asked of the radio when the tuner starts. Until the
    /// connect-time MD0; read lands, RadioStateService still holds the mode
    /// from the last session's radio_state.json, and on a cold start the
    /// tuner saw "LSB" there while the radio was in RTTY-L - which puts space
    /// on the wrong side of mark whenever the radio is really in RTTY-U.
    /// </summary>
    public sealed class RttyTunerService : IDisposable
    {
        public const double DefaultMarkHz  = 2125.0;
        public const int    DefaultShiftHz = 170;

        /// <summary>
        /// Nobody has asked for a sweep in this long: the page has gone, so
        /// stop holding the radio's audio device open for it.
        /// </summary>
        private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Closing and reopening the dialog must not close and reopen the
        /// radio's USB capture: churning PortAudio on that codec has
        /// native-crashed the host (see FilterSpectrumService). A stop only
        /// takes effect once it has stood this long.
        /// </summary>
        private static readonly TimeSpan StopDebounce = TimeSpan.FromSeconds(2);

        private readonly RadioAudioBridgeService _bridge;
        private readonly RadioStateService _state;
        private readonly CatMultiplexerService _mux;
        private readonly ILogger<RttyTunerService> _logger;
        private readonly object _gate = new();

        private RttyTuningScope? _scope;
        private bool _holdsCapture;
        private bool _acquiring;
        private string? _captureError;
        private double _markHz = DefaultMarkHz;
        private int _shiftHz = DefaultShiftHz;
        private bool _reverse;
        private DateTime _lastPollUtc;
        private DateTime? _stopRequestedUtc;
        private System.Threading.Timer? _timer;

        public RttyTunerService(RadioAudioBridgeService bridge,
                                RadioStateService state,
                                CatMultiplexerService mux,
                                ILogger<RttyTunerService> logger)
        {
            _bridge = bridge;
            _state = state;
            _mux = mux;
            _logger = logger;
        }

        public bool IsRunning { get { lock (_gate) return _scope != null; } }

        /// <summary>
        /// Where the mark and space filters go, in audio Hz. Pure, so the
        /// rule can be tested without a radio.
        /// </summary>
        public static (double MarkHz, double SpaceHz) TonesFor(string? mode, double markHz, int shiftHz, bool reverse)
        {
            bool spaceAbove = mode != "RTTY-U";
            if (reverse) spaceAbove = !spaceAbove;
            return (markHz, spaceAbove ? markHz + shiftHz : markHz - shiftHz);
        }

        /// <summary>Start, or re-tone if already running. Returns an error for bad settings.</summary>
        public async Task<string?> StartAsync(double markHz, int shiftHz, bool reverse)
        {
            if (markHz < 300 || markHz > 3000) return "Mark must be between 300 and 3000 Hz.";
            if (shiftHz is not (170 or 200 or 425 or 450 or 850)) return "Shift must be 170, 200, 425, 450 or 850 Hz.";
            // Checked both ways round, so a later mode change cannot move space out of range.
            if (markHz + shiftHz > 3500 || markHz - shiftHz < 150)
                return "That mark and shift put the space tone outside the audio passband.";

            // A re-tone of a running tuner already has the mode: the
            // ModeA change handler has kept it current since the start.
            if (!IsRunning) await ReadModeAsync();

            bool acquire;
            lock (_gate)
            {
                _markHz = markHz;
                _shiftHz = shiftHz;
                _reverse = reverse;
                _lastPollUtc = DateTime.UtcNow;
                _stopRequestedUtc = null;

                var (m, s) = TonesFor(_state.ModeA, _markHz, _shiftHz, _reverse);
                if (_scope == null)
                {
                    _scope = new RttyTuningScope(AudioConstants.SampleRate, m, s);
                    _state.PropertyChanged += OnRadioStateChanged;
                    _bridge.RxFrameCaptured += OnRxFrame;
                    _timer = new System.Threading.Timer(_ => OnTimer(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                }
                else
                {
                    _scope.SetTones(m, s);
                }
                // Retry a failed open on every start: the operator may have
                // fixed the device setting since.
                acquire = !_holdsCapture && !_acquiring;
                if (acquire) _acquiring = true;
            }

            if (acquire)
            {
                var error = await _bridge.AcquireCaptureAsync();
                bool stoppedMeanwhile;
                lock (_gate)
                {
                    _acquiring = false;
                    // A stop that landed while the device was opening left the
                    // release to us. The bridge undoes its own count on failure.
                    stoppedMeanwhile = _scope == null;
                    _holdsCapture = error == null && !stoppedMeanwhile;
                    if (!stoppedMeanwhile) _captureError = error;
                }
                if (error == null && stoppedMeanwhile)
                {
                    _bridge.ReleaseCapture();
                    return null;
                }
                if (error != null)
                    _logger.LogWarning("RTTY tuner running but capture could not open: {Error}", error);
                else
                    _logger.LogInformation("RTTY tuner started: mark {Mark} Hz, shift {Shift} Hz, {Pol}, mode {Mode}",
                        markHz, shiftHz, reverse ? "reverse" : "normal", _state.ModeA);
            }
            return null;
        }

        /// <summary>
        /// Asks the radio for VFO A's mode; the dispatcher puts the answer
        /// into RadioStateService. With no answer the tuner goes on with what
        /// state holds, and the ModeA change handler re-tones when it lands.
        /// </summary>
        private async Task ReadModeAsync()
        {
            if (!_mux.IsConnected) return;
            try
            {
                await _mux.SendCommandAndDispatchAsync($"MD{RadioCapabilities.ModeP1("A")};", "RttyTuner");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RTTY tuner could not read the mode - using {Mode}", _state.ModeA);
            }
        }

        /// <summary>The dialog has closed. Takes effect after <see cref="StopDebounce"/> unless restarted.</summary>
        public void RequestStop()
        {
            lock (_gate)
            {
                if (_scope != null) _stopRequestedUtc ??= DateTime.UtcNow;
            }
        }

        private void OnTimer()
        {
            string? why = null;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (_stopRequestedUtc is { } at && now - at >= StopDebounce) why = "dialog closed";
                else if (now - _lastPollUtc > IdleStop) why = "no page polling";
            }
            if (why != null) StopNow(why);
        }

        private void StopNow(string why)
        {
            bool release;
            lock (_gate)
            {
                if (_scope == null) return;
                _bridge.RxFrameCaptured -= OnRxFrame;
                _state.PropertyChanged -= OnRadioStateChanged;
                _timer?.Dispose();
                _timer = null;
                _scope = null;
                _stopRequestedUtc = null;
                _captureError = null;
                // An acquire still in flight releases its own hold when it
                // finds the scope gone, so only a completed hold is ours.
                release = _holdsCapture;
                _holdsCapture = false;
            }

            if (release) _bridge.ReleaseCapture();
            _logger.LogInformation("RTTY tuner stopped ({Why})", why);
        }

        /// <summary>
        /// The latest <paramref name="points"/> points of the figure, scaled
        /// to whole numbers against this sweep's own peak so the reply stays
        /// small. The peak is sent too, for the display's gain control.
        /// </summary>
        public RttyTunerFrame Frame(int points)
        {
            RttyScopeFrame? f;
            lock (_gate)
            {
                _lastPollUtc = DateTime.UtcNow;
                f = _scope?.Snapshot(points);
            }

            var (mark, space) = TonesFor(_state.ModeA, _markHz, _shiftHz, _reverse);
            var frame = new RttyTunerFrame
            {
                Running          = f != null,
                Mode             = _state.ModeA ?? "",
                MarkHz           = f?.MarkHz ?? mark,
                SpaceHz          = f?.SpaceHz ?? space,
                ShiftHz          = _shiftHz,
                Reverse          = _reverse,
                CaptureError     = _captureError,
                AudioDevicesOpen = _bridge.DevicesOpen,
            };
            if (f == null) return frame;

            float peak = 0f;
            foreach (var v in f.Points) peak = Math.Max(peak, Math.Abs(v));
            var xy = new int[f.Points.Length];
            if (peak > 0)
                for (int i = 0; i < xy.Length; i++)
                    xy[i] = (int)Math.Round(f.Points[i] / peak * 1000f);

            frame.Points  = xy;
            frame.Peak    = peak;
            frame.MarkDb  = Math.Round(f.MarkDb, 1);
            frame.SpaceDb = Math.Round(f.SpaceDb, 1);
            frame.InputDb = Math.Round(f.InputDb, 1);
            return frame;
        }

        /// <summary>
        /// On the PortAudio callback thread. Four biquads a sample is a few
        /// microseconds a frame, so unlike the CW decoder this runs in place
        /// rather than through a queue.
        /// </summary>
        private void OnRxFrame(ReadOnlyMemory<float> frame)
        {
            try
            {
                _scope?.Process(frame.Span);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RTTY tuner frame threw - ignoring");
            }
        }

        private void OnRadioStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(RadioStateService.ModeA)) return;
            lock (_gate)
            {
                if (_scope == null) return;
                var (m, s) = TonesFor(_state.ModeA, _markHz, _shiftHz, _reverse);
                if (m != _scope.MarkHz || s != _scope.SpaceHz) _scope.SetTones(m, s);
            }
        }

        public void Dispose() => StopNow("shutting down");
    }

    public sealed class RttyTunerFrame
    {
        public bool    Running          { get; set; }
        public string  Mode             { get; set; } = "";
        public double  MarkHz           { get; set; }
        public double  SpaceHz          { get; set; }
        public int     ShiftHz          { get; set; }
        public bool    Reverse          { get; set; }
        public string? CaptureError     { get; set; }
        public bool    AudioDevicesOpen { get; set; }

        /// <summary>Interleaved x (mark filter), y (space filter), -1000..1000 of Peak.</summary>
        public int[]   Points           { get; set; } = Array.Empty<int>();
        public float   Peak             { get; set; }
        public double  MarkDb           { get; set; }
        public double  SpaceDb          { get; set; }
        public double  InputDb          { get; set; }
    }
}
