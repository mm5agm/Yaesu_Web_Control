using System;

namespace RadioWebControl.Core.Services.Rtty
{
    /// <summary>
    /// The RTTY crossed-ellipse tuning scope, as it used to be wired: the audio
    /// goes through a mark filter and a space filter, the mark filter drives the
    /// X plates and the space filter the Y plates.
    ///
    ///   on tune         mark draws a flat line across, space a line up and
    ///                   down: a cross, each arm a thin ellipse
    ///   off tune        each tone gets through both filters, with a phase
    ///                   difference between them, so the arms tilt and open
    ///                   into fat ellipses leaning towards each other
    ///   wrong shift     one arm is right and the other is a blob
    ///
    /// So the operator tunes for the cross, exactly as on the scope, and which
    /// tone is mark does not matter to the picture - swapping them only swaps
    /// which arm is which.
    ///
    /// What comes out is the filtered waveforms themselves, not a summary of
    /// them: at 2 kHz a tone needs many points per cycle to draw an ellipse
    /// rather than a polygon, so points are kept at half the input rate and the
    /// caller asks for the most recent few tens of milliseconds - one sweep of
    /// the scope - each time it redraws.
    ///
    /// Thread-safe: Process is expected on an audio thread and Snapshot on a
    /// request thread. The work done under the lock is four biquads a sample,
    /// a few microseconds per 10 ms frame.
    /// </summary>
    public sealed class RttyTuningScope
    {
        /// <summary>Points kept, at half the input rate: 200 ms at 48 kHz.</summary>
        public const int RingPoints = 4800;

        private readonly object _gate = new();
        private readonly int _sampleRate;
        private readonly double _bandwidthHz;

        private Bandpass _mark;
        private Bandpass _space;

        private readonly float[] _x = new float[RingPoints];
        private readonly float[] _y = new float[RingPoints];
        private long _written;
        private bool _odd;

        // Mean square of each filter output and of the input, smoothed over
        // about 50 ms: enough to read steadily, short enough to follow a fade.
        private readonly double _levelAlpha;
        private double _markPower, _spacePower, _inputPower;

        /// <param name="sampleRate">Input rate in Hz.</param>
        /// <param name="markHz">Mark tone, audio Hz.</param>
        /// <param name="spaceHz">Space tone, audio Hz.</param>
        /// <param name="bandwidthHz">
        /// Width of each filter section. Two sections are cascaded, so the
        /// overall -3 dB width is about two thirds of this. 100 Hz gives about
        /// 65 Hz overall - close to the tone filters in a 45-baud terminal
        /// unit, which is what makes the arms of the cross thin.
        /// </param>
        public RttyTuningScope(int sampleRate, double markHz, double spaceHz, double bandwidthHz = 100.0)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (bandwidthHz <= 0) throw new ArgumentOutOfRangeException(nameof(bandwidthHz));

            _sampleRate = sampleRate;
            _bandwidthHz = bandwidthHz;
            _levelAlpha = 1.0 - Math.Exp(-1.0 / (0.05 * sampleRate));
            SetTones(markHz, spaceHz);
        }

        public double MarkHz  { get; private set; }
        public double SpaceHz { get; private set; }

        /// <summary>Output points per second: half the input rate.</summary>
        public int PointRate => _sampleRate / 2;

        /// <summary>
        /// Move the filters. Their state is reset, so there is a few
        /// milliseconds of settling, which is invisible on a live display.
        /// </summary>
        public void SetTones(double markHz, double spaceHz)
        {
            double nyquist = _sampleRate / 2.0;
            if (markHz <= 0 || markHz >= nyquist) throw new ArgumentOutOfRangeException(nameof(markHz));
            if (spaceHz <= 0 || spaceHz >= nyquist) throw new ArgumentOutOfRangeException(nameof(spaceHz));

            lock (_gate)
            {
                MarkHz = markHz;
                SpaceHz = spaceHz;
                _mark = new Bandpass(_sampleRate, markHz, _bandwidthHz);
                _space = new Bandpass(_sampleRate, spaceHz, _bandwidthHz);
            }
        }

