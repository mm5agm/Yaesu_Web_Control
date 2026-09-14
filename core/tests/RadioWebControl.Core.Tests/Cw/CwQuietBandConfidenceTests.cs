using RadioWebControl.Core.Services.Cw;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// Confidence is the tone detector's answer to "do I know where the tone
    /// is", and zero-in will not offer a frequency correction below 0.5 of it.
    /// It is built from spectral prominence, which is a shape measure: it says
    /// the peak stands clear of the rest of the search window, never that the
    /// peak is a signal. Anything with a bump in the right place scores well.
    ///
    /// On 2026-08-27, six minutes of an empty 20m ran at a mean confidence of
    /// 0.43 and crossed 0.5 three times with not one character decoded, so the
    /// panel would have offered to retune onto a station that was not there.
    /// The fix lets a frame raise confidence only while the detector's own
    /// level test says a tone is present; falling is left alone, so the slow
    /// decay that carries confidence across the gap between overs still works.
    /// </summary>
    public class CwQuietBandConfidenceTests
    {
        private const int    Rate  = 48000;
        private const double Pitch = 600.0;

        /// <summary>Highest confidence the detector reaches anywhere in the clip.</summary>
        private static double PeakConfidence(float[] audio)
        {
            var detector = new CwToneDetector(new CwToneDetectorOptions
            {
                InputSampleRate = Rate,
                PitchHz         = Pitch,
                TrackPitch      = true,
            });

            var samples = new List<CwToneSample>();
            detector.Process(audio, samples);

            double peak = 0.0;
            foreach (var s in samples) peak = Math.Max(peak, s.Confidence);
            return peak;
        }

        /// <summary>Gaussian noise, optionally with an unkeyed carrier sitting on the pitch.</summary>
        private static float[] Noise(double seconds, double sigma, double carrierAmp, int seed)
        {
            var rng   = new Random(seed);
            var audio = new float[(int)(seconds * Rate)];
            for (int i = 0; i < audio.Length; i++)
            {
                // Box-Muller, so the tail is a real Gaussian tail rather than
                // the clipped one a sum of uniforms would give.
                double u1 = 1.0 - rng.NextDouble();
                double u2 = rng.NextDouble();
                double n  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);

                audio[i] = (float)(sigma * n
                                 + carrierAmp * Math.Sin(2.0 * Math.PI * Pitch * i / Rate));
            }
            return audio;
        }

        /// <summary>
        /// A carrier that never keys is the sharpest form of the bug, and the
        /// detector already has the right opinion about it: an unmodulated tone
        /// gets absorbed into the noise floor estimate, so the level test says
        /// "no signal" no matter how loud it is - correctly, because nothing is
        /// being sent. Prominence disagreed and scored 0.99, because the peak
        /// really is exactly where a signal's peak would be. Nothing decodes
        /// here, so nothing should be offering to retune the radio.
        /// </summary>
        [Theory]
        [InlineData(0.005)]
        [InlineData(0.010)]
        [InlineData(0.020)]
        [InlineData(0.080)]
        public void A_carrier_that_never_keys_is_not_something_to_zero_in_on(double carrierAmp)
        {
            double peak = PeakConfidence(Noise(20.0, 0.05, carrierAmp, 20260827));

            Assert.True(peak < 0.5,
                $"confidence reached {peak:F2} on an unkeyed carrier at amplitude "
              + $"{carrierAmp}; zero-in offers a correction at 0.5 and there is "
              + "no signal here to correct to");
        }

        [Theory]
        [InlineData(20260827)]
        [InlineData(11)]
        [InlineData(22)]
        public void An_empty_band_never_gets_confident_enough_to_offer_zero_in(int seed)
        {
            double peak = PeakConfidence(Noise(30.0, 0.05, 0.0, seed));

            Assert.True(peak < 0.5, $"confidence reached {peak:F2} on noise alone (seed {seed})");
        }

        /// <summary>
        /// The other half, and the one that stops all of the above being
        /// satisfied by simply clamping confidence to zero. Well down in the
        /// noise, and it should still be sure where the tone is.
        /// </summary>
        [Fact]
        public void A_real_signal_still_gets_confident()
        {
            var gen   = new CwSignalGenerator { SampleRate = Rate, ToneHz = Pitch };
            var audio = CwSignalGenerator.Concat(gen.LeadIn(),
                                                 gen.Generate("CQ CQ DE MM5AGM K", 20.0));
            gen.AddNoise(audio, 6.0);

            double peak = PeakConfidence(audio);

            Assert.True(peak >= 0.5,
                $"confidence only reached {peak:F2} on a real signal; zero-in would "
              + "refuse to answer for a station that is plainly there");
        }
    }
}
