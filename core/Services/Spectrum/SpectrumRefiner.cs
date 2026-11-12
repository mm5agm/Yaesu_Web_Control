using System;

namespace RadioWebControl.Core.Services.Spectrum
{
    /// <summary>
    /// Turns a radio's raw scope sweeps into a refined trace for a person to
    /// look at: each bin averaged over the last few sweeps, so the noise floor
    /// settles into a thin line and weak signals stand out of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A radio's own scope sends each sweep as a separate snapshot, so one sweep
    /// has a rough floor. An SDR's frames come already averaged by its worker; a
    /// radio's do not. Measured on 2026-11-12 on 20m FT8 at a 20 kHz span, with
    /// the "peaks over floor roughness" ratio used for the FTdx101's ACC scope:
    /// </para>
    /// <list type="bullet">
    ///   <item>IC-7300 MkII, CI-V at 19200 baud, 3.2 sweeps a second, REF +10 dB:
    ///   raw 15.3; weight 0.5 gave 23.4 and 0.3 gave 28.9.</item>
    ///   <item>FTdx101 ACC scope, 25 frames a second (the YaesuAccScope library's
    ///   ScopeRefiner, the same method): raw 8.8, weight 0.3 gave 21.5.</item>
    /// </list>
    /// <para>
    /// The weight is how much each new sweep counts. At F sweeps a second a
    /// weight w takes about 1/(F·w) seconds to follow a change and smooths the
    /// floor about as much as averaging 2/w − 1 sweeps would, so a slow scope
    /// wants a lighter weight than a fast one: each app picks its own steps.
    /// </para>
    /// <para>
    /// The average follows the radio: when the centre moves the trace moves with
    /// it by whole bins, so a signal stays sharp while tuning instead of
    /// smearing. A new span, a different number of bins, or a move of more than
    /// half a span starts the average again. Use one refiner per stream of
    /// sweeps; a stream that hops between bands (a cross-band peek) gets its own.
    /// </para>
    /// <para>
    /// The raw sweep is for anything that measures; the refined trace is for eyes.
    /// </para>
    /// </remarks>
    public sealed class SpectrumRefiner
    {
        private float _weight;
        private float[]? _average;
        private double _spanHz;
        private double _alignedCentreHz;

        /// <param name="weight">
        /// How much each new sweep counts, more than 0 and at most 1. Smaller is
        /// smoother and slower to follow; 1 is the raw sweeps.
        /// </param>
        public SpectrumRefiner(double weight)
        {
            _weight = CheckWeight(weight);
        }

        /// <summary>How much each new sweep counts.</summary>
        public double Weight
        {
            get => _weight;
            // A new weight carries on from the current average: no jump in the trace.
            set => _weight = CheckWeight(value);
        }

        /// <summary>
        /// Adds a sweep (bins low frequency first, any scale, usually dB) and
        /// returns the refined trace: the same length and scale. The array is a
        /// new one each time, so it can be kept or sent. A weight of 1 returns a
        /// copy of the sweep.
        /// </summary>
        public float[] Add(float[] bins, double centreHz, double spanHz)
        {
            if (bins == null) throw new ArgumentNullException(nameof(bins));

            if (bins.Length < 2 || !(spanHz > 0))
            {
                // Nowhere to put it: show it as it is and start again after.
                Reset();
                return (float[])bins.Clone();
            }

            if (_average == null || _average.Length != bins.Length || spanHz != _spanHz || !Follow(bins, centreHz, spanHz))
            {
                _average = (float[])bins.Clone();
                _spanHz = spanHz;
                _alignedCentreHz = centreHz;
                return (float[])bins.Clone();
            }

            var avg = _average;
            float w = _weight;
            for (int i = 0; i < avg.Length; i++) avg[i] += w * (bins[i] - avg[i]);
            return (float[])avg.Clone();
        }

        /// <summary>Forgets the average, so the next sweep starts it again.</summary>
        public void Reset()
        {
            _average = null;
            _spanHz = 0;
            _alignedCentreHz = 0;
        }

        /// <summary>
        /// Moves the average to where the new sweep sits, by whole bins, and says
        /// whether it could: false when it has moved more than half a span. Bins
        /// that come into view take the new sweep's levels.
        /// </summary>
        private bool Follow(float[] bins, double centreHz, double spanHz)
        {
            double hzPerBin = spanHz / (bins.Length - 1);
            int shift = (int)Math.Round((centreHz - _alignedCentreHz) / hzPerBin);
            if (shift == 0) return true;
            if (Math.Abs(shift) > bins.Length / 2) return false;

            var old = _average!;
            var moved = new float[old.Length];
            for (int i = 0; i < moved.Length; i++)
            {
                int from = i + shift;
                moved[i] = from >= 0 && from < old.Length ? old[from] : bins[i];
            }
            _average = moved;
            // Keep the part of a bin the shift didn't cover, so slow tuning
            // still moves the trace once it adds up to a bin.
            _alignedCentreHz += shift * hzPerBin;
            return true;
        }

        private static float CheckWeight(double weight)
        {
            if (!(weight > 0 && weight <= 1))
                throw new ArgumentOutOfRangeException(nameof(weight), weight, "The weight must be more than 0 and at most 1.");
            return (float)weight;
        }
    }
}
