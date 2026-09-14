namespace RadioWebControl.Core.Services.Spectrum
{
    /// <summary>
    /// Cuts a narrow window out of a wide FFT so a spectrum panel can show a
    /// few kHz without the SDR hardware being retuned for it.
    ///
    /// Why this exists. Below a certain span the only way to zoom by
    /// reconfiguring the receiver is to decimate harder, and every step of
    /// that is a stream restart: blank trace, waterfall reset, and on some
    /// drivers a chance to leak the device. The alternative is to hold the
    /// hardware at one fixed rate, run a large FFT so the bins are fine enough,
    /// and treat the span as a display property — pick the bins under the
    /// window and send only those. A span change is then a control message
    /// rather than a respawn.
    ///
    /// Pure arithmetic on a bin array; nothing in here knows what radio or
    /// SDR the bins came from, which is why it lives in core.
    /// </summary>
    public static class SpectrumZoom
    {
        /// <summary>One cropped view of a wider spectrum.</summary>
        /// <param name="Bins">The bins under the window, in the same order as the source.</param>
        /// <param name="CentreHz">Frequency at the exact middle of <paramref name="Bins"/>.</param>
        /// <param name="SpanHz">Width covered by <paramref name="Bins"/>, i.e. bins × Hz per bin.</param>
        public readonly record struct View(float[] Bins, long CentreHz, long SpanHz);

        /// <summary>
        /// Crop <paramref name="bins"/> to the window centred on
        /// <paramref name="viewCentreHz"/> and <paramref name="viewSpanHz"/> wide,
        /// then thin the result to at most <paramref name="maxBins"/> values.
        /// </summary>
        /// <param name="bins">
        /// FFT-shifted power bins of the whole stream: bin 0 is the most negative
        /// frequency, bin <c>N/2</c> sits on <paramref name="tuneCentreHz"/>, the
        /// last bin is the most positive. Any length, not only powers of two.
        /// </param>
        /// <param name="hzPerBin">Bin spacing, i.e. sample rate ÷ FFT size.</param>
        /// <param name="tuneCentreHz">The frequency the hardware is tuned to.</param>
        /// <param name="viewCentreHz">Where the operator wants the middle of the picture.</param>
        /// <param name="viewSpanHz">How wide a picture they want.</param>
        /// <param name="maxBins">
        /// Upper limit on the number of bins returned. A 16k-point FFT cropped to
        /// its full width is far more than a browser canvas can draw or a
        /// SignalR message should carry, so wider windows are thinned by an
        /// integer factor. The thinning keeps the <b>maximum</b> of each group,
        /// not the mean: a CW carrier occupies one bin, and averaging eight bins
        /// together would sink it by 9 dB — exactly the way a spatial smoother
        /// was once found to bury CW in the browser.
        /// </param>
        /// <remarks>
        /// The window is slid, not shrunk, when it would run off either end of
        /// the source: asking for a 10 kHz view centred 2 kHz from the edge of a
        /// 125 kHz stream gives a 10 kHz view that stops at the edge. A window
        /// wider than the source is clamped to the source. The returned
        /// <see cref="View.CentreHz"/> and <see cref="View.SpanHz"/> describe the
        /// bins actually returned, after both adjustments, so a caller drawing an
        /// axis from them is never lied to.
        /// </remarks>
        public static View Crop(
            ReadOnlySpan<float> bins,
            double hzPerBin,
            long   tuneCentreHz,
            long   viewCentreHz,
            long   viewSpanHz,
            int    maxBins)
        {
            if (bins.Length == 0)       throw new ArgumentException("No bins.", nameof(bins));
            if (!(hzPerBin > 0))         throw new ArgumentOutOfRangeException(nameof(hzPerBin));
            if (viewSpanHz <= 0)         throw new ArgumentOutOfRangeException(nameof(viewSpanHz));
            if (maxBins <= 0)            throw new ArgumentOutOfRangeException(nameof(maxBins));

            int n = bins.Length;

            // Bins wanted, capped at what there is.
            int count = (int)Math.Round(viewSpanHz / hzPerBin);
            if (count < 1) count = 1;
            if (count > n) count = n;

            // Thinning factor decided before the window is placed, so the
            // window can be trimmed to a whole number of groups and the
            // reported span stays exact.
            int factor = (count + maxBins - 1) / maxBins;
            if (factor > 1) count -= count % factor;
            if (count < factor) count = factor;

            // Bin index (fractional) whose centre sits on viewCentreHz. Bin k is
            // centred on tune + (k - n/2) * hzPerBin.
            double centreBin = n / 2.0 + (viewCentreHz - tuneCentreHz) / hzPerBin;

            // First bin so that the window's middle lands on centreBin. The
            // middle of `count` bins starting at `first` is first + count/2 - 0.5.
            int first = (int)Math.Round(centreBin - count / 2.0 + 0.5);
            if (first < 0)          first = 0;
            if (first + count > n)  first = n - count;

            float[] outBins;
            if (factor == 1)
            {
                outBins = bins.Slice(first, count).ToArray();
            }
            else
            {
                int groups = count / factor;
                outBins = new float[groups];
                for (int g = 0; g < groups; g++)
                {
                    int   start = first + g * factor;
                    float best  = bins[start];
                    for (int i = 1; i < factor; i++)
                    {
                        float v = bins[start + i];
                        if (v > best) best = v;
                    }
                    outBins[g] = best;
                }
            }

            // Where the returned array's middle really is, and how wide it
            // really is — from the bins used, not from what was asked for.
            double midBin   = first + count / 2.0 - 0.5;
            long   centreHz = (long)Math.Round(tuneCentreHz + (midBin - n / 2.0) * hzPerBin);
            long   spanHz   = (long)Math.Round(count * hzPerBin);

            return new View(outBins, centreHz, spanHz);
        }

        /// <summary>
        /// Keeps a sliding window of interleaved I/Q samples so an FFT can be
        /// run more often than once per window's worth of new samples.
        ///
        /// Without overlap the frame rate and the resolution are the same
        /// number: an N-point FFT at rate R gives R/N Hz per bin and R/N frames
        /// a second, whatever R is. Fine bins for a 2.5 kHz view therefore
        /// mean a slow display unless each FFT reuses most of the previous
        /// one's samples. Feed <see cref="Push"/> with each hop of new samples;
        /// once <see cref="IsFull"/> the <see cref="Window"/> is ready to
        /// transform after every hop.
        /// </summary>
        public sealed class OverlapBuffer
        {
            private readonly float[] _window;
            private          int     _filled;   // in samples, not floats

            /// <param name="windowSamples">Complex samples per FFT.</param>
            public OverlapBuffer(int windowSamples)
            {
                if (windowSamples <= 0) throw new ArgumentOutOfRangeException(nameof(windowSamples));
                _window = new float[windowSamples * 2];
            }

            /// <summary>Interleaved I/Q, length windowSamples × 2. Valid once <see cref="IsFull"/>.</summary>
            public float[] Window => _window;

            /// <summary>True once enough samples have been pushed to fill the window.</summary>
            public bool IsFull => _filled * 2 >= _window.Length;

            /// <summary>
            /// Append interleaved I/Q samples, discarding the oldest to make
            /// room. A hop larger than the window simply replaces it.
            /// </summary>
            public void Push(ReadOnlySpan<float> iq)
            {
                if (iq.Length % 2 != 0) throw new ArgumentException("Interleaved I/Q must have even length.", nameof(iq));

                if (iq.Length >= _window.Length)
                {
                    iq[^_window.Length..].CopyTo(_window);
                    _filled = _window.Length / 2;
                    return;
                }

                int keep = _window.Length - iq.Length;
                Array.Copy(_window, iq.Length, _window, 0, keep);
                iq.CopyTo(_window.AsSpan(keep));
                _filled = Math.Min(_filled + iq.Length / 2, _window.Length / 2);
            }

            /// <summary>Forget everything; the next window must be refilled from scratch.</summary>
            public void Reset()
            {
                Array.Clear(_window);
                _filled = 0;
            }
        }
    }
}
