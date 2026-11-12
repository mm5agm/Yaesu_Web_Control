using RadioWebControl.Core.Services.Spectrum;

namespace RadioWebControl.Core.Tests.Spectrum
{
    public class SpectrumRefinerTests
    {
        private const int N = 475;
        private const double Span = 20_000;
        private static double HzPerBin => Span / (N - 1);

        private static float[] Flat(float level)
        {
            var b = new float[N];
            Array.Fill(b, level);
            return b;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-0.1)]
        [InlineData(1.01)]
        [InlineData(double.NaN)]
        public void A_weight_outside_0_to_1_is_refused(double w)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumRefiner(w));
            var r = new SpectrumRefiner(0.5);
            Assert.Throws<ArgumentOutOfRangeException>(() => r.Weight = w);
        }

        [Fact]
        public void The_first_sweep_comes_back_as_it_is()
        {
            var r = new SpectrumRefiner(0.3);
            var s = Flat(-100);
            s[200] = -50;
            Assert.Equal(s, r.Add(s, 14_074_000, Span));
        }

        [Fact]
        public void Weight_1_is_the_raw_sweeps()
        {
            var r = new SpectrumRefiner(1);
            r.Add(Flat(-100), 14_074_000, Span);
            Assert.All(r.Add(Flat(-80), 14_074_000, Span), v => Assert.Equal(-80f, v));
        }

        [Fact]
        public void A_rough_floor_settles()
        {
            // Every bin flips 20 dB each sweep, as a rough floor does.
            var r = new SpectrumRefiner(0.3);
            float[] last = null!, now = null!;
            for (int n = 0; n < 40; n++)
            {
                last = now;
                now = r.Add(Flat(n % 2 == 0 ? -110 : -90), 14_074_000, Span);
            }
            Assert.True(Math.Abs(now[100] - last[100]) < 20f / 2, $"moved {Math.Abs(now[100] - last[100])} dB a sweep");
            Assert.InRange(now[100], -105f, -95f);
        }

        [Fact]
        public void The_result_is_a_new_array_each_time()
        {
            var r = new SpectrumRefiner(0.5);
            var a = r.Add(Flat(-100), 1_000_000, Span);
            var b = r.Add(Flat(-100), 1_000_000, Span);
            Assert.NotSame(a, b);
            a[0] = 0;
            Assert.Equal(-100f, r.Add(Flat(-100), 1_000_000, Span)[0]);
        }

        [Fact]
        public void A_signal_stays_sharp_while_tuning()
        {
            // A carrier at a fixed frequency; the centre moves up 10 bins a sweep.
            // Followed, it stays one bin wide at full strength instead of smearing.
            var r = new SpectrumRefiner(0.3);
            const double carrierHz = 14_075_000;
            float[] t = null!;
            double centre = 14_074_000;
            for (int n = 0; n < 20; n++, centre += 10 * HzPerBin)
            {
                var s = Flat(-100);
                int at = (int)Math.Round((carrierHz - (centre - Span / 2)) / HzPerBin);
                s[at] = -40;
                t = r.Add(s, centre, Span);
            }
            centre -= 10 * HzPerBin;
            int expected = (int)Math.Round((carrierHz - (centre - Span / 2)) / HzPerBin);
            Assert.Equal(-40f, t[expected], 3);
            Assert.Equal(-100f, t[expected - 1], 3);
            Assert.Equal(-100f, t[expected + 1], 3);
        }

        [Fact]
        public void Tuning_by_less_than_a_bin_adds_up()
        {
            var r = new SpectrumRefiner(0.5);
            var s = Flat(-100);
            s[200] = -40;
            r.Add(s, 14_074_000, Span);
            // Four moves of a third of a bin each: one whole bin in all, after rounding.
            float[] t = null!;
            for (int k = 1; k <= 4; k++)
                t = r.Add(Flat(-100), 14_074_000 + k * HzPerBin / 3, Span);
            // The old carrier's average has moved down one bin (199).
            Assert.True(t[199] > t[200], $"199 {t[199]}, 200 {t[200]}");
        }

        [Theory]
        [InlineData(10_000, N)]       // a new span
        [InlineData(Span, 237)]       // a different number of bins
        public void A_new_span_or_bin_count_starts_again(double span, int bins)
        {
            var r = new SpectrumRefiner(0.3);
            r.Add(Flat(-100), 14_074_000, Span);
            var fresh = new float[bins];
            Array.Fill(fresh, -60f);
            Assert.All(r.Add(fresh, 14_074_000, span), v => Assert.Equal(-60f, v));
        }

        [Fact]
        public void A_jump_of_more_than_half_a_span_starts_again()
        {
            var r = new SpectrumRefiner(0.3);
            r.Add(Flat(-100), 14_074_000, Span);
            Assert.All(r.Add(Flat(-60), 7_074_000, Span), v => Assert.Equal(-60f, v));
        }

        [Fact]
        public void A_sweep_with_no_span_is_shown_as_it_is_and_starts_again()
        {
            var r = new SpectrumRefiner(0.3);
            r.Add(Flat(-100), 14_074_000, Span);
            Assert.All(r.Add(Flat(-70), 14_074_000, 0), v => Assert.Equal(-70f, v));
            Assert.All(r.Add(Flat(-60), 14_074_000, Span), v => Assert.Equal(-60f, v));
        }

        [Fact]
        public void Changing_the_weight_carries_on_from_the_average()
        {
            var r = new SpectrumRefiner(1);
            r.Add(Flat(-100), 14_074_000, Span);
            r.Weight = 0.5;
            Assert.All(r.Add(Flat(-80), 14_074_000, Span), v => Assert.Equal(-90f, v));
        }
    }
}
