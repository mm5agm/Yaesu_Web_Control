using RadioWebControl.Core.Services.Spectrum;

namespace RadioWebControl.Core.Tests.Spectrum
{
    public class AudioSpectrumAnalyserTests
    {
        private const int Rate = 48_000;
        private const int N    = 2048;

        private static float[] Sine(double hz, double amplitude, int samples)
        {
            var s = new float[samples];
            for (int i = 0; i < samples; i++)
                s[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * hz * i / Rate));
            return s;
        }

        [Fact]
        public void Bins_cover_zero_to_max_hz_and_no_further()
        {
            var a = new AudioSpectrumAnalyser(Rate, N, maxHz: 6000);

            Assert.Equal(48_000.0 / 2048, a.BinHz, 6);
            Assert.Equal((int)Math.Ceiling(6000 / a.BinHz), a.BinCount);
            Assert.True(a.BinCount * a.BinHz >= 6000);
            Assert.True((a.BinCount - 1) * a.BinHz < 6000);
        }

        [Fact]
        public void Silence_is_all_zero_and_produces_a_frame_per_fft_size()
        {
            var a = new AudioSpectrumAnalyser(Rate, N);

            Assert.False(a.Push(new float[N - 1]));
            Assert.True(a.Push(new float[1]));
            Assert.All(a.Bins.ToArray(), b => Assert.Equal(0, b));
        }

        [Fact]
        public void Full_scale_sine_pins_its_own_bin_and_leaves_the_rest_low()
        {
            // No smoothing, so one frame is the measurement itself.
            var a = new AudioSpectrumAnalyser(Rate, N, smoothing: 0);
            int peak = 43;                 // ~1008 Hz, on a bin centre
            double hz = peak * a.BinHz;
            Assert.True(a.Push(Sine(hz, 1.0, N)));

            var bins = a.Bins.ToArray();

            // 0 dBFS is well above maxDb (-30), so the peak bin saturates.
            Assert.Equal(255, bins[peak]);

            // Hann leakage is gone within a few bins. Byte 60 is -83.5 dBFS,
            // so this still asserts the rest of the passband is 50 dB down
            // on the tone. (A tone between bin centres leaks more -- 1000 Hz
            // exactly puts the far bins near -88 dB -- which is the window,
            // not a fault, so the test keeps the tone centred.)
            for (int k = 0; k < bins.Length; k++)
            {
                if (Math.Abs(k - peak) <= 3) continue;
                Assert.True(bins[k] < 60, $"bin {k} ({k * a.BinHz:F0} Hz) = {bins[k]}, expected near floor");
            }
        }

        [Fact]
        public void Level_maps_linearly_in_db_between_min_and_max()
        {
            // -65 dBFS sits halfway between -100 and -30, so its bin should
            // read about 128. A tone at a bin centre keeps the Hann peak
            // exactly on the bin.
            var a = new AudioSpectrumAnalyser(Rate, N, smoothing: 0);
            int bin = 43;
            double hz = bin * a.BinHz;
            double amplitude = Math.Pow(10, -65 / 20.0);

            a.Push(Sine(hz, amplitude, N));

            Assert.InRange(a.Bins[bin], 120, 136);
        }

        [Fact]
        public void Smoothing_leans_on_the_previous_frame()
        {
            var a = new AudioSpectrumAnalyser(Rate, N, smoothing: 0.7);
            int bin = 43;
            double hz = bin * a.BinHz;

            // Starts from the floor: the first loud frame does not jump all
            // the way, so the byte is below saturation and rises on the next.
            a.Push(Sine(hz, 1.0, N));
            byte first = a.Bins[bin];
            a.Push(Sine(hz, 1.0, N));
            byte second = a.Bins[bin];

            Assert.True(first < 255, $"first frame {first} should not have reached full scale yet");
            Assert.True(second > first, $"second {second} should exceed first {first}");
        }

        [Fact]
        public void Reset_forgets_partial_frame_and_history()
        {
            var a = new AudioSpectrumAnalyser(Rate, N, smoothing: 0.7);
            a.Push(Sine(1000, 1.0, N));
            a.Push(new float[100]);

            a.Reset();

            Assert.All(a.Bins.ToArray(), b => Assert.Equal(0, b));
            Assert.False(a.Push(new float[N - 1]));
            Assert.True(a.Push(new float[1]));
            Assert.All(a.Bins.ToArray(), b => Assert.Equal(0, b));
        }

        [Fact]
        public void Rejects_non_power_of_two_and_inverted_range()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AudioSpectrumAnalyser(Rate, 1000));
            Assert.Throws<ArgumentException>(() => new AudioSpectrumAnalyser(Rate, N, minDb: -30, maxDb: -100));
        }
    }
}
