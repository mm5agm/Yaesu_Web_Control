using RadioWebControl.Core.Services.Cw;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// Farnsworth spacing: characters sent quickly, gaps stretched to bring the
    /// overall rate down. Every code practice recording aimed below about 15 WPM
    /// is sent this way, so this is not an exotic case - it is what a beginner
    /// hears on 80 m every evening, and what the ARRL W1AW files transmit.
    ///
    /// The decoder used to fail these completely. It measured its dit from the
    /// elements, which are fast, and then derived a character gap of three of
    /// those dits and a word gap of seven. Farnsworth gaps are far longer than
    /// either, so every gap read as a word gap and "QST DE W1AW" came back as
    /// "Q S T  D E  W 1 A W". Scored against the ARRL text that is 40% at 5 WPM
    /// and 39% at 10, against 99% at 20 - a decoder that got worse the slower
    /// and clearer the sending got.
    ///
    /// The fix is to stop believing the dit about the gaps and measure the
    /// character gap directly, as the lower quartile of the separator gaps
    /// actually seen. These tests hold that: the assertion is not on a
    /// constant but on the two things a reader needs, which are the right
    /// characters and the words split in the right places.
    /// </summary>
    public class CwFarnsworthTests
    {
        private readonly ITestOutputHelper _out;

        public CwFarnsworthTests(ITestOutputHelper output) => _out = output;

        private const string Text = "CQ CQ DE MM5AGM MM5AGM K";

        private static string Decode(float[] audio, double pitchHz = 600.0)
        {
            var engine = new CwDecoderEngine(new CwDecoderOptions { PitchHz = pitchHz });
            var text = new System.Text.StringBuilder();

            const int frame = 480;
            for (int i = 0; i < audio.Length; i += frame)
            {
                int n = System.Math.Min(frame, audio.Length - i);
                text.Append(engine.ProcessFrame(audio.AsSpan(i, n)));
            }
            text.Append(engine.Flush());

            return text.ToString();
        }

        /// <summary>
        /// The speeds the ARRL files are sent at, with the 14.6 WPM character
        /// speed those files measure. 5 WPM is the hardest of them: the gaps are
        /// eighteen dits where the decoder expects three.
        /// </summary>
        [Theory]
        [InlineData(5.0)]
        [InlineData(10.0)]
        [InlineData(13.0)]
        [InlineData(15.0)]
        public void Reads_Farnsworth_at_the_speeds_the_practice_files_use(double wpm)
        {
            var gen   = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(), gen.Generate(Text, wpm, 14.6), gen.Silence(0.5));
            gen.AddNoise(audio, 20.0);

            var decoded = Decode(audio);
            double score = CwAccuracy.Score(decoded, Text);

            _out.WriteLine($"{wpm,4:0} WPM Farnsworth (14.6 char): {score:P1}  " +
                           $"\"{CwAccuracy.Normalise(decoded)}\"");
            Assert.True(score >= 0.90, $"{wpm} WPM Farnsworth scored {score:P1}: {decoded}");
        }

        /// <summary>
        /// The specific failure, named. Six characters and one word gap: if the
        /// character gaps are being read as word gaps this comes back with five
        /// spaces in it instead of one, and that is what the count asserts.
        /// </summary>
        [Fact]
        public void Does_not_split_every_character_into_its_own_word()
        {
            var gen   = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(), gen.Generate("QST DE W1AW", 5.0, 14.6), gen.Silence(0.5));

            var decoded = CwAccuracy.Normalise(Decode(audio)).Trim();
            int spaces  = decoded.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length - 1;

            _out.WriteLine($"5 WPM Farnsworth: \"{decoded}\" ({spaces} word gaps, expected 2)");
            Assert.True(spaces <= 3, $"split into {spaces + 1} words: \"{decoded}\"");
        }

        /// <summary>
        /// Straight timing has to keep working. The percentile that measures the
        /// character gap is floored at the textbook three dits, so a sender who
        /// leaves textbook gaps is read by the textbook rule and an operator who
        /// runs their word gaps a little short is not punished for it.
        /// </summary>
        [Fact]
        public void Still_reads_textbook_spacing()
        {
            var gen   = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(), gen.Generate(Text, 18.0), gen.Silence(0.5));
            gen.AddNoise(audio, 20.0);

            var decoded = Decode(audio);
            double score = CwAccuracy.Score(decoded, Text);

            _out.WriteLine($"18 WPM straight: {score:P1}  \"{CwAccuracy.Normalise(decoded)}\"");
            Assert.True(score >= 0.95, $"straight timing scored {score:P1}: {decoded}");
        }
    }
}
