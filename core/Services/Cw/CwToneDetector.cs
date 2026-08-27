namespace RadioWebControl.Core.Services.Cw
{
    /// <summary>One envelope observation, produced once per hop (5 ms by default).</summary>
    public readonly struct CwToneSample
    {
        /// <summary>Seconds since the detector was created, at the centre of the window.</summary>
        public double TimeSeconds { get; init; }

        /// <summary>Key-down decision after the adaptive threshold and hysteresis.</summary>
        public bool KeyDown { get; init; }

        /// <summary>Goertzel magnitude at the tracked tone, amplitude-like (not power).</summary>
        public double Magnitude { get; init; }

        /// <summary>Tone the detector is currently tracking, Hz.</summary>
        public double ToneHz { get; init; }

        /// <summary>Rough signal-to-noise in the detector own ~90 Hz bandwidth, dB.</summary>
        public double SnrDb { get; init; }

        /// <summary>0..1. How much the pitch measurement should be believed.</summary>
        public double Confidence { get; init; }

        /// <summary>True while the presence gate says there is a signal at all.</summary>
        public bool SignalPresent { get; init; }

        /// <summary>
        /// The tracked noise floor at this instant, in the same units as
        /// Magnitude. Carried so that consumers can ask how far above the noise
        /// one particular element sat, which SnrDb cannot answer: SnrDb is built
        /// from the slow peak tracker and describes the signal, not the mark.
        /// </summary>
        public double NoiseLevel { get; init; }

        /// <summary>
        /// The adaptive mark reference at this instant, same units as Magnitude.
        /// With NoiseLevel this pins down both thresholds exactly - on at half
        /// way from the noise up to the reference, off at 35% - so a trace can
        /// show what the detector decided and why, rather than leaving it to be
        /// inferred from where KeyDown happened to flip.
        /// </summary>
        public double MarkLevel { get; init; }

        /// <summary>
        /// In-phase part of the audio at the operator's <em>configured</em>
        /// pitch - not the tracked tone. Referenced to a phase that runs
        /// continuously from the start of the stream, so a tone sitting exactly
        /// on the pitch holds a fixed angle and a tone off by dF rotates at
        /// exactly dF turns per second, anticlockwise when it is high.
        ///
        /// That is the whole point: referenced to the tracked tone it would be
        /// stationary by construction and would show the operator nothing.
        /// Units are the same as Magnitude, and unnormalised - the level here
        /// follows the AGC, so a display wanting a constant-size figure has to
        /// scale it itself.
        /// </summary>
        public double PhasorI { get; init; }

        /// <summary>Quadrature part. See <see cref="PhasorI"/>.</summary>
        public double PhasorQ { get; init; }
    }

    public sealed class CwToneDetectorOptions
    {
        /// <summary>Rate of the frames handed in. Must be a whole multiple of 8000.</summary>
        public int InputSampleRate { get; set; } = 48000;

        /// <summary>The configured CW pitch, and the centre of the search window.</summary>
        public double PitchHz { get; set; } = 600.0;

        /// <summary>Half-width of the pitch search, Hz. The tone is never tracked outside it.</summary>
        public double SearchWindowHz { get; set; } = 250.0;

        /// <summary>False pins the Goertzel to PitchHz, which is what Reader Mode wants.</summary>
        public bool TrackPitch { get; set; } = true;

        /// <summary>
        /// Report nothing keyed for this long after the first audio, while the
        /// noise and peak estimates settle, seconds.
        ///
        /// The noise mean is an EMA with a quarter-second time constant and the
        /// peak tracker rises four times faster, so for the first second after
        /// audio starts their ratio is meaningless - it reads as tens of dB of
        /// signal-to-noise on plain hiss. Left alone it produces a burst of very
        /// short marks in the first two seconds of every session, which is
        /// exactly long enough to drag the speed tracker onto its MaxWpm clamp
        /// before a single real element has arrived (plan section 4.11d.1).
        /// </summary>
        public double WarmupSeconds { get; set; } = 0.5;

        /// <summary>
        /// Erase key-state runs shorter than this, milliseconds. 0 disables it.
        ///
        /// A mark that dips below the off threshold for one or two hops is
        /// reported as two marks with a gap, and the element decoder has no way
        /// to tell that from a genuine dit-dit.
        ///
        /// What this actually removes is not what it was built for. The first
        /// reading was that the bad files were fragmented marks, but turning
        /// the debounce on barely moves their mark histogram while very nearly
        /// doubling the decode. The histogram only counts runs of 12 ms and
        /// up; what goes away is the key-down blips below that, which never
        /// appeared in it and which the element decoder was reading as dits.
        /// That is where the E/I/S/H chatter came from. Speed is not the
        /// discriminator either - 21, 25, 30, 32 and 47 wpm all decode well,
        /// and the worst files still measure 42-46 wpm with this switched on.
        ///
        /// The transition is backdated to where the run actually began, not to
        /// where it was confirmed, so suppressing a dropout costs no timing
        /// accuracy - which is the whole reason this is not a plain hold-off.
        /// The cost is latency: samples are held for this long before they can
        /// be emitted.
        ///
        /// Keep it well under a dit or it will merge real elements. A dit is
        /// 30 ms at 40 wpm and 25 ms at 47 wpm, so 10 ms is about the ceiling
        /// for the speeds this decoder claims to reach.
        ///
        /// 10 ms is the default, chosen on the bench on 2026-08-27. Readable
        /// characters, off against 10 ms against 15 ms:
        ///
        ///     strong-fast-2   83 / 46%    157 / 50%    185 / 63%
        ///     strong-fast-1   59 / 53%     66 / 56%    107 / 62%
        ///     mkii-dk9py     261 / 95%    248 / 95%    225 / 96%
        ///     strong-sig-1    81 / 91%     72 / 92%     71 / 92%
        ///     cq-then-qso    119 / 98%    119 / 98%    119 / 98%
        ///
        /// 15 ms scores better on the broken files but it is 0.58 of a dit at
        /// 46 wpm and starts eating real elements - AC1D disappears out of
        /// mkii-dk9py. 10 ms is 0.38 of a dit. It is not free even so:
        /// strong-sig-1 loses nine characters and a correct SM6M becomes V36M.
        /// </summary>
        public double KeyDebounceMs { get; set; } = 10.0;
    }

    /// <summary>
    /// Turns audio into a clean key-down/key-up stream plus a tone measurement.
    ///
    /// Two paths run at very different rates, because pitch and keying change at
    /// very different rates:
    ///
    /// Envelope path. A single Goertzel at the tracked tone over an 80-sample
    /// window at 8 kHz - 10 ms, about 90 Hz of bandwidth, which is a sensible CW
    /// filter - stepped every 40 samples so the edges land to 5 ms. At 40 WPM a
    /// dit is 30 ms, so this is the part that has to be quick; a long FFT on its
    /// own cannot do it.
    ///
    /// Pitch path. A 1024-point FFT (7.8 Hz bins, 128 ms) every 512 samples finds
    /// where the tone actually is inside the search window. Slow is fine: pitch
    /// drifts, it does not key. This feeds the Goertzel above and, later, zero-in.
    ///
    /// The threshold between the two is adaptive - a floor tracker and a peak
    /// tracker with Schmitt hysteresis between them - which is the whole point.
    /// A fixed threshold is what DEC LVL on the FTdx101 is, and having to set it
    /// by hand is the reason we are writing this at all.
    /// </summary>
    public sealed class CwToneDetector
    {
        private const int WorkRate  = 8000;
        private const int EnvWindow = 80;    // 10 ms
        private const int EnvHop    = 40;    // 5 ms
        private const int FftSize   = 1024;  // 128 ms, 7.8 Hz bins
        private const int FftHop    = 512;
        private const int FirTaps   = 49;

        // Threshold shaping. On at half way from the noise mean up to the peak,
        // off at 35%: wide enough to ignore noise ripple, narrow enough not to
        // clip short dits.
        private const double OnFraction  = 0.50;
        private const double OffFraction = 0.35;

        // Presence gate, as peak over the mean noise level, with hysteresis.
        // Measured against the mean and not against a minimum tracker on purpose:
        // narrowband noise is Rayleigh, so its peak sits several times its own
        // minimum and a floor-referenced gate calls pure hiss a signal.
        //
        // Referencing the mean narrows that gap but does not close it. _peakEst
        // is a multi-second max-hold and _noiseMean a quarter-second mean, so
        // the two are not the same statistic and their ratio carries the peak
        // factor of the noise on its own: stationary hiss already measures 2.1
        // against a 3.0 gate, leaving 3.5 dB of margin, and any wander in the
        // level - AGC breathing, QSB, atmospheric crashes - spends it. This is
        // why the ratio cannot be the whole test; see the keying gate below.
        private const double PresentRatio = 3.0;
        private const double AbsentRatio  = 2.2;

        /// <summary>Lowest the mark reference may sit, as a multiple of the noise mean.</summary>
        private const double MarkFloorRatio = 2.5;

        /// <summary>
        /// How fast the mark level is allowed to fall while the key is up,
        /// in dB per second, floored at <see cref="MarkFloorRatio"/> x noise.
        ///
        /// Without this the mark level is a latch. It only updates while the
        /// key is down, and the key only goes down at half way from the noise
        /// up to it, so once a fade takes the signal more than 6 dB below the
        /// last mark nothing can key again and nothing can lower the bar. A
        /// 20 dB fade at 0.05 Hz falls at up to 3 dB/s, and at 5 WPM the word
        /// gaps are 5.5 s long, so one gap is enough to strand it - which is
        /// why fading alone cost 5 WPM 29 points of copy while 10 WPM, whose
        /// longest gap is 1.3 s, got through it almost untouched.
        /// </summary>
        private const double MarkDecayDbPerSec = 4.0;

        /// <summary>
        /// Bottom stop for that decay, as a multiple of the mean noise, and it
        /// is deliberately higher than <see cref="MarkFloorRatio"/>.
        ///
        /// The decay must not sink the bar under the noise. Keying turns on at
        /// half way from the noise up to the mark level, so a bottom stop of
        /// 2.5 puts the on-threshold at 1.75x the mean noise while hiss peaks
        /// at about 2.1x - the gate then keys on the band itself. Measured on
        /// 2026-08-27: at 4 dB/s with the 2.5 stop, 20 WPM at 3 dB fell from
        /// 72.5% to 65.5% and 5 WPM faded-and-noisy from 41.8% to 29.6%.
        /// </summary>
        private const double MarkDecayFloorRatio = 6.0;

        // Keying gate. On 2026-08-27 an empty 15m, with nothing audible on it,
        // read 13.7 dB, "signal", "locked" and 60 wpm, and turned bare hiss
        // into 93% chatter. Amplitude-modulated noise reproduces it exactly:
        // at 0.9 depth and 1 Hz, pure noise reads 13.3 dB and keys a third of
        // the time. No level test separates those, because nothing about the
        // level is different - what differs is duration.
        //
        // A dit is 20 ms even at 60 wpm, which is four hops, and its envelope
        // holds a flat top for all of them; a dah holds one three times longer
        // at any speed. A Rayleigh spike lasts one or two hops and has no top
        // at all. So a tone counts as present only once the envelope has
        // lately made several sustained excursions, which noise cannot fake.
        //
        // Acquiring is deliberately stricter than holding. Presence gates
        // KeyDown, so dropping it mid-over costs characters outright - which
        // is why the window is long and RunsOff is 1: a signal that has proved
        // itself keyed keeps presence across the gaps between overs, and only
        // a window with no sustained excursion at all in it gives presence up.
        //
        // Calibrated against three minutes of empty 15m and the bench corpus:
        // the empty band holds at 0% presence and 0 characters, while
        // mkii-dk9py keeps all 241 of its characters at 95% readable, which is
        // exactly its score before the gate existed. The margin is real but it
        // is not large - at a 12 s window the empty band breaks back through
        // at 17%, so lengthening this further needs re-measuring, not just a
        // bigger number.
        // What the window holds is the total length of the qualified runs, not
        // how many of them there were. Counting runs sounds like the same
        // question and is not: it measures how fast the other operator is
        // sending, and a fixed count in a fixed window therefore encodes a
        // minimum speed. At 5 WPM with Farnsworth spacing - which is how every
        // practice recording below about 15 WPM is sent - the ARRL W1AW file
        // produces almost exactly 10 qualified runs per ten seconds, sitting
        // on the old hold count of 10. Presence flapped, and because presence
        // forces the key up it took whole characters with it: of the 1012
        // marks in that file the detector reported 631, missing dits and dahs
        // at the same rate and in contiguous clumps rather than singly, which
        // is the signature of a gate opening and closing rather than of marks
        // being too weak.
        //
        // Time separates the two cases far better than count does, because a
        // qualified noise run is barely over MarkRunHops while a real mark is
        // several times it. Measured per ten-second window: three minutes of
        // empty 15m accumulates around 70 hops, the 5 WPM file around 320, and
        // anything faster very much more.
        private const int MarkRunHops      = 6;     // 30 ms: every dah to 90 wpm, dits to 40
        private const int KeyingWindowHops = 2000;  // 10 s of history
        private const int KeyingOpenHops   = 40;
        // The hold threshold is what actually rejects an empty band. Swept
        // 2026-08-27 in hops of qualified run inside the ten-second window,
        // against three minutes of recorded empty 15m, two dead-band probes,
        // and the signals the previous calibration used:
        //
        //   hops : probe15c   dk9py   cq-qso   5 wpm   10 wpm   40 wpm
        //     20 :   13 ch     246     118     79.0%   95.9%    99.4%
        //     30 :   13 ch     246     118     79.0%   95.9%    99.4%
        //     40 :   13 ch     246     118     79.0%   95.9%    99.4%
        //     50 :   13 ch     246     114     79.0%   95.9%    99.4%
        //     70 :    0 ch     246     113     79.0%   95.9%    99.4%
        //
        // noise-15m-long, diag-dead, diag-strong and probe15d emit nothing
        // anywhere in that range, and the ARRL files do not move at all: the
        // whole range is flat on accuracy and the only thing being traded is
        // a real QSO against a dead band. 30 sits in the middle of the flat
        // stretch rather than on either edge. Past 40 the QSO starts paying -
        // by 70, cq-then-qso has lost five characters - which is the same
        // "eating QSOs for no further gain" the old run-count sweep found at
        // its top end.
        //
        // The one recording that is not silent below 70 is probe15c, and it is
        // worth being precise about what it emits rather than reading 13 as a
        // failure. Over 65 seconds it produces "NA 3IEEE I HI" at confidence
        // 0.17, with no run of four characters anywhere and readability calling
        // it 92% Chatter. That is the classifier doing its job: the reader is
        // saying it can hear something and cannot read it, which is true, and
        // is a different thing from claiming a decode. Buying that last 13
        // characters costs a real QSO characters it currently gets right, so
        // it is not bought here.
        private const int KeyingHoldHops   = 30;

        // Grace. The gate cannot be the first thing that decides presence, or
        // it eats the start of every transmission: it needs several marks to
        // make up its mind and the first of them are the callsign. So the
        // level test opens the gate immediately and the keying gate is given
        // this long to confirm, taking presence away again if it cannot. On a
        // real signal confirmation arrives inside a couple of characters and
        // nothing is lost. On an empty band the cost is one burst of chatter
        // at the top of the session, after which the level test never falls
        // back below AbsentRatio, so the grace never re-arms and the band
        // stays quiet.
        private const int GraceHops = 1100;   // 5.5 s

        // How long the level has to stay down before the grace re-arms. A
        // momentary dip must not count: on an empty band snrLin crosses the
        // gate constantly, and re-arming on each dip hands noise a fresh grace
        // period every time. Three minutes of recorded empty 15m measured 94%
        // present with no delay at all and 23% at 3 s, because the grace kept
        // re-arming through the session; at 6 s it is 3%, which is the opening
        // burst and nothing after it. The cost is four characters on one bench
        // QSO. Between two overs the band goes properly quiet for longer than
        // this, so a real signal still re-arms for the next over.
        private const int ReArmHops = 1200;   // 6 s

        // Once a lock is this confident, the tone search stops roaming the
        // passband and follows the tone it has.
        //
        // Presence is the primary guard - an absent signal never tracks - and
        // this is the second one, because the grace period grants presence on
        // sight for several seconds and would otherwise pin acquisition to the
        // configured pitch at the start of every session.
        //
        // Measured 2026-08-27 over the bench recordings, per 5 s block:
        //
        //     empty band   noise-15m-long  median 0.61, max 0.75
        //                  probe15c        median 0.56, max 0.66
        //                  diag-dead       median 0.51, max 0.69
        //     real signal  mkii-dk9py      median 0.89
        //                  sp5xoc          median 0.91
        //                  cq-then-qso     median 0.83
        //
        // So 0.80: above everything an empty band reached, below what a
        // readable signal sits at. The distributions overlap at their tails,
        // and the overlap is deliberately resolved towards staying wide -
        // failing to narrow only means the search keeps hunting, which is what
        // it did before any of this existed.
        private const double TrackHoldConfidence = 0.80;

        // How far a tracked tone may be followed per FFT frame's search. Wide
        // enough for drift, QSB and a hand on the dial; far narrower than the
        // spacing between two stations sharing a passband.
        private const double TrackBandHz = 150.0;

        /// <summary>
        /// How much wider than the search window the spectrum display reaches,
        /// so a station just outside the range the reader will chase is still
        /// on screen as the explanation for why it is not being read.
        /// </summary>
        private const double SpectrumMargin = 1.5;

        private readonly CwToneDetectorOptions _opt;
        private readonly int      _decimation;
        private readonly double[] _fir;
        private readonly double[] _firDelay;
        private int _firPos;
        private int _decPhase;

        private readonly double[] _envBuf = new double[EnvWindow];
        private int _envFill;

        private readonly double[] _fftBuf = new double[FftSize];
        private readonly double[] _fftWin = new double[FftSize];
        private readonly double[] _re     = new double[FftSize];
        private readonly double[] _im     = new double[FftSize];
        private readonly double[] _mag    = new double[FftSize / 2];
        private int _fftFill;

        private long   _sampleIndex8k;
        private double _phasorPhase;
        private double _toneHz;
        private double _goertzelCoeff;
        private double _noiseMean;
        private double _peakEst;
        private double _markLevel;

        /// <summary>Amplitude factor per envelope hop for <see cref="MarkDecayDbPerSec"/>.</summary>
        private static readonly double MarkDecayPerHop =
            Math.Pow(10.0, -MarkDecayDbPerSec * (EnvHop / (double)WorkRate) / 20.0);
        private bool   _keyDown;

        // Debounce. _pending holds samples that cannot be emitted yet because a
        // later hop may still backdate their key state.
        private readonly List<CwToneSample> _pending = new();
        private int  _debounceHops;
        private bool _committedKey;
        private int  _runHops;
        // Rolling record of which hops ended a sustained excursion, and how
        // many are still inside the window, for the keying gate.
        private readonly int[] _markRuns = new int[KeyingWindowHops];
        private int _markRunPos;
        private int _markRunHops;
        private int _aboveRun;
        private int _levelHops;
        private int _quietHops;

        // Last passband spectrum, in dB above the median of its own span, for
        // the tuning display. See SpectrumMargin and CopySpectrum.
        private readonly double[] _specDb;
        private readonly int _specLo;
        private readonly int _specHi;
        private bool _specReady;

        private bool   _present;
        private double _confidence;
        private bool   _primed;
        private readonly long _warmupHops;

        public CwToneDetector(CwToneDetectorOptions? options = null)
        {
            _opt = options ?? new CwToneDetectorOptions();

            if (_opt.InputSampleRate % WorkRate != 0)
                throw new ArgumentException(
                    "InputSampleRate must be a whole multiple of " + WorkRate + ".",
                    nameof(options));

            _decimation = _opt.InputSampleRate / WorkRate;
            _fir        = BuildDecimationFir(_opt.InputSampleRate, FirTaps);
            _firDelay   = new double[FirTaps];

            for (int i = 0; i < FftSize; i++)
                _fftWin[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1));

            _warmupHops = (long)Math.Round(Math.Max(0.0, _opt.WarmupSeconds) * WorkRate / EnvHop);
            _debounceHops = (int)Math.Round(Math.Max(0.0, _opt.KeyDebounceMs) * WorkRate / (1000.0 * EnvHop));

            // The span the spectrum display covers, fixed at construction.
            //
            // Deliberately the whole acquire range plus a margin, and not the
            // range actually being searched: that narrows to TrackBandHz the
            // moment a lock is confident, and a display whose axis moves under
            // the operator while they are tuning is worse than no display. The
            // margin is there so a station just outside the search range is
            // still visible as the reason the reader is not hearing it.
            double binHz = (double)WorkRate / FftSize;
            double halfSpan = _opt.SearchWindowHz * SpectrumMargin;
            _specLo = Math.Max(1,             (int)Math.Floor((_opt.PitchHz - halfSpan) / binHz));
            _specHi = Math.Min(FftSize / 2 - 1, (int)Math.Ceiling((_opt.PitchHz + halfSpan) / binHz));
            if (_specHi < _specLo) _specHi = _specLo;
            _specDb = new double[_specHi - _specLo + 1];

            SetTone(_opt.PitchHz);
        }

        /// <summary>The tone currently being tracked, Hz.</summary>
        public double ToneHz => _toneHz;

        /// <summary>0..1 confidence in that tone.</summary>
        public double Confidence => _confidence;

        /// <summary>
        /// Feed one frame. Appends an observation to the output list for every
        /// completed hop, and returns how many were added.
        /// </summary>
        public int Process(ReadOnlySpan<float> samples, IList<CwToneSample> output)
        {
            int produced = 0;

            foreach (float s in samples)
            {
                // Anti-alias, then keep one sample in _decimation. The filter only
                // has to run on the samples we keep, which is the whole trick.
                _firDelay[_firPos] = s;
                _firPos = (_firPos + 1) % FirTaps;

                if (++_decPhase < _decimation) continue;
                _decPhase = 0;

                double acc = 0;
                int idx = _firPos;
                for (int t = 0; t < FirTaps; t++)
                {
                    acc += _fir[t] * _firDelay[idx];
                    idx = (idx + 1) % FirTaps;
                }

                produced += PushWorkSample(acc, output);
            }

            return produced;
        }

        private int PushWorkSample(double x, IList<CwToneSample> output)
        {
            _sampleIndex8k++;
            int produced = 0;

            // ---- pitch path -------------------------------------------------
            _fftBuf[_fftFill++] = x;
            if (_fftFill == FftSize)
            {
                UpdatePitch();
                Array.Copy(_fftBuf, FftHop, _fftBuf, 0, FftSize - FftHop);
                _fftFill = FftSize - FftHop;
            }

            // ---- envelope path ----------------------------------------------
            _envBuf[_envFill++] = x;
            if (_envFill == EnvWindow)
            {
                produced += Emit(MakeSample(), output);
                Array.Copy(_envBuf, EnvHop, _envBuf, 0, EnvWindow - EnvHop);
                _envFill = EnvWindow - EnvHop;
            }

            return produced;
        }

        /// <summary>
        /// Hold each sample for <c>_debounceHops</c> hops, so that a key-state
        /// run which turns out to be too short can be erased before anything
        /// downstream sees it - and a run which turns out to be real can have
        /// its transition backdated to the hop where it actually began.
        /// </summary>
        private int Emit(CwToneSample s, IList<CwToneSample> output)
        {
            int d = _debounceHops;
            if (d <= 0) { output.Add(s); return 1; }

            if (s.KeyDown == _committedKey) _runHops = 0;
            else _runHops++;

            // Queued carrying the committed state rather than its own. A run
            // that never reaches d hops is therefore never visible at all.
            _pending.Add(s with { KeyDown = _committedKey });

            if (_runHops >= d)
            {
                _committedKey = !_committedKey;

                // Backdate. The qualifying run is the last _runHops entries and
                // is still inside the buffer, so the flip lands on the first hop
                // that crossed rather than on the one that confirmed it.
                for (int k = _pending.Count - _runHops; k < _pending.Count; k++)
                    _pending[k] = _pending[k] with { KeyDown = _committedKey };

                _runHops = 0;
            }

            int produced = 0;
            while (_pending.Count > d)
            {
                output.Add(_pending[0]);
                _pending.RemoveAt(0);
                produced++;
            }
            return produced;
        }

        /// <summary>
        /// Emit whatever the debounce is still holding. Call at end of stream;
        /// without it the last few hops never appear.
        /// </summary>
        public int Flush(IList<CwToneSample> output)
        {
            int produced = _pending.Count;
            foreach (var p in _pending) output.Add(p);
            _pending.Clear();
            _runHops = 0;
            return produced;
        }

        private CwToneSample MakeSample()
        {
            double mag = Goertzel(_envBuf, EnvWindow, _goertzelCoeff);
            Phasor(out double phI, out double phQ);

            if (!_primed)
            {
                _noiseMean = mag;
                _peakEst   = mag;
                _markLevel = mag;
                _primed    = true;
            }

            // Peak: quick to rise, slow to fall, so QSB drags it down over seconds
            // rather than losing the signal on one fade.
            _peakEst += (mag > _peakEst ? 0.25 : 0.0015) * (mag - _peakEst);

            // Noise: tracked while the key is up, where the only thing present is
            // noise. A slow creep while the key is down stops a very long mark
            // freezing the estimate entirely.
            //
            // _keyDown alone is not a safe test for "the key is up", because it
            // is forced false whenever presence is false - so before the gate
            // opens, the signal gets averaged into the noise estimate at the
            // fast rate. That is self-sealing: a rising _noiseMean lifts midThr
            // below, mark runs stop qualifying, and the keying gate can never
            // reach the count that would grant presence. It is why no value of
            // KeyingRunsOn worked, and why the opening lost whole callsigns
            // rather than the handful of marks the gate nominally costs.
            //
            // So an unconfirmed sustained excursion protects the estimate too.
            //
            // The wait for MarkRunHops is deliberate, and the obvious tightening
            // of it is a trap worth recording.
            //
            // The first six hops - 30 ms - of every mark are still averaged in
            // at the fast rate, aimed at the full signal level. On the ARRL
            // W1AW practice files, where the tone runs 85 dB over the floor,
            // each leading edge drags the estimate around 11% of the way to the
            // signal, so it looks like it has to be costing marks. Dropping the
            // wait to "_aboveRun > 0" to close that hole was tried on
            // 2026-08-27 and is wrong twice over.
            //
            // It bought nothing. The 5 WPM file reported the same 631 marks out
            // of 1012 either way, so the leading edges were never the limiting
            // factor. The keying gate was - see MarkRunHops below.
            //
            // And it wrecks the empty band. On noise the magnitude crosses the
            // mid threshold on roughly half of all hops, so "_aboveRun > 0" is
            // true almost continuously, and the estimate freezes at the slow
            // rate instead of climbing to the noise it exists to measure. Every
            // hop then reads as signal. probe15c went from silent to 130
            // characters and its presence from 20% to 91%.
            //
            // Requiring a qualified run is exactly what separates the two
            // cases: a real mark's leading edge is followed by a run that goes
            // on to qualify, and a hiss peak's is not.
            bool inMark = _keyDown || _aboveRun >= MarkRunHops;
            _noiseMean += (inMark ? 0.0005 : 0.02) * (mag - _noiseMean);

            // Mark level: updated only while the key is down, so it follows the
            // signal itself rather than the duty cycle. This is what rides QSB.
            // The peak tracker is deliberately slow, which is right for deciding
            // whether a signal is there at all and wrong for setting a threshold:
            // during a fade a slow peak leaves the threshold stranded above the
            // signal, which is precisely the failure a fixed DEC LVL has.
            if (_keyDown) _markLevel += 0.05 * (mag - _markLevel);
            else if (MarkDecayDbPerSec > 0.0)
            {
                // Falls only. Math.Max against the floor on its own does not
                // stop the level dropping, it lifts it whenever the noise is
                // high relative to the last mark, which raises the keying bar
                // on exactly the noisy band that can least afford it - that
                // read as the floor sweep running backwards on 2026-08-27.
                double stop = MarkDecayFloorRatio * _noiseMean;
                if (_markLevel > stop)
                    _markLevel = Math.Max(_markLevel * MarkDecayPerHop, stop);
            }

            double noise = Math.Max(_noiseMean, 1e-12);
            double snrLin = _peakEst / noise;

            // Sustained-excursion count. The threshold sits midway between the
            // noise mean and the peak, which is where a mark's flat top lies
            // and where the tip of a noise spike does not.
            // Referenced to the mark level and not to the peak, for the same
            // reason _markLevel exists at all: _peakEst is a multi-second
            // max-hold, so through a fade it strands the threshold above the
            // signal that is still there. That cost 20 dB QSB tests whole
            // callsigns - the marks stopped qualifying, the window emptied and
            // presence dropped mid-transmission. The peak is still the ceiling,
            // so a stale mark level cannot lower the bar below what is real.
            //
            // The floor matters just as much as the ceiling. Grace grants a
            // couple of seconds of presence before the gate has decided, and
            // in those seconds _markLevel is learned from whatever is there -
            // on an empty band, noise. Left unfloored that pulls the bar down
            // to the noise, every hiss excursion qualifies, and the gate holds
            // presence on nothing: measured 99% on three minutes of empty 15m.
            // Floored at MarkFloorRatio the ceiling takes over instead, since
            // hiss peaks at about 2.1x its mean and the floor asks for 2.5x,
            // so on noise this collapses back to the strict peak reference.
            double markRef   = Math.Min(
                                   Math.Max(_markLevel, MarkFloorRatio * _noiseMean),
                                   _peakEst);
            double midThr    = _noiseMean + 0.5 * (markRef - _noiseMean);
            int qualifiedHops = 0;
            if (mag >= midThr) _aboveRun++;
            else
            {
                if (_aboveRun >= MarkRunHops) qualifiedHops = _aboveRun;
                _aboveRun = 0;
            }
            _markRunHops -= _markRuns[_markRunPos];
            _markRuns[_markRunPos] = qualifiedHops;
            _markRunHops += qualifiedHops;
            _markRunPos = (_markRunPos + 1) % KeyingWindowHops;

            bool levelOk = snrLin >= (_present ? AbsentRatio : PresentRatio);
            if (levelOk) { _levelHops++; _quietHops = 0; }
            else if (++_quietHops >= ReArmHops) _levelHops = 0;

            bool keying = _markRunHops >= (_present ? KeyingHoldHops : KeyingOpenHops);
            bool grace  = _levelHops <= GraceHops;

            _present = levelOk && (keying || grace);

            if (_present)
            {
                // Never let the reference collapse onto the noise: without this
                // floor the loop can chase its own tail down into the hiss.
                double reference = Math.Max(_markLevel, MarkFloorRatio * _noiseMean);
                double span   = reference - _noiseMean;
                double onThr  = _noiseMean + OnFraction  * span;
                double offThr = _noiseMean + OffFraction * span;
                if (!_keyDown && mag >= onThr) _keyDown = true;
                else if (_keyDown && mag < offThr) _keyDown = false;
            }
            else
            {
                _keyDown = false;
            }

            // Warm-up. The estimators above have been updating all along; what
            // is suppressed is any claim that something is keyed while they are
            // still converging.
            if (_sampleIndex8k < _warmupHops * EnvHop)
            {
                _present = false;
                _keyDown = false;
            }

            return new CwToneSample
            {
                TimeSeconds = (_sampleIndex8k - EnvWindow / 2.0) / WorkRate,
                KeyDown     = _keyDown,
                Magnitude   = mag,
                ToneHz      = _toneHz,
                SnrDb         = 20.0 * Math.Log10(Math.Max(snrLin, 1e-6)),
                Confidence    = _confidence,
                SignalPresent = _present,
                NoiseLevel    = _noiseMean,
                MarkLevel     = Math.Max(_markLevel, MarkFloorRatio * _noiseMean),
                PhasorI       = phI,
                PhasorQ       = phQ,
            };
        }

        private void UpdatePitch()
        {
            if (!_opt.TrackPitch) { _confidence = 1.0; return; }

            for (int i = 0; i < FftSize; i++)
            {
                _re[i] = _fftBuf[i] * _fftWin[i];
                _im[i] = 0.0;
            }
            Fft(_re, _im);

            int half = FftSize / 2;
            for (int i = 0; i < half; i++)
                _mag[i] = Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);

            CaptureSpectrum();

            // Acquire across everything the filter passes; track narrowly once
            // there is something to track. The wide search is what stops an
            // audible signal being invisible because it is not near the pitch,
            // and the narrow track is what stops a louder station elsewhere in
            // the passband stealing a lock that is already established - which
            // is the risk that wide searching would otherwise carry, and the
            // reason the search window used to be capped instead.
            //
            // Confidence, not presence, decides which mode this is in. Presence
            // is granted on sight for a couple of seconds while the keying gate
            // makes up its mind, so on an empty band it would pin acquisition
            // to the pitch for the first few seconds of every session.
            double binHz    = (double)WorkRate / FftSize;
            bool   tracking = _present && _confidence >= TrackHoldConfidence;
            double centre   = tracking ? _toneHz : _opt.PitchHz;
            double halfBand = tracking
                            ? Math.Min(TrackBandHz, _opt.SearchWindowHz)
                            : _opt.SearchWindowHz;

            int lo = Math.Max(1,        (int)Math.Floor((centre - halfBand) / binHz));
            int hi = Math.Min(half - 2, (int)Math.Ceiling((centre + halfBand) / binHz));
            if (hi <= lo) { _confidence = 0.0; return; }

            int    peakBin = lo;
            double peakMag = _mag[lo];
            double sum     = 0.0;
            for (int i = lo; i <= hi; i++)
            {
                sum += _mag[i];
                if (_mag[i] > peakMag) { peakMag = _mag[i]; peakBin = i; }
            }

            int    n    = hi - lo + 1;
            double rest = n > 1 ? (sum - peakMag) / (n - 1) : sum / n;

            // Prominence over the rest of the window is the confidence. A tone
            // stands well clear of it; noise does not.
            double prominence = rest > 1e-12 ? peakMag / rest : 0.0;
            double frameConfidence = Math.Clamp((prominence - 2.0) / 6.0, 0.0, 1.0);

            // Quick to believe a good frame, slow to forget one. Otherwise the
            // silence between overs would drag confidence to zero and zero-in
            // would refuse to answer at exactly the moment somebody presses it.
            //
            // Only while a tone is actually present, though. Prominence is a
            // shape measure - it says the peak stands clear of the rest of the
            // search window, not that there is anything in the window to begin
            // with. Flat noise has a peak too, and across a narrow window its
            // upper tail clears the mean often enough that the fast attack
            // ratchets confidence up and the slow decay then holds it there.
            // Three minutes of an empty 20m settled at 0.41 with not one
            // character decoded, which is a number an operator would read as
            // "nearly half sure" about a station that does not exist.
            //
            // Between overs _present is false and confidence decays slowly,
            // which is exactly what the asymmetry above was for. On an empty
            // band it never rises in the first place. Tone tracking below is
            // deliberately left alone: it keys off frameConfidence, not this,
            // so acquisition still works before presence is established.
            if (_present)
            {
                _confidence += (frameConfidence > _confidence ? 0.5 : 0.02)
                             * (frameConfidence - _confidence);
            }
            else if (frameConfidence < _confidence)
            {
                _confidence += 0.02 * (frameConfidence - _confidence);
            }

            if (frameConfidence <= 0.0) return;

            // Parabolic interpolation on the log magnitudes, which is the right
            // curve for a windowed peak and gets well inside one bin.
            double y0  = Math.Log(Math.Max(_mag[peakBin - 1], 1e-12));
            double y1  = Math.Log(Math.Max(_mag[peakBin],     1e-12));
            double y2  = Math.Log(Math.Max(_mag[peakBin + 1], 1e-12));
            double den = y0 - 2 * y1 + y2;
            double shift = Math.Abs(den) > 1e-12 ? 0.5 * (y0 - y2) / den : 0.0;
            shift = Math.Clamp(shift, -1.0, 1.0);

            double measured = (peakBin + shift) * binHz;
            measured = Math.Clamp(measured, centre - halfBand, centre + halfBand);

            // Weight the move by confidence, so a marginal frame nudges the tone
            // rather than jumping it.
            SetTone(_toneHz + 0.35 * frameConfidence * (measured - _toneHz));
        }

        /// <summary>
        /// Take a copy of the display span of the current FFT frame, in dB
        /// relative to the median of that span.
        ///
        /// Relative to the median rather than to an absolute level, because
        /// nothing upstream of here has a calibrated scale - the audio has
        /// been through the radio's AF gain, the codec and the browser - so an
        /// absolute dB figure would be a number with no units. The median is
        /// used rather than the mean because a strong carrier drags a mean up
        /// and flattens itself against its own noise floor; half the bins in a
        /// CW passband are floor even with a signal in it.
        /// </summary>
        private void CaptureSpectrum()
        {
            int n = _specHi - _specLo + 1;

            Span<double> sorted = stackalloc double[n];
            for (int i = 0; i < n; i++) sorted[i] = _mag[_specLo + i];
            sorted.Sort();
            double median = sorted[n / 2];
            if (median < 1e-12) median = 1e-12;

            for (int i = 0; i < n; i++)
                _specDb[i] = 20.0 * Math.Log10(Math.Max(_mag[_specLo + i], 1e-12) / median);

            _specReady = true;
        }

        /// <summary>
        /// The most recent passband spectrum, oldest-to-highest frequency, in
        /// dB above its own median. Returns 0 before the first FFT frame has
        /// been gathered, or when pitch tracking is off and no FFT is run.
        /// </summary>
        /// <param name="dest">
        /// Filled with up to its own length of bins. <see cref="SpectrumBins"/>
        /// is how many there are.
        /// </param>
        /// <param name="firstHz">Centre frequency of the first bin written.</param>
        /// <param name="binHz">Spacing between bins.</param>
        public int CopySpectrum(Span<double> dest, out double firstHz, out double binHz)
        {
            binHz   = (double)WorkRate / FftSize;
            firstHz = _specLo * binHz;

            if (!_specReady) return 0;

            int n = Math.Min(dest.Length, _specDb.Length);
            _specDb.AsSpan(0, n).CopyTo(dest);
            return n;
        }

        /// <summary>How many bins <see cref="CopySpectrum"/> will write.</summary>
        public int SpectrumBins => _specDb.Length;

        private void SetTone(double hz)
        {
            _toneHz = hz;
            _goertzelCoeff = 2.0 * Math.Cos(2.0 * Math.PI * hz / WorkRate);
        }

        /// <summary>
        /// Project the envelope window onto the operator's configured pitch and
        /// keep both parts, which the Goertzel above throws away when it takes
        /// a magnitude.
        ///
        /// The reference phase is anchored to the absolute sample index rather
        /// than to the start of the window, so it runs continuously across
        /// hops. That is what makes the result a tuning aid instead of noise: a
        /// tone exactly on the pitch holds one angle hop after hop, and a tone
        /// dF away advances by 2*pi*dF*hop each time, so it walks round the
        /// circle dF times a second - anticlockwise if it is above the pitch.
        ///
        /// Rotation faster than half the hop rate aliases, which here is 100 Hz
        /// - far outside the range anyone tunes by eye, and the FFT path
        /// reports the offset numerically anyway.
        /// </summary>
        private void Phasor(out double i, out double q)
        {
            double w = 2.0 * Math.PI * _opt.PitchHz / WorkRate;

            // _phasorPhase is the reference angle at the first sample of this
            // window, carried forward hop by hop. Accumulating it beats deriving
            // it from the absolute sample index, which is only the same angle
            // modulo 2*pi when the pitch is a whole number of Hz.
            double cr = Math.Cos(_phasorPhase), sr = Math.Sin(_phasorPhase);
            double cd = Math.Cos(w),            sd = Math.Sin(w);
            double si = 0.0, sq = 0.0;
            for (int k = 0; k < EnvWindow; k++)
            {
                double x = _envBuf[k];
                si += x * cr;
                sq -= x * sr;

                double nr = cr * cd - sr * sd;
                sr        = sr * cd + cr * sd;
                cr        = nr;
            }

            // Same 2/N scaling the Goertzel uses, so the phasor's radius is
            // comparable with Magnitude.
            i = si * 2.0 / EnvWindow;
            q = sq * 2.0 / EnvWindow;

            // On to the next window. Wrapped so it cannot drift off into the
            // range where a double stops resolving small angles.
            _phasorPhase = (_phasorPhase + w * EnvHop) % (2.0 * Math.PI);
        }

        private static double Goertzel(double[] buf, int n, double coeff)
        {
            double s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++)
            {
                double s = buf[i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s;
            }
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            return Math.Sqrt(Math.Max(power, 0.0)) * 2.0 / n;
        }

        /// <summary>
        /// Windowed-sinc low pass at 3.2 kHz, which is below the 4 kHz Nyquist of
        /// the 8 kHz working rate. Without this, hiss above 4 kHz folds straight
        /// down on top of the tone.
        /// </summary>
        private static double[] BuildDecimationFir(int inputRate, int taps)
        {
            var h = new double[taps];
            double fc = 3200.0 / inputRate;
            int m = taps - 1;
            double sum = 0;

            for (int i = 0; i < taps; i++)
            {
                double t = i - m / 2.0;
                double sinc = Math.Abs(t) < 1e-9
                    ? 2.0 * fc
                    : Math.Sin(2.0 * Math.PI * fc * t) / (Math.PI * t);
                double w = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / m);   // Hamming
                h[i] = sinc * w;
                sum += h[i];
            }

            for (int i = 0; i < taps; i++) h[i] /= sum;
            return h;
        }

        /// <summary>In-place iterative radix-2 FFT. Core takes no dependencies.</summary>
        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;

            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j |= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1.0, ci = 0.0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double xr = re[b] * cr - im[b] * ci;
                        double xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr;        im[a] += xi;
                        double nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }
    }
}
