// Spectrum probe — a numeric health check on the IQ stream, logged once per
// worker start.
//
// Why it is permanent rather than a throwaway. The bins that reach the browser
// have been through SpectrumProcessor's gain, dB clamp and EMA smoothing, so
// they cannot be measured; and outside the tuner's passband the trace is ADC
// quantisation noise, which is flat whatever the analogue filter is doing. A
// screenshot therefore cannot distinguish a 200 kHz filter from a 1.5 MHz one,
// nor a residual DC offset from a real carrier at the tuned frequency. Hours
// went into reading pictures that could not answer the question; four numbers
// answered it immediately.
//
// Leaving it on means every log already carries the evidence if the centre
// spike ever comes back — nobody has to think to switch anything on first.
// The cost is one extra FFT per frame for the first few seconds of a worker's
// life, then nothing.
//
// The companion switch is YWC_SDR_FORCE_IF_ZERO=1 (see SdrplayDevice), which
// reruns the identical tune plan in zero-IF. That pair is the A/B that proves
// low-IF down-conversion is actually engaging.

using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Yaesu_Web_Control.Workers.Sdr;

internal sealed class SpectrumProbe
{
    private readonly int      _fftSize;
    private readonly int      _targetFrames;
    private readonly int      _skipFrames;   // let the DC estimate settle first
    private readonly float[]  _window;
    private readonly double[] _power;        // accumulated |X[k]|^2, FFT-shifted
    private int  _skipped;
    private int  _frames;
    private bool _reported;

    public SpectrumProbe(int fftSize, int targetFrames = 64, int skipFrames = 300)
    {
        _fftSize      = fftSize;
        _targetFrames = targetFrames;
        _skipFrames   = skipFrames;
        _power        = new double[fftSize];
        _window       = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (fftSize - 1)));
    }

    /// <summary>
    /// Accumulate one frame, using the same DC estimate the display just
    /// subtracted so this measures the corrected stream rather than the raw
    /// one. Returns true on the frame that completes the average.
    /// </summary>
    public bool Add(float[] iq, float dcI, float dcQ)
    {
        if (_reported) return false;
        // The DC blocker needs a few seconds to converge; measuring during that
        // would report the transient instead of the steady state.
        if (_skipped < _skipFrames) { _skipped++; return false; }

        var c = new Complex32[_fftSize];
        for (int i = 0; i < _fftSize; i++)
        {
            float w = _window[i];
            c[i] = new Complex32((iq[i * 2] - dcI) * w, (iq[i * 2 + 1] - dcQ) * w);
        }
        Fourier.Forward(c, FourierOptions.AsymmetricScaling);

        int half = _fftSize / 2;
        for (int i = 0; i < _fftSize; i++)
        {
            // Same shift the display uses: bin 0 is the most negative offset,
            // bin fftSize/2 is DC — the tuned frequency, where the spike sat.
            double m = c[(i + half) % _fftSize].Magnitude;
            _power[i] += m * m;
        }

        return ++_frames >= _targetFrames;
    }

    /// <summary>One-shot report. Levels are uncalibrated dBFS; only the
    /// differences between bins mean anything, which is all that is needed.</summary>
    public IEnumerable<string> Format(double spanHz, long centreHz)
    {
        _reported = true;

        int    n        = _fftSize;
        int    half     = n / 2;
        double hzPerBin = spanHz / n;

        // 1/N for the transform, 1/frames for the average, and Hann's 0.5
        // coherent gain squared. Absolute level is not the point; this only
        // keeps the numbers in a readable range.
        double   norm = 1.0 / (_frames * (double)n * n * 0.25);
        double[] db   = new double[n];
        for (int i = 0; i < n; i++)
            db[i] = 10.0 * Math.Log10(_power[i] * norm + 1e-30);

        // The median bin is the noise floor by definition — more than half the
        // span is noise in every case that matters, which makes it a stabler
        // reference than the mean, and a strong carrier cannot drag it up.
        double[] sorted = (double[])db.Clone();
        Array.Sort(sorted);
        double floorDb = sorted[n / 2];

        yield return $"PROBE frames={_frames} fft={n} centre={centreHz} Hz span={spanHz:0} Hz " +
                     $"bin={hzPerBin:0.0} Hz floor={floorDb:0.0} dBFS";

        // THE number to watch. Much above about 3 dB means the centre spike is
        // back: either the tune plan stopped matching one of the API's
        // documented fsHz/bwType/ifType low-IF combinations, or the DC blocker
        // in SpectrumProcessor is not converging.
        yield return $"PROBE centre excess {db[half] - floorDb:+0.0;-0.0} dB " +
                     $"(neighbours {db[half - 1] - floorDb:+0.0;-0.0} / " +
                     $"{db[half + 1] - floorDb:+0.0;-0.0})";

        // Filter shape: 32 buckets across the span, each the mean level
        // relative to the noise floor, on one line so the shape reads as a row
        // of numbers. A working analogue filter shows as a raised plateau with
        // shoulders; wide open shows as flat.
        const int buckets = 32;
        int       per     = n / buckets;
        var       shape   = new List<string>(buckets);
        for (int b = 0; b < buckets; b++)
        {
            double sum = 0;
            for (int k = b * per; k < (b + 1) * per; k++) sum += db[k];
            shape.Add($"{sum / per - floorDb:+0;-0;0}");
        }
        yield return "PROBE shape(dB rel floor, low to high across span): " + string.Join(" ", shape);

        // Strongest bins, so a real signal can be told from a spike by where it
        // sits and whether it moves when the radio is retuned.
        var top = Enumerable.Range(0, n)
                            .OrderByDescending(i => db[i])
                            .Take(5)
                            .Select(i => $"{(i - half) * hzPerBin:+0;-0;0}Hz={db[i]:0.0}dB");
        yield return "PROBE strongest: " + string.Join("  ", top);
    }
}
