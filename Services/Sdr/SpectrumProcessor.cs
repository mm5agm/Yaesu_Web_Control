// Yaesu Web Control – Spectrum Processor
//
// Implements the SDR-style DSP pipeline from the spectrum redesign design
// document:
//
//   5.1 Compute FFT
//   5.2 Apply window function (Hann)
//   5.3 Apply continuous pre-dB gain G
//   5.4 Convert magnitudes to dB
//   5.5 Subtract reference level
//   5.6 Clamp to display range
//   5.7 Apply averaging / smoothing (EMA)
//
// One instance per spectrum panel (per VFO). The instance carries the EMA
// state across frames; create once per device, call ComputeSpectrum on each
// new IQ buffer.

using MathNet.Numerics.IntegralTransforms;

namespace Yaesu_Web_Control.Services.Sdr
{
    internal sealed class SpectrumProcessor
    {
        // Per-instance EMA history. Null until the first frame establishes the size.
        private float[]? _ema;

        // Hann window cache — pure function of fftSize, shared across instances.
        private static readonly Dictionary<int, float[]> _hannWindowCache = new();

        // ── DSP configuration (mutable; read every frame) ────────────────────

        /// <summary>Linear gain applied to FFT magnitudes BEFORE log conversion (design doc §4.1).</summary>
        public float GainLinear { get; set; } = 1.0f;

        /// <summary>dB subtracted from each bin AFTER log conversion (design doc §4.2).</summary>
        public float RefLevelDb { get; set; } = 0.0f;

        /// <summary>Lower display clamp (dB). Bins below this are pinned to this floor.</summary>
        public float DbFloor { get; set; } = -120f;

        /// <summary>Upper display clamp (dB). Bins above this are pinned to this ceiling.</summary>
        public float DbCeiling { get; set; } = 0f;

        /// <summary>
        /// EMA smoothing factor. 0.0 = output frozen at last frame, 1.0 = no smoothing.
        /// Default 0.3 is a starting point — tune against the SDR Console reference shots.
        /// </summary>
        public float EmaAlpha { get; set; } = 0.3f;

        /// <summary>
        /// Run the full pipeline on one IQ buffer.
        /// </summary>
        /// <param name="iqSamples">(I, Q) interleaved floats, length ≥ fftSize × 2.</param>
        /// <param name="fftSize">Power of two.</param>
        /// <returns>FFT-shifted dB array of length fftSize (bin 0 = −SR/2, last = +SR/2).</returns>
        public float[] ComputeSpectrum(float[] iqSamples, int fftSize)
        {
            float[] window  = GetHannWindow(fftSize);
            var     complex = new MathNet.Numerics.Complex32[fftSize];

            // 5.2 windowing + load IQ into complex buffer.
            for (int i = 0; i < fftSize; i++)
            {
                float w = window[i];
                complex[i] = new MathNet.Numerics.Complex32(
                    iqSamples[i * 2]     * w,
                    iqSamples[i * 2 + 1] * w);
            }

            // 5.1 forward FFT. AsymmetricScaling = we normalise manually below
            // so 0 dB = full-scale signal.
            Fourier.Forward(complex, FourierOptions.AsymmetricScaling);

            // Capture the EMA buffer into a local before the loop. ResetSmoothing()
            // runs on the control-channel thread (a DSP/gain change) and sets _ema
            // to null; without this capture a reset landing between the null-check
            // and the loop below would NRE the streaming thread mid-frame and crash
            // the worker (observed when the Gain slider is dragged rapidly). Using a
            // local means an in-flight frame always finishes against a valid buffer;
            // a concurrent reset simply takes effect on the next frame.
            float[]? ema = _ema;
            if (ema is null || ema.Length != fftSize)
            {
                ema  = new float[fftSize];
                _ema = ema;
            }

            float[] bins   = new float[fftSize];
            float   invN   = 1.0f / fftSize;
            float   gain   = GainLinear;
            float   refDb  = RefLevelDb;
            float   lo     = DbFloor;
            float   hi     = DbCeiling;
            float   alpha  = EmaAlpha;
            float   keep   = 1.0f - alpha;

            for (int i = 0; i < fftSize; i++)
            {
                // FFT shift: bin 0 = most-negative frequency, last bin = most-positive.
                int   si  = (i + fftSize / 2) % fftSize;

                // 5.3 pre-dB gain applied to the linear magnitude.
                float mag = complex[si].Magnitude * invN * gain;

                // 5.4 magnitude → dB; +1e-10 guards log10(0).
                // 5.5 subtract reference level in the same expression.
                float db  = 20.0f * MathF.Log10(mag + 1e-10f) - refDb;

                // 5.6 clamp to display range.
                if      (db < lo) db = lo;
                else if (db > hi) db = hi;

                // 5.7 EMA: y[n] = α·x[n] + (1−α)·y[n−1].
                float smoothed = alpha * db + keep * ema[i];
                ema[i]   = smoothed;
                bins[i]  = smoothed;
            }

            return bins;
        }

        /// <summary>Reset EMA history. Call after a settings change (sample rate,
        /// span, antenna) so the smoothing doesn't visibly bleed pre-change levels
        /// into the new spectrum.</summary>
        public void ResetSmoothing() => _ema = null;

        // ── Private helpers ──────────────────────────────────────────────────

        private static float[] GetHannWindow(int size)
        {
            if (_hannWindowCache.TryGetValue(size, out float[]? cached))
                return cached;

            var window = new float[size];
            float denom = size - 1;
            for (int i = 0; i < size; i++)
                window[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / denom));

            _hannWindowCache[size] = window;
            return window;
        }
    }
}
