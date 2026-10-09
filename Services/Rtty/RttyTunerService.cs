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
    /// the operator's (2125 Hz unless they say otherwise), and the space tone
    /// comes out 170 Hz above it in every mode measured so far:
    ///
    ///   RTTY-L   the dial is on mark and the radio demodulates lower
    ///            sideband about it, so space - 170 Hz below mark on the air -
    ///            comes out 170 Hz ABOVE mark in the audio. Measured on the
    ///            FTdx101MP 2026-09-23: a carrier stepped below the RTTY-L dial
    ///            rose in pitch Hz for Hz from 2125.
    ///   RTTY-U   upper sideband about mark + shift: a carrier at the dial
    ///            comes out at 2295 Hz (measured 2026-09-24, #178), so the two
    ///            tones are 2125 and 2295 here too. The upper one is the
    ///            station's mark, so the radio's decoder reads amateur RTTY
    ///            reversed in RTTY-U, but the cross is the same either way
    ///            round. This used to put space at 1955, unmeasured: in CQ WW
    ///            RTTY (2026-09-26) it took Reverse to get a cross in RTTY-U,
    ///            and that Reverse broke the cross on switching to RTTY-L.
    ///   anything else - DATA-L, DATA-U, LSB, USB - is AFSK, where the software
    ///            makes the tones, and RTTY software puts space above mark
    ///            (2125 / 2295) by default.
    ///
    /// Reverse puts the space tone below mark instead, for REV polarity on the
    /// radio or reversed tones in the software. A station sending reversed
    /// does not need it: its tones land in the same two places.
    ///
    /// It opens no device of its own: it takes a capture hold on the audio
    /// bridge, the same reference-counted hold the CW reader takes, so the two
    /// can run together.
    ///
    /// The mode is asked of the radio when the tuner starts. Until the
    /// connect-time MD0; read lands, RadioStateService still holds the mode
    /// from the last session's radio_state.json, and on a cold start the
    /// tuner saw "LSB" there while the radio was in RTTY-L. No mode moves the
    /// tones today, but the status reports the mode and a model measured
    /// differently would need it.
    /// </summary>
    public sealed class RttyTunerService : IDisposable
    {
        public const double DefaultMarkHz  = 2125.0;
        public const int    DefaultShiftHz = 170;

        /// <summary>
        /// Amateur RTTY, and what the radio's own decoder is fixed at.
        /// </summary>
        public const double DefaultBaud = 45.45;

        private readonly RadioAudioBridgeService _bridge;
        private readonly RadioStateService _state;
        private readonly CatMultiplexerService _mux;
        private readonly RttyTunerModeService _mode;
        private readonly ILogger<RttyTunerService> _logger;
        private readonly object _gate = new();

        private RttyTuningScope? _scope;
        private bool _holdsCapture;
        private bool _acquiring;
        private string? _captureError;
        private double _markHz = DefaultMarkHz;
        private int _shiftHz = DefaultShiftHz;
        private bool _reverse;
        // What RttyTunerModeService changed about the radio, for the status line.
        private string? _modeNote;
        // The scope does not use this - it is two filters and speed means nothing
        // to it. It is held here because this is the server-side record of what the
        // operator is listening to, and the reader that decodes it will want it. On
        // the server rather than in the page for the reason Reader Mode's state is:
        // a reload must not lose the one figure the operator cannot re-derive by
        // eye. See the note on Baud in StartAsync.
        private double _baud = DefaultBaud;
        // One per window showing the figure; the audio is held while any is.
        private readonly RttyTunerLeases _leases = new();
        private System.Threading.Timer? _timer;

        public RttyTunerService(RadioAudioBridgeService bridge,
                                RadioStateService state,
                                CatMultiplexerService mux,
                                RttyTunerModeService mode,
                                ILogger<RttyTunerService> logger)
        {
            _bridge = bridge;
            _state = state;
            _mux = mux;
            _mode = mode;
            _logger = logger;
        }

        public bool IsRunning { get { lock (_gate) return _scope != null; } }

        /// <summary>
        /// What the operator has said they are listening to. Held here, and
        /// readable whether the scope is running or not, because this dialog is
        /// where those four figures are set and this service is where they
        /// survive a page reload - so the reader asks the tuner rather than
        /// keeping a second copy that could disagree with the figure on screen.
        ///
        /// <para>Note what is <em>not</em> here: where the two tones actually
        /// land in the audio. That depends on the radio's mode as well, which
        /// moves under both of us, so each side works it out from
        /// <see cref="TonesFor"/> against the mode of the moment rather than
        /// being handed a pair that was right a second ago.</para>
        /// </summary>
        public RttyListening Listening
        {
            get { lock (_gate) return new RttyListening(_markHz, _shiftHz, _reverse, _baud); }
        }

        /// <summary>
        /// The operator has changed one of those four. Raised outside the lock,
        /// and only on a real change: a start that re-sends the same settings -
        /// which every re-poll of a second browser window does - says nothing,
        /// because a listener that rebuilds a decoder on it would be rebuilding
        /// it several times a second and never decode a character.
        /// </summary>
        public event Action<RttyListening>? ListeningChanged;

        /// <summary>
        /// Where the mark and space filters go, in audio Hz. Pure, so the
        /// rule can be tested without a radio. The mode is kept for a radio
        /// that turns out to differ; on the FTdx101MP none does.
        /// </summary>
        public static (double MarkHz, double SpaceHz) TonesFor(string? mode, double markHz, int shiftHz, bool reverse)
        {
            bool spaceAbove = !reverse;
            return (markHz, spaceAbove ? markHz + shiftHz : markHz - shiftHz);
        }

        /// <summary>
        /// Start for <paramref name="client"/>, or re-tone if already running.
        /// The filters are shared, so a re-tone from one window moves them for
        /// every window. Returns an error for bad settings.
        /// </summary>
        public async Task<string?> StartAsync(double markHz, int shiftHz, bool reverse,
                                              double baud = DefaultBaud, string? client = null)
        {
            if (markHz < 300 || markHz > 3000) return "Mark must be between 300 and 3000 Hz.";
            // Any shift that fits in the audio, not just the four this radio's own
            // RTTY Shift Width menu offers (170, 200, 425, 850 - see RttyToneMap).
            //
            // This was briefly restricted to a fixed list, on the reasoning that a
            // shift the radio cannot be told about is a shift the operator cannot
            // use. That is the wrong way round, and Colin settled it on 2026-10-08:
            // the decoder is the authority, not the radio's own. A listener meets
            // 450 Hz on the DWD weather stations every day - and 450 is precisely
            // the rung this radio's menu does not have, which is the whole shape of
            // the problem - while the 850 of aviation circuits it does. Writing the
            // menu is a separate request and already reports honestly when there is
            // no rung for a figure.
            if (shiftHz < 20 || shiftHz > 1200) return "Shift must be between 20 and 1200 Hz.";
            // Checked both ways round, so a later mode change cannot move space out of range.
            if (markHz + shiftHz > 3500 || markHz - shiftHz < 150)
                return "That mark and shift put the space tone outside the audio passband.";
            // Not used by the scope, only recorded - but recorded wrong is worse than
            // not recorded, so it is checked like anything else. The range covers
            // every speed a listener meets, from 45.45 to the 100 and 200 baud
            // military and aviation circuits.
            if (baud < 20 || baud > 300) return "Speed must be between 20 and 300 baud.";

            // A re-tone of a running tuner already has the mode: the
            // ModeA change handler has kept it current since the start.
            if (!IsRunning) await ReadModeAsync();

            // Before the lock, and after the mode has been read, because the mode
            // is an input to both: TonesFor reads _state.ModeA to decide which
            // side of the mark the space tone sits on, so a mode change after the
            // filters were placed would place them on the wrong sides. Only the
            // first start of a run changes anything - see EnsureAsync.
            var modeNote = await _mode.EnsureAsync(markHz, shiftHz, baud);

            bool acquire;
            bool changed;
            lock (_gate)
            {
                changed = _markHz != markHz || _shiftHz != shiftHz
                       || _reverse != reverse || _baud != baud;
                _markHz = markHz;
                _shiftHz = shiftHz;
                _reverse = reverse;
                _baud = baud;
                // Kept rather than returned: StartAsync's return value is an
                // error, and what the mode service did is not an error. It rides
                // out on the next frame so every window showing the figure says
                // the same thing, including one that opened afterwards.
                if (modeNote != null) _modeNote = modeNote;
                _leases.Start(client, DateTime.UtcNow);

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

            // Outside the lock: a handler that rebuilt a decoder while holding
            // it would be holding this one too, and this one is taken on every
            // audio frame.
            if (changed) ListeningChanged?.Invoke(new RttyListening(markHz, shiftHz, reverse, baud));

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

        /// <summary>
        /// <paramref name="client"/>'s dialog has closed. The audio is let go
        /// once no other window holds it, after
        /// <see cref="RttyTunerLeases.StopDebounce"/> unless restarted.
        /// </summary>
        public void RequestStop(string? client = null)
        {
            lock (_gate)
            {
                if (_scope != null) _leases.Stop(client, DateTime.UtcNow);
            }
        }

        private void OnTimer()
        {
            string? why = null;
            lock (_gate)
            {
                why = _leases.Expire(DateTime.UtcNow);
            }
            if (why != null) _ = StopNowAsync(why);
        }

        private async Task StopNowAsync(string why)
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
                _leases.Clear();
                _captureError = null;
                // An acquire still in flight releases its own hold when it
                // finds the scope gone, so only a completed hold is ours.
                release = _holdsCapture;
                _holdsCapture = false;
                _modeNote = null;
            }

            if (release) _bridge.ReleaseCapture();

            // After the audio, and outside the lock because it awaits. The two do
            // not share a wire on this radio the way they do on the Icom - the
            // audio is the browser's bridge and the restore is the CAT port - but
            // letting the operator's capture device go first still means a radio
            // that is slow to answer cannot hold it open. A no-op unless the start
            // changed something.
            await _mode.RestoreAsync();
            _logger.LogInformation("RTTY tuner stopped ({Why})", why);
        }

        /// <summary>
        /// The latest <paramref name="points"/> points of the figure, scaled
        /// to whole numbers against this sweep's own peak so the reply stays
        /// small. The peak is sent too, for the display's gain control.
        ///
        /// Running means running for <paramref name="client"/>: a window
        /// whose lease lapsed while another kept the tuner going is told it
        /// is stopped, so it starts again and is counted.
        /// </summary>
        public RttyTunerFrame Frame(int points, string? client = null)
        {
            RttyScopeFrame? f;
            lock (_gate)
            {
                f = _scope != null && _leases.Poll(client, DateTime.UtcNow) ? _scope.Snapshot(points) : null;
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
                Baud             = _baud,
                CaptureError     = _captureError,
                ModeNote         = _modeNote,
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

            // Outside the lock, because it writes to the radio, and only when
            // there is something to write: the mode arrives on every poll loop
            // and the usual answer is "still RTTY, nothing to do".
            if (_mode.HeldMode is not null
                && RttyMarkCentre.SidebandForFskMode(_state.ModeA) is null)
            {
                _ = HoldModeAsync();
            }
        }

        /// <summary>
        /// Put the mode back when something outside the tuner has moved it. Any
        /// failure is logged and dropped: the figure is still worth drawing, and
        /// a mode that could not be written will be tried again on the next poll
        /// that reports it.
        /// </summary>
        private async Task HoldModeAsync()
        {
            try
            {
                var note = await _mode.ReassertAsync();
                if (note is null) return;
                lock (_gate)
                {
                    if (_scope != null) _modeNote = note;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RTTY tuner: holding the mode threw");
            }
        }

        public void Dispose() => StopNowAsync("shutting down").GetAwaiter().GetResult();
    }

    /// <summary>
    /// The four figures the operator sets in the RTTY dialog. Mark and shift
    /// place the tones, Reverse flips which is which, and Baud is the sending
    /// speed - which the scope has no use for and the reader cannot work
    /// without.
    /// </summary>
    public sealed record RttyListening(double MarkHz, int ShiftHz, bool Reverse, double Baud);

    public sealed class RttyTunerFrame
    {
        public bool    Running          { get; set; }
        public string  Mode             { get; set; } = "";
        public double  MarkHz           { get; set; }
        public double  SpaceHz          { get; set; }
        public int     ShiftHz          { get; set; }
        public bool    Reverse          { get; set; }

        /// <summary>
        /// The speed the operator has set, which the scope does not use. Here so
        /// that the dialog shows the same figure after a reload, and for the reader
        /// to pick up. See the field it comes from in RttyTunerService.
        /// </summary>
        public double  Baud             { get; set; }
        public string? CaptureError     { get; set; }

        /// <summary>
        /// What was changed about the radio so that the figure could be trusted -
        /// a mode switch, a widened filter, or both. Null when nothing was, which
        /// is the usual case: an operator already in RTTY with a sensible filter
        /// is told nothing, because nothing happened to them.
        /// </summary>
        public string? ModeNote        { get; set; }
        public bool    AudioDevicesOpen { get; set; }

        /// <summary>Interleaved x (mark filter), y (space filter), -1000..1000 of Peak.</summary>
        public int[]   Points           { get; set; } = Array.Empty<int>();
        public float   Peak             { get; set; }
        public double  MarkDb           { get; set; }
        public double  SpaceDb          { get; set; }
        public double  InputDb          { get; set; }
    }
}
