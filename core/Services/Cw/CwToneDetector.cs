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
        // minimum and a floor-referenced gate calls pure hiss a signal. It does
        // not sit far above its mean, which is what makes this test work.
        private const double PresentRatio = 3.0;
        private const double AbsentRatio  = 2.2;

        /// <summary>Lowest the mark reference may sit, as a multiple of the noise mean.</summary>
        private const double MarkFloorRatio = 2.5;

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
        private double _toneHz;
        private double _goertzelCoeff;
        private double _noiseMean;
        private double _peakEst;
        private double _markLevel;
        private bool   _keyDown;
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
                output.Add(MakeSample());
                produced++;
                Array.Copy(_envBuf, EnvHop, _envBuf, 0, EnvWindow - EnvHop);
                _envFill = EnvWindow - EnvHop;
            }

            return produced;
        }

        private CwToneSample MakeSample()
        {
            double mag = Goertzel(_envBuf, EnvWindow, _goertzelCoeff);

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
            _noiseMean += (_keyDown ? 0.0005 : 0.02) * (mag - _noiseMean);

            // Mark level: updated only while the key is down, so it follows the
            // signal itself rather than the duty cycle. This is what rides QSB.
            // The peak tracker is deliberately slow, which is right for deciding
            // whether a signal is there at all and wrong for setting a threshold:
            // during a fade a slow peak leaves the threshold stranded above the
            // signal, which is precisely the failure a fixed DEC LVL has.
            if (_keyDown) _markLevel += 0.05 * (mag - _markLevel);

            double noise = Math.Max(_noiseMean, 1e-12);
            double snrLin = _peakEst / noise;

            if (!_present && snrLin >= PresentRatio) _present = true;
            else if (_present && snrLin < AbsentRatio) _present = false;

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

            double binHz = (double)WorkRate / FftSize;
            int lo = Math.Max(1,        (int)Math.Floor((_opt.PitchHz - _opt.SearchWindowHz) / binHz));
            int hi = Math.Min(half - 2, (int)Math.Ceiling((_opt.PitchHz + _opt.SearchWindowHz) / binHz));
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
            _confidence += (frameConfidence > _confidence ? 0.5 : 0.02)
                         * (frameConfidence - _confidence);

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
            measured = Math.Clamp(measured,
                                  _opt.PitchHz - _opt.SearchWindowHz,
                                  _opt.PitchHz + _opt.SearchWindowHz);

            // Weight the move by confidence, so a marginal frame nudges the tone
            // rather than jumping it.
            SetTone(_toneHz + 0.35 * frameConfidence * (measured - _toneHz));
        }

        private void SetTone(double hz)
        {
            _toneHz = hz;
            _goertzelCoeff = 2.0 * Math.Cos(2.0 * Math.PI * hz / WorkRate);
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
