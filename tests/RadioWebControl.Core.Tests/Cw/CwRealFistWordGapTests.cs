using RadioWebControl.Core.Services.Cw;
using Xunit;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// Word gaps on a fist that does not scale its two gaps together.
    ///
    /// The reader used to split words at a fixed 1.8 times the measured
    /// character gap. That is a model of an operator, and it says that whatever
    /// they do to their character gaps they do proportionally to their word
    /// gaps. `bench/mkii-i1yrl.wav`, the only plain-QSO recording in the bench
    /// corpus, is an operator who does not: measured from the audio on
    /// 2026-09-01 his character gaps centre on 4.4 dits and his word gaps on
    /// 5.8, a ratio of 1.3. Scaling from the one to the other put the split at
    /// 6.7 dits, above two thirds of his word gaps, and the reader printed
    /// TKSFORINFOAND, QTHNRTIN and CQCQCQDE.
    ///
    /// These tests key known text with that measured fist, so the thing the
    /// recording only suggests can be asserted against ground truth.
    /// </summary>
    public class CwRealFistWordGapTests
    {
        private const double I1yrlCharGapDits = 4.4;
        private const double I1yrlWordGapDits = 5.8;

        private static string Decode(float[] audio, double toneHz)
        {
            var engine = new CwDecoderEngine(new CwDecoderOptions { PitchHz = toneHz });

            var text = new System.Text.StringBuilder();
            const int frame = 480;
            for (int i = 0; i + frame <= audio.Length; i += frame)
                text.Append(engine.ProcessFrame(audio.AsSpan(i, frame)));
            text.Append(engine.Flush());
            return CwAccuracy.Normalise(text.ToString()).Trim();
        }

        [Theory]
        [InlineData(0.00)]
        [InlineData(0.08)]
        public void Finds_word_gaps_on_a_fist_that_stretched_only_its_character_gaps(double jitter)
        {
            var gen = new CwSignalGenerator { ToneHz = 600.0, FistJitter = jitter };
            const string sent = "TKS FOR INFO AND UR RST 599 QTH NR TURIN NAME LUC";

            var audio = CwSignalGenerator.Concat(gen.LeadIn(),
                               gen.GenerateWithFist(sent, 28.0,
                                                    I1yrlCharGapDits, I1yrlWordGapDits),
                               gen.Silence(0.5));

            string got = Decode(audio, 600.0);

            // The characters are not in question here - every one of them is
            // clear of the noise floor and the suite already covers element
            // decoding. What is in question is where the spaces fall, so score
            // the word boundaries and nothing else.
            Assert.True(CwAccuracy.Score(got, sent) >= 0.90,
                $"expected >=90% on a real fist, got {CwAccuracy.Score(got, sent) * 100.0:F1}%: {got}");
        }

        /// <summary>
        /// The other half of the bargain. Erring towards not splitting is the
        /// cheap direction - a missed word gap costs one edit per word, a
        /// spurious one costs an edit per character - so a sender who leaves no
        /// word gaps at all must not have any invented for them.
        /// </summary>
        [Fact]
        public void Invents_no_word_gaps_when_the_sender_leaves_none()
        {
            var gen = new CwSignalGenerator { ToneHz = 600.0 };

            // Character gaps only, every one of them the same: there is one
            // population here and the reader must not find two in it.
            var audio = CwSignalGenerator.Concat(gen.LeadIn(),
                               gen.GenerateWithFist("CQCQCQDEI1YRLI1YRLK", 28.0,
                                                    charGapDits: 4.4, wordGapDits: 4.4),
                               gen.Silence(0.5));

            string got = Decode(audio, 600.0);

            Assert.False(got.Contains(' '),
                $"expected no word gaps at all, got: {got}");
        }

    }
}
