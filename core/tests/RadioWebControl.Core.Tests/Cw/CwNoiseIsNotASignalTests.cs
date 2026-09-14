using RadioWebControl.Core.Services.Cw;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// Presence is the detector's answer to "is there a tone at all", and it
    /// gates KeyDown: nothing is decoded while it is false. It used to be a
    /// pure level test, peak over mean noise, and that cannot separate a
    /// signal from hiss whose level wanders. _peakEst is a multi-second
    /// max-hold and _noiseMean a quarter-second mean, so their ratio carries
    /// the peak factor of the noise by itself.
    ///
    /// On 2026-08-27 an empty 15m - nothing audible anywhere on it - reported
    /// 13.7 dB, "signal", "locked", 60 wpm and 93% chatter, and the same band
    /// read as seven separate stations because every frequency returned a tone
    /// at the centre of the IF filter. What fixes it is that a mark lasts:
    /// 20 ms at 60 wpm, three times that for a dah, against one or two hops
    /// for a noise spike.
    ///
    /// These tests use amplitude-modulated noise because stationary noise does
    /// not reproduce the fault - it measures 6.4 dB and never trips the gate.
    /// It took a wandering level to get there, which is what a receiver with
    /// AGC, QSB and atmospherics actually hands over.
    /// </summary>
    public class CwNoiseIsNotASignalTests
    {
        private const int    Rate  = 48000;
        private const double Pitch = 600.0;

        /// <summary>
        /// Noise whose level wanders, as a real receiver's does. The gain is a
        /// random walk pulled back towards 1, with <paramref name="wander"/>
        /// setting how far it strays and <paramref name="tauSeconds"/> how long
        /// it takes to get there.
        /// </summary>
        private static float[] WanderingNoise(double seconds, double sigma, int seed,
                                              double wander, double tauSeconds)
        {
            var rng   = new Random(seed);
            var audio = new float[(int)(seconds * Rate)];
            double gain = 1.0;
            double pull = tauSeconds > 0 ? 1.0 / (tauSeconds * Rate) : 0.0;

            for (int i = 0; i < audio.Length; i++)
            {
                double u1 = 1.0 - rng.NextDouble();
                double u2 = rng.NextDouble();
                double g  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);

                // Ornstein-Uhlenbeck on the gain: drift, with a spring back to 1
                // so the level cannot run away over a long clip.
                double kick = Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble()))
                            * Math.Cos(2.0 * Math.PI * rng.NextDouble());
                gain += -pull * (gain - 1.0) + wander * Math.Sqrt(pull) * kick;
                gain  = Math.Max(0.05, gain);

                audio[i] = (float)(sigma * gain * g);
            }
            return audio;
        }

        private static (double presentFraction, double keyedFraction) Run(float[] audio)
        {
            var detector = new CwToneDetector(new CwToneDetectorOptions
            {
                InputSampleRate = Rate,
                PitchHz         = Pitch,
                TrackPitch      = true,
            });

            var samples = new List<CwToneSample>();
            detector.Process(audio, samples);
            detector.Flush(samples);

            if (samples.Count == 0) return (0.0, 0.0);

            // Presence is measured after the detector has had time to make up
            // its mind, not from the first sample. The level test opens the
            // gate on sight and the keying gate is given a couple of seconds
            // to confirm or withdraw it, so an opening burst of presence on an
            // empty band is the design and not the defect - see GraceHops in
            // CwToneDetector. What must not happen is that the burst never
            // ends. Keying is measured over the whole file, with no allowance
            // at all: the burst is allowed to say "something might be here",
            // never to emit a character.
            int skip = (int)(SettleSeconds * Rate / HopSamples);
            int present = 0, settled = 0, keyed = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].SignalPresent) { present++; if (i >= skip) settled++; }
                if (samples[i].KeyDown) keyed++;
            }
            int settledCount = Math.Max(1, samples.Count - skip);
            return ((double)settled / settledCount, (double)keyed / samples.Count);
        }

        // 5 s, comfortably past the detector's 3.5 s grace.
        private const double SettleSeconds = 5.0;
        private const int    HopSamples    = 40 * (Rate / 8000);   // EnvHop at the work rate

        // Calibrated against the real thing, not guessed. The 180 s recording
        // of empty 15m in bench/ wanders with sigma 0.053 at tau 0.72 s; two
        // 12 s captures from other frequencies give sigma 0.075 / tau 0.57 and
        // sigma 0.122 / tau 0.58. So a band wanders by around 5-12%, and the
        // vectors below run from that up to 0.25 - twice the worst measured -
        // with tau spread either side of the measured 0.6 s.
        //
        // An earlier version of this test asked for 0.3, 0.6 and 1.0, which
        // failed and looked like a detector fault. It was not: sigma 1.0 is a
        // 26 dB random swing in the noise floor, twenty times anything the
        // recordings show, and a level that wanders that hard on a 0.3 s
        // timescale is not noise misbehaving - it is amplitude keying at a
        // few wpm, which a tone detector is right to call a signal. Raise
        // these numbers only against a recording that justifies them.
        //
        // The bar on presence is looser for the two over-margin vectors, and
        // deliberately so. At 0.25 the level swings far enough, for long
        // enough, that an excursion lasts about as long as a dah does at a
        // slow speed - so the run-length test the gate is built on genuinely
        // cannot separate the two, and it leaks. Telling them apart would take
        // a test of whether the runs are *consistent* in length, since morse
        // elements cluster at one and three units while a random walk's
        // excursions are exponentially distributed. That is a bigger change
        // than this one, and this is the note that says why it was not made
        // here. What does hold at every vector below, with no allowance, is
        // the keying assertion: the band may be called suspicious, but not a
        // single element is read off it.
        //
        // A sixth vector, 0.25 at tau 0.3 s, was tried and dropped. It is
        // twice the worst measured wander at half the measured timescale, and
        // a noise floor that swings 2 dB every 300 ms is not a noise floor -
        // it is amplitude modulation at a few wpm, which is the same reason
        // the first draft of this test used a 1 Hz sine and had to stop. It
        // did read 2% keying, and it read exactly the same 2% with the gate
        // bypassed entirely, so it was measuring the envelope detector rather
        // than anything this test is about.
        [Theory]
        [InlineData(0.00, 0.7, 0.05)]
        [InlineData(0.05, 0.7, 0.05)]
        [InlineData(0.12, 0.6, 0.05)]
        [InlineData(0.25, 1.5, 0.20)]
        [InlineData(0.25, 0.6, 0.20)]
        public void Noise_that_wanders_is_still_not_a_signal(double wander, double tauSeconds, double maxPresent)
        {
            var (present, keyed) = Run(WanderingNoise(30.0, 0.05, 4242, wander, tauSeconds));

            Assert.True(present < maxPresent,
                $"noise wandering {wander} over {tauSeconds}s was still called a signal " +
                $"{present:P0} of the time once the gate had had its say");
            Assert.True(keyed < 0.01,
                $"noise wandering {wander} over {tauSeconds}s read as keying {keyed:P0} of the time");
        }

        /// <summary>
        /// The one that stops the others being satisfied by never reporting a
        /// signal at all.
        /// </summary>
        [Theory]
        [InlineData(20.0)]
        [InlineData(35.0)]
        public void Real_morse_is_still_a_signal(double wpm)
        {
            var gen   = new CwSignalGenerator { SampleRate = Rate, ToneHz = Pitch };
            var audio = CwSignalGenerator.Concat(gen.LeadIn(),
                                                 gen.Generate("CQ CQ DE MM5AGM MM5AGM K", wpm));
            gen.AddNoise(audio, 12.0);

            var (present, keyed) = Run(audio);
            Assert.True(present > 0.5, $"morse at {wpm} wpm was only present {present:P0} of the time");
            Assert.True(keyed   > 0.1, $"morse at {wpm} wpm only read as keyed {keyed:P0} of the time");
        }
    }
}
