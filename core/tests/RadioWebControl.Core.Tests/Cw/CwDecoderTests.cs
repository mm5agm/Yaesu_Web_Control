using RadioWebControl.Core.Services.Cw;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// The measurements the plan asks Phase 1 to produce. Every test prints its
    /// numbers as well as asserting on them, so a run of "dotnet test -v n" is a
    /// report on the decoder and not just a pass or a fail.
    /// </summary>
    public class CwDecoderTests
    {
        private readonly ITestOutputHelper _out;

        public CwDecoderTests(ITestOutputHelper output) => _out = output;

        private const string Over1 = "CQ CQ DE MM5AGM MM5AGM K";
        private const string Over2 = "MM5AGM DE OZ1JTE GM OM UR RST 599 5NN QTH COPENHAGEN BK";

        private static string Decode(float[] audio, double pitchHz = 600.0)
        {
            var engine = new CwDecoderEngine(new CwDecoderOptions { PitchHz = pitchHz });
            var text = new System.Text.StringBuilder();

            const int frame = 480;                       // 10 ms, what capture produces
            for (int i = 0; i < audio.Length; i += frame)
            {
                int n = Math.Min(frame, audio.Length - i);
                text.Append(engine.ProcessFrame(audio.AsSpan(i, n)));
            }
            text.Append(engine.Flush());

            return text.ToString();
        }

        // ---------------------------------------------------------------- speed

        [Theory]
        [InlineData(12)]
        [InlineData(20)]
        [InlineData(27)]
        [InlineData(35)]
        public void Decodes_across_the_speeds_people_actually_send(double wpm)
        {
            var gen   = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate(Over1, wpm), gen.Silence(0.3));
            gen.AddNoise(audio, 20.0);

            var decoded = Decode(audio);
            double score = CwAccuracy.Score(decoded, Over1);

            _out.WriteLine($"{wpm,4:0} WPM at 20 dB: {score:P1}  \"{CwAccuracy.Normalise(decoded)}\"");
            Assert.True(score >= 0.95, $"{wpm} WPM scored {score:P1}: {decoded}");
        }

        // ------------------------------------------------------------------ SNR

        [Theory]
        [InlineData(30.0, 0.95)]
        [InlineData(20.0, 0.95)]
        [InlineData(12.0, 0.95)]
        [InlineData(6.0,  0.85)]
        public void Degrades_gracefully_as_the_signal_gets_weaker(double snrDb, double floor)
        {
            var gen   = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate(Over1, 20), gen.Silence(0.3));
            gen.AddNoise(audio, snrDb);

            var decoded = Decode(audio);
            double score = CwAccuracy.Score(decoded, Over1);

            _out.WriteLine($"{snrDb,4:0} dB SNR/500Hz: {score:P1}  \"{CwAccuracy.Normalise(decoded)}\"");
            Assert.True(score >= floor, $"{snrDb} dB scored {score:P1}: {decoded}");
        }

        // ------------------------------------------------------- the mixed-speed
        // case, which is the one the FTdx101 cannot do at all and therefore the
        // one that justifies writing this.

        [Fact]
        public void Follows_a_QSO_where_the_two_operators_send_at_different_speeds()
        {
            var gen = new CwSignalGenerator();

            var over1 = gen.Generate(Over1, 27);
            var over2 = gen.Generate(Over2, 16);

            var mixed = CwSignalGenerator.Concat(gen.LeadIn(), over1, gen.Silence(1.5), over2, gen.Silence(0.3));
            gen.AddNoise(mixed, 20.0);

            var mixedDecoded = Decode(mixed);
            string expected  = Over1 + " " + Over2;
            double score     = CwAccuracy.Score(mixedDecoded, expected);

            // Each over on its own, so the transition can be charged only for the
            // errors it actually caused rather than for the decoder's baseline.
            var a1 = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate(Over1, 27), gen.Silence(0.3));
            var a2 = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate(Over2, 16), gen.Silence(0.3));
            gen.AddNoise(a1, 20.0);
            gen.AddNoise(a2, 20.0);

            int solo = CwAccuracy.EditDistance(Decode(a1), Over1)
                     + CwAccuracy.EditDistance(Decode(a2), Over2);
            int both = CwAccuracy.EditDistance(mixedDecoded, expected);
            int transitionCost = Math.Max(0, both - solo);

            _out.WriteLine($"27 -> 16 WPM: {score:P1}, characters lost to the change: {transitionCost}");
            _out.WriteLine($"  \"{CwAccuracy.Normalise(mixedDecoded)}\"");

            Assert.True(score >= 0.95, $"mixed-speed QSO scored {score:P1}: {mixedDecoded}");
            Assert.True(transitionCost <= 2, $"lost {transitionCost} characters at the speed change");
        }

        [Fact]
        public void Follows_a_speed_change_upward_too()
        {
            // The harder direction: a new dah at the faster speed can be shorter
            // than twice the old dit, so a fixed boundary would read it as a dit.
            var gen = new CwSignalGenerator();

            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(), gen.Generate(Over1, 14),
                gen.Silence(1.5), gen.Generate(Over2, 30),
                gen.Silence(0.3));
            gen.AddNoise(audio, 20.0);

            var decoded = Decode(audio);
            double score = CwAccuracy.Score(decoded, Over1 + " " + Over2);

            _out.WriteLine($"14 -> 30 WPM: {score:P1}  \"{CwAccuracy.Normalise(decoded)}\"");
            Assert.True(score >= 0.90, $"upward speed change scored {score:P1}: {decoded}");
        }

        // ----------------------------------------------------------- ragged fist

        [Fact]
        public void Copes_with_a_ragged_fist()
        {
            var gen = new CwSignalGenerator(seed: 7) { FistJitter = 0.20 };
            var audio = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate(Over1, 18), gen.Silence(0.3));
            gen.AddNoise(audio, 20.0);

            var decoded = Decode(audio);
            double score = CwAccuracy.Score(decoded, Over1);

            _out.WriteLine($"20% timing jitter: {score:P1}  \"{CwAccuracy.Normalise(decoded)}\"");
            Assert.True(score >= 0.90, $"ragged fist scored {score:P1}: {decoded}");
        }

        // ------------------------------------------------------------------ QSB

        [Theory]
        // Ordinary QSB, at three fade rates. This is the case an adaptive
        // threshold is supposed to make invisible, and it does.
        [InlineData(0.05, 10.0, 20.0, 0.95)]
        [InlineData(0.10, 10.0, 20.0, 0.95)]
        [InlineData(0.25, 10.0, 20.0, 0.90)]
        // A 20 dB fade takes a 20 dB signal down to nothing at the bottom of the
        // cycle, so what is lost here is lost to the noise and not to the
        // threshold. The next case is what proves that.
        [InlineData(0.10, 20.0, 20.0, 0.55)]
        [InlineData(0.25, 20.0, 20.0, 0.40)]
        // Same 20 dB fade on a strong signal: nothing is lost, which says the
        // tracker follows the fade and the losses above are the band, not us.
        [InlineData(0.10, 20.0, 40.0, 0.95)]
        public void Rides_through_QSB_without_a_threshold_control(
            double fadeHz, double depthDb, double snrDb, double floor)
        {
            var gen   = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate(Over1, 20), gen.Silence(0.3));
            gen.ApplyQsb(audio, fadeHz, depthDb);
            gen.AddNoise(audio, snrDb);

            var decoded = Decode(audio);
            double score = CwAccuracy.Score(decoded, Over1);

            _out.WriteLine($"QSB {depthDb,4:0} dB at {fadeHz:0.00} Hz on a {snrDb:0} dB signal: " +
                           $"{score:P1}  \"{CwAccuracy.Normalise(decoded)}\"");
            Assert.True(score >= floor, $"QSB scored {score:P1}: {decoded}");
        }

        // ----------------------------------------------------------- tone offset

        [Fact]
        public void Finds_a_tone_that_is_not_on_the_configured_pitch()
        {
            var gen   = new CwSignalGenerator { ToneHz = 715.0 };
            var audio = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate(Over1, 20), gen.Silence(0.3));
            gen.AddNoise(audio, 20.0);

            var engine = new CwDecoderEngine(new CwDecoderOptions { PitchHz = 600.0 });
            var text   = new System.Text.StringBuilder();
            for (int i = 0; i < audio.Length; i += 480)
                text.Append(engine.ProcessFrame(audio.AsSpan(i, Math.Min(480, audio.Length - i))));
            text.Append(engine.Flush());

            double score = CwAccuracy.Score(text.ToString(), Over1);
            _out.WriteLine($"tone 715 Hz, pitch set to 600 Hz: tracked {engine.ToneHz:0.0} Hz, " +
                           $"zero-in offset {engine.ZeroInOffsetHz()} Hz, {score:P1}");

            Assert.InRange(engine.ToneHz, 700.0, 730.0);
            Assert.True(score >= 0.90, $"offset tone scored {score:P1}: {text}");
        }

        // -------------------------------------------------------------- reported

        [Fact]
        public void Reports_the_speed_it_is_tracking()
        {
            var gen   = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate(Over2, 22), gen.Silence(0.3));
            gen.AddNoise(audio, 25.0);

            var engine = new CwDecoderEngine();
            for (int i = 0; i < audio.Length; i += 480)
                engine.ProcessFrame(audio.AsSpan(i, Math.Min(480, audio.Length - i)));

            _out.WriteLine($"sent 22 WPM, reported {engine.WordsPerMinute:0.0} WPM");
            Assert.True(engine.IsLocked);
            Assert.InRange(engine.WordsPerMinute, 19.0, 25.0);
        }

        [Fact]
        public void Says_nothing_when_there_is_nothing_but_noise()
        {
            var gen   = new CwSignalGenerator();
            var audio = gen.Silence(5.0);
            gen.AddNoise(audio, 20.0);

            var decoded = CwAccuracy.Normalise(Decode(audio));
            _out.WriteLine($"five seconds of noise produced \"{decoded}\" ({decoded.Length} characters)");
            Assert.True(decoded.Length <= 3, $"noise produced {decoded.Length} characters: {decoded}");
        }

        /// <summary>
        /// The tone search should cover the passband the operator is listening
        /// to, and stop at its skirt. Measured on bench/sp5xoc.wav, where a
        /// station 232 Hz off the 610 Hz pitch inside a 500 Hz filter is found
        /// at the implied 250 Hz and missed at 150 - see the plan's section
        /// 4.11b. The wide end used to be clamped to 500 as well, on the
        /// argument that a lock 1.8 kHz off the pitch is a different QSO rather
        /// than a mistuned one. That cost a real signal on 2026-08-27, and the
        /// neighbouring-QSO risk is now answered where it belongs - see
        /// CwOffPitchSearchTests.
        /// </summary>
        [Theory]
        [InlineData(500,  250.0)]   // the common CW filter; the old fixed constant
        [InlineData(250,  125.0)]   // narrow CW: do not hunt outside what is audible
        [InlineData(2400, 1200.0)]  // SSB: the whole passband is searchable
        [InlineData(3600, 1800.0)]
        [InlineData(6000, 1800.0)]  // clamped: wider than any real CW passband
        [InlineData(100,  100.0)]   // clamped up: narrower than the tone estimate's own error
        [InlineData(50,   100.0)]
        public void Tone_search_covers_the_passband_and_no_more(int filterHz, double expected)
        {
            Assert.Equal(expected, CwDecoderOptions.SearchWindowForFilterWidth(filterHz));
        }
    }
}
