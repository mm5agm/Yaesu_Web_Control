using RadioWebControl.Core.Services.Spectrum;

namespace RadioWebControl.Core.Tests.Spectrum
{
    public class SpectrumZoomTests
    {
        // A 16k-point FFT over 125 kHz: 7.629 Hz per bin, the shape the SDR
        // worker actually runs in its zoom mode.
        private const int    N        = 16384;
        private const double Rate     = 125_000;
        private const double HzPerBin = Rate / N;
        private const long   Tune     = 9_000_000;

        /// <summary>Bins whose value is their own index, so any slice is self-describing.</summary>
        private static float[] IndexBins(int n = N)
        {
            var b = new float[n];
            for (int i = 0; i < n; i++) b[i] = i;
            return b;
        }

        private static double BinFrequency(int k) => Tune + (k - N / 2.0) * HzPerBin;

        [Fact]
        public void Full_width_view_returns_every_bin_thinned_to_the_cap()
        {
            var view = SpectrumZoom.Crop(IndexBins(), HzPerBin, Tune, Tune, (long)Rate, maxBins: 2048);

            Assert.Equal(2048, view.Bins.Length);
            Assert.Equal((long)Rate, view.SpanHz);
            // Max-hold over groups of 8: group g holds bin 8g+7.
            Assert.Equal(7f,           view.Bins[0]);
            Assert.Equal(N - 1,        view.Bins[^1]);
        }

        [Fact]
        public void Narrow_view_is_centred_where_asked_and_reports_its_own_width()
        {
            long centre = 9_005_000;
            long span   = 2_500;

            var view = SpectrumZoom.Crop(IndexBins(), HzPerBin, Tune, centre, span, maxBins: 2048);

            int expectedCount = (int)Math.Round(span / HzPerBin);   // 328
            Assert.Equal(expectedCount, view.Bins.Length);

            // The reported span is the width of the bins returned, which is not
            // exactly the 2500 asked for because bins are 7.63 Hz wide.
            Assert.Equal((long)Math.Round(expectedCount * HzPerBin), view.SpanHz);
            Assert.InRange(view.SpanHz, span - 8, span + 8);

            // The reported centre is within half a bin of the request.
            Assert.InRange(view.CentreHz, centre - HzPerBin / 2, centre + HzPerBin / 2);

            // And the middle of the returned array really is that frequency:
            // bin values are their source indices, so read the frequency back.
            int    firstBin = (int)view.Bins[0];
            double midBin   = firstBin + view.Bins.Length / 2.0 - 0.5;
            Assert.InRange(BinFrequency(0) + midBin * HzPerBin, view.CentreHz - 1, view.CentreHz + 1);
        }

        [Fact]
        public void A_view_that_would_run_off_the_top_is_slid_back_inside_not_shrunk()
        {
            // 10 kHz asked for, centred 2 kHz below the top edge of the stream.
            long topEdge = Tune + (long)(Rate / 2);
            var view = SpectrumZoom.Crop(IndexBins(), HzPerBin, Tune, topEdge - 2_000, 10_000, maxBins: 4096);

            int expectedCount = (int)Math.Round(10_000 / HzPerBin);
            Assert.Equal(expectedCount, view.Bins.Length);
            Assert.Equal(N - 1, view.Bins[^1]);                 // stops at the last bin
            Assert.InRange(view.SpanHz, 9_990, 10_010);
            Assert.True(view.CentreHz < topEdge - 2_000);       // moved down to fit
        }

        [Fact]
        public void A_view_that_would_run_off_the_bottom_is_slid_up()
        {
            long bottomEdge = Tune - (long)(Rate / 2);
            var view = SpectrumZoom.Crop(IndexBins(), HzPerBin, Tune, bottomEdge, 5_000, maxBins: 4096);

            Assert.Equal(0f, view.Bins[0]);
            Assert.True(view.CentreHz > bottomEdge);
        }

        [Fact]
        public void A_view_wider_than_the_source_is_clamped_to_the_source()
        {
            var view = SpectrumZoom.Crop(IndexBins(), HzPerBin, Tune, Tune, 1_000_000, maxBins: 100_000);

            Assert.Equal(N, view.Bins.Length);
            Assert.Equal((long)Math.Round(N * HzPerBin), view.SpanHz);
            Assert.Equal(0f,    view.Bins[0]);
            Assert.Equal(N - 1, view.Bins[^1]);
        }

        [Fact]
        public void Thinning_keeps_the_peak_not_the_mean()
        {
            // Flat floor with one single-bin carrier in it.
            var bins = new float[N];
            Array.Fill(bins, -100f);
            bins[N / 2 + 1000] = -20f;

            var view = SpectrumZoom.Crop(bins, HzPerBin, Tune, Tune, (long)Rate, maxBins: 2048);

            Assert.Equal(-20f, view.Bins.Max());
            Assert.Equal(1,    view.Bins.Count(v => v > -100f));
        }

        [Fact]
        public void Thinned_view_covers_a_whole_number_of_groups_and_says_so()
        {
            // 50 kHz at 7.63 Hz/bin is 6554 bins; cap 2048 -> factor 4 -> 6552 bins.
            var view = SpectrumZoom.Crop(IndexBins(), HzPerBin, Tune, Tune, 50_000, maxBins: 2048);

            Assert.Equal(6552 / 4, view.Bins.Length);
            Assert.Equal((long)Math.Round(6552 * HzPerBin), view.SpanHz);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Rejects_a_non_positive_span(long span)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SpectrumZoom.Crop(IndexBins(64), 10, Tune, Tune, span, 64));
        }

        // ── OverlapBuffer ────────────────────────────────────────────────────

        [Fact]
        public void Overlap_buffer_is_full_after_window_over_hop_pushes_and_slides_thereafter()
        {
            var buf = new SpectrumZoom.OverlapBuffer(windowSamples: 8);
            Assert.False(buf.IsFull);

            // Hop of 2 samples (4 floats), numbered so we can see them move.
            float next = 0;
            float[] Hop() { var h = new float[4]; for (int i = 0; i < 4; i++) h[i] = next++; return h; }

            buf.Push(Hop()); Assert.False(buf.IsFull);
            buf.Push(Hop()); Assert.False(buf.IsFull);
            buf.Push(Hop()); Assert.False(buf.IsFull);
            buf.Push(Hop()); Assert.True(buf.IsFull);

            // Oldest sample at the front, newest at the back.
            Assert.Equal(0f,  buf.Window[0]);
            Assert.Equal(15f, buf.Window[^1]);

            buf.Push(Hop());
            Assert.Equal(4f,  buf.Window[0]);   // first hop has fallen off
            Assert.Equal(19f, buf.Window[^1]);
        }

        [Fact]
        public void Overlap_buffer_takes_the_tail_of_an_oversized_push()
        {
            var buf = new SpectrumZoom.OverlapBuffer(windowSamples: 4);
            var big = new float[20];
            for (int i = 0; i < 20; i++) big[i] = i;

            buf.Push(big);

            Assert.True(buf.IsFull);
            Assert.Equal(12f, buf.Window[0]);
            Assert.Equal(19f, buf.Window[^1]);
        }

        [Fact]
        public void Overlap_buffer_reset_empties_it()
        {
            var buf = new SpectrumZoom.OverlapBuffer(windowSamples: 2);
            buf.Push(new float[] { 1, 2, 3, 4 });
            Assert.True(buf.IsFull);

            buf.Reset();

            Assert.False(buf.IsFull);
            Assert.All(buf.Window, v => Assert.Equal(0f, v));
        }
    }
}