        public void Process(ReadOnlySpan<float> audio)
        {
            lock (_gate)
            {
                for (int n = 0; n < audio.Length; n++)
                {
                    double s = audio[n];
                    double x = _mark.Next(s);
                    double y = _space.Next(s);

                    _markPower  += _levelAlpha * (x * x - _markPower);
                    _spacePower += _levelAlpha * (y * y - _spacePower);
                    _inputPower += _levelAlpha * (s * s - _inputPower);

                    // Every other sample. The filters have removed everything
                    // above about 2.4 kHz by now, so this is not aliasing
                    // anything the display could show.
                    _odd = !_odd;
                    if (_odd) continue;

                    int i = (int)(_written % RingPoints);
                    _x[i] = (float)x;
                    _y[i] = (float)y;
                    _written++;
                }
            }
        }

        /// <summary>
        /// The most recent <paramref name="count"/> points, oldest first,
        /// interleaved x0, y0, x1, y1... Fewer if fewer have been written.
        /// </summary>
        public RttyScopeFrame Snapshot(int count)
        {
            lock (_gate)
            {
                int n = (int)Math.Min(Math.Clamp(count, 0, RingPoints), _written);
                var xy = new float[n * 2];
                long from = _written - n;
                for (int k = 0; k < n; k++)
                {
                    int i = (int)((from + k) % RingPoints);
                    xy[k * 2]     = _x[i];
                    xy[k * 2 + 1] = _y[i];
                }

                return new RttyScopeFrame(
                    MarkHz, SpaceHz, xy,
                    Db(_markPower), Db(_spacePower), Db(_inputPower));
            }
        }

        /// <summary>RMS level in dBFS, with a floor so silence is a number.</summary>
        private static double Db(double meanSquare) =>
            10.0 * Math.Log10(Math.Max(meanSquare, 1e-12));

        /// <summary>
        /// Two cascaded RBJ band-pass sections, 0 dB at the centre. One section
        /// alone has skirts too wide at a 170 Hz shift: each filter would pass
        /// the other tone only about 11 dB down, and the arms of the cross
        /// would be visibly open. Two sections put it about 22 dB down.
        /// </summary>
        private struct Bandpass
        {
            private readonly double _b0, _a1, _a2;
            private double _x1a, _x2a, _y1a, _y2a;
            private double _x1b, _x2b, _y1b, _y2b;

            public Bandpass(int sampleRate, double centreHz, double bandwidthHz)
            {
                double w0 = 2.0 * Math.PI * centreHz / sampleRate;
                double alpha = Math.Sin(w0) * bandwidthHz / (2.0 * centreHz);
                double a0 = 1.0 + alpha;
                _b0 = alpha / a0;                 // b1 = 0, b2 = -b0
                _a1 = -2.0 * Math.Cos(w0) / a0;
                _a2 = (1.0 - alpha) / a0;
                _x1a = _x2a = _y1a = _y2a = 0;
                _x1b = _x2b = _y1b = _y2b = 0;
            }

            public double Next(double x)
            {
                double y = _b0 * (x - _x2a) - _a1 * _y1a - _a2 * _y2a;
                _x2a = _x1a; _x1a = x; _y2a = _y1a; _y1a = y;

                double z = _b0 * (y - _x2b) - _a1 * _y1b - _a2 * _y2b;
                _x2b = _x1b; _x1b = y; _y2b = _y1b; _y1b = z;
                return z;
            }
        }
    }

    /// <summary>One redraw's worth of the scope.</summary>
    /// <param name="Points">Interleaved x, y: mark filter, space filter.</param>
    /// <param name="MarkDb">Mark filter output, dBFS RMS.</param>
    /// <param name="SpaceDb">Space filter output, dBFS RMS.</param>
    /// <param name="InputDb">Whole audio passband, dBFS RMS.</param>
    public sealed record RttyScopeFrame(
        double MarkHz, double SpaceHz, float[] Points,
        double MarkDb, double SpaceDb, double InputDb);
}
