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
    /// The figure is drawn, as on a terminal unit, from a limited signal: a
    /// band-pass over both tones, then a hard clipper, then the mark and space
    /// filters. The clipper holds the figure the same size through fading and
    /// makes the two arms equal length however unequal the tones arrive, which
    /// is what made the old display crisp. Without it every fade and noise
    /// burst smears the trace (measured on DDK9, 10.1008 MHz, 2026-09-23). The
    /// levels are taken from a second, unlimited pair of filters, because a
    /// clipped signal is always full scale and cannot say how much is there.
    ///
    /// Each filter lets a little of the other tone through, and what decides
    /// whether that tilts an arm is its phase, not how little there is. With
    /// two sections of a fixed width the leak arrived about 150 degrees round,
    /// so on tune each arm leaned 4 to 5 degrees, and making the mark arm flat
    /// and the space arm upright took two dial settings some 60 Hz apart, where
    /// MMTTY drew both straight at once (Bruce VK2RT, FTdx101D, 2026-10-28).
    /// Four sections now, each as wide as puts the other tone 270 degrees
    /// round - in quadrature with the tone the filter is for - so on tune the
    /// leak only opens the arm a hair and cannot lean it. Four sections of
    /// that width are about as wide overall as the two of 100 Hz were.
    ///
    /// What comes out is the filtered waveforms themselves, not a summary of
    /// them: at 2 kHz a tone needs many points per cycle to draw an ellipse
    /// rather than a polygon, so points are kept at half the input rate and the
    /// caller asks for the most recent few tens of milliseconds - one sweep of
    /// the scope - each time it redraws.
    ///
    /// Thread-safe: Process is expected on an audio thread and Snapshot on a
    /// request thread. The work done under the lock is seventeen biquad
    /// sections a sample, a few tens of microseconds per 10 ms frame.
    /// </summary>
    public sealed class RttyTuningScope
    {
        /// <summary>Points kept, at half the input rate: 200 ms at 48 kHz.</summary>
        public const int RingPoints = 4800;

        private const int Sections = 4;

        /// <summary>
        /// The limiter's channel is never narrower than this, so at a narrow
        /// shift both tones still sit well inside it.
        /// </summary>
        private const double NarrowestChannelHz = 200.0;

        private readonly object _gate = new();
        private readonly int _sampleRate;
        private readonly double _widestSectionHz;

        private readonly bool _limit;

        // Unlimited: the levels.
        private Bandpass _mark;
        private Bandpass _space;

        // Limited: the figure.
        private Bandpass _channel;
        private Bandpass _markLimited;
        private Bandpass _spaceLimited;

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
        /// <param name="widestSectionHz">
        /// The most a filter section may be widened to put the other tone in
        /// quadrature. At 170 Hz shift that needs about 135 to 155 Hz, which
        /// makes about 60 Hz overall - close to the tone filters in a 45-baud
        /// terminal unit. A wide shift would need far more, and gets this
        /// instead; its leak is then too small to lean anything.
        /// </param>
        /// <param name="limiter">
        /// Clip ahead of the filters that draw the figure, as a terminal unit
        /// did. Off, the figure follows the signal's level.
        /// </param>
        public RttyTuningScope(int sampleRate, double markHz, double spaceHz, double widestSectionHz = 200.0, bool limiter = true)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (widestSectionHz <= 0) throw new ArgumentOutOfRangeException(nameof(widestSectionHz));

            _sampleRate = sampleRate;
            _widestSectionHz = widestSectionHz;
            _limit = limiter;
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
                double markWidth  = QuadratureWidth(markHz, spaceHz);
                double spaceWidth = QuadratureWidth(spaceHz, markHz);
                _mark = new Bandpass(_sampleRate, markHz, markWidth, Sections);
                _space = new Bandpass(_sampleRate, spaceHz, spaceWidth, Sections);
                _markLimited = new Bandpass(_sampleRate, markHz, markWidth, Sections);
                _spaceLimited = new Bandpass(_sampleRate, spaceHz, spaceWidth, Sections);

                // Over both tones and no wider: the clipper shares its output
                // among everything it is given, so noise let in here takes
                // figure away from the signal. Twice the shift keeps both
                // tones well inside it and equally placed.
                double centre = (markHz + spaceHz) / 2.0;
                double width = Math.Max(2.0 * Math.Abs(spaceHz - markHz), NarrowestChannelHz);
                _channel = new Bandpass(_sampleRate, centre, Math.Min(width, 2.0 * centre - 1.0), sections: 1);
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

                    if (_limit)
                    {
                        double c = _channel.Next(s);
                        double clipped = c > 0 ? 1.0 : c < 0 ? -1.0 : 0.0;
                        x = _markLimited.Next(clipped);
                        y = _spaceLimited.Next(clipped);
                    }

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
        /// The section width at which a filter on <paramref name="centreHz"/>
        /// turns <paramref name="otherHz"/> 270 degrees round, over all four
        /// sections, or the widest allowed if that is short of it. Each
        /// section's phase there grows from 0 towards 90 degrees as the
        /// section narrows, so halving finds it.
        /// </summary>
        private double QuadratureWidth(double centreHz, double otherHz)
        {
            double target = 1.5 * Math.PI / Sections;
            if (Bandpass.PhaseAt(_sampleRate, centreHz, _widestSectionHz, otherHz) >= target)
                return _widestSectionHz;

            double narrow = 1.0, wide = _widestSectionHz;
            for (int i = 0; i < 50; i++)
            {
                double mid = (narrow + wide) / 2.0;
                if (Bandpass.PhaseAt(_sampleRate, centreHz, mid, otherHz) > target) narrow = mid;
                else wide = mid;
            }
            return (narrow + wide) / 2.0;
        }

        /// <summary>
        /// Cascaded RBJ band-pass sections, 0 dB at the centre. The tone
        /// filters use four, which with their quadrature width put the other
        /// tone of a 170 Hz pair about 33 dB down; the limiter's channel
        /// filter uses one, where gentle skirts are wanted.
        /// </summary>
        private sealed class Bandpass
        {
            private readonly double _b0, _a1, _a2;
            private readonly double[] _state;   // x1, x2, y1, y2 per section

            public Bandpass(int sampleRate, double centreHz, double bandwidthHz, int sections)
            {
                (_b0, _a1, _a2) = Coefficients(sampleRate, centreHz, bandwidthHz);
                _state = new double[4 * sections];
            }

            private static (double b0, double a1, double a2) Coefficients(int sampleRate, double centreHz, double bandwidthHz)
            {
                double w0 = 2.0 * Math.PI * centreHz / sampleRate;
                double alpha = Math.Sin(w0) * bandwidthHz / (2.0 * centreHz);
                double a0 = 1.0 + alpha;
                return (alpha / a0, -2.0 * Math.Cos(w0) / a0, (1.0 - alpha) / a0);   // b1 = 0, b2 = -b0
            }

            /// <summary>How far round one section turns a tone at <paramref name="hz"/>, radians, 0 to pi/2.</summary>
            public static double PhaseAt(int sampleRate, double centreHz, double bandwidthHz, double hz)
            {
                var (b0, a1, a2) = Coefficients(sampleRate, centreHz, bandwidthHz);
                var z1 = System.Numerics.Complex.FromPolarCoordinates(1.0, -2.0 * Math.PI * hz / sampleRate);
                var h = b0 * (1.0 - z1 * z1) / (1.0 + a1 * z1 + a2 * z1 * z1);
                return Math.Abs(h.Phase);
            }

            public double Next(double x)
            {
                var s = _state;
                for (int k = 0; k < s.Length; k += 4)
                {
                    double y = _b0 * (x - s[k + 1]) - _a1 * s[k + 2] - _a2 * s[k + 3];
                    s[k + 1] = s[k]; s[k] = x; s[k + 3] = s[k + 2]; s[k + 2] = y;
                    x = y;
                }
                return x;
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
