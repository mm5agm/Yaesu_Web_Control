using System.Text;
using RadioWebControl.Core.Services.Cw;
using Xunit;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// The opening of a transmission is the part an operator can least afford
    /// to lose - on air it is the callsign - and it is also the part the
    /// decoder knows least about, because no marks have arrived yet to judge.
    ///
    /// CwDecoderEngine holds text until the marks prove readable, and discards
    /// the hold once it has gone stale. Those two rules were in conflict at the
    /// start of a slow transmission: the stale clock ran from the first frame,
    /// so on a Farnsworth sender it expired before the tenth mark had arrived
    /// and the opening was thrown away rather than released. Measured on
    /// 2026-09-01, a 5 WPM message reaches its tenth mark at about 8.4 s and
    /// the horizon is 5 s, so "CQ CQ DE MM5AGM MM5AGM K" came back as
    /// "CQ DE MM5AGM MM5AGM K" - every character correct, the first word gone.
    ///
    /// The distinction the fix rests on is that "not readable" and "no verdict
    /// yet" are not the same state, so both are asserted here: the opening of a
    /// slow sender survives, and the hold still expires when what is being held
    /// was decoded from something that has since been judged and found wanting.
    /// </summary>
    public class CwOpeningHoldTests
    {
        private readonly ITestOutputHelper _out;

        public CwOpeningHoldTests(ITestOutputHelper output) => _out = output;

        private static string Decode(float[] audio)
        {
            var engine = new CwDecoderEngine(new CwDecoderOptions());
            var text = new StringBuilder();
            const int frame = 480;
            for (int i = 0; i < audio.Length; i += frame)
                text.Append(engine.ProcessFrame(audio.AsSpan(i, System.Math.Min(frame, audio.Length - i))));
            text.Append(engine.Flush());
            return CwAccuracy.Normalise(text.ToString()).Trim();
        }

        /// <summary>
        /// Slow enough that the readability window cannot fill inside the
        /// five-second hold horizon. Both words of the call must appear.
        /// </summary>
        [Theory]
        [InlineData(5.0)]
        [InlineData(8.0)]
        public void Keeps_the_opening_of_a_sender_too_slow_to_judge_in_five_seconds(double wpm)
        {
            var gen   = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(), gen.Generate("CQ CQ DE MM5AGM K", wpm, 14.6), gen.Silence(0.5));
            gen.AddNoise(audio, 20.0);

            var decoded = Decode(audio);
            _out.WriteLine($"{wpm,4:0} WPM: \"{decoded}\"");

            Assert.StartsWith("CQ CQ", decoded);
        }

        /// <summary>
        /// The other half. Once the windows have filled the verdict exists, so
        /// silence is stale rather than undecided and the hold is dropped:
        /// a long quiet gap must not push the first station's text onto the
        /// front of the second's.
        /// </summary>
        [Fact]
        public void Drops_held_text_once_the_marks_behind_it_have_gone_stale()
        {
            var gen = new CwSignalGenerator();

            // A burst of noise blips long enough to fill the readability
            // windows, a minute of nothing, then a clean signal. Whatever the
            // blips decoded to must not be printed in front of the real copy.
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(),
                gen.Generate("EEEEEEEEEEEE", 30.0),
                gen.Silence(60.0),
                gen.Generate("CQ DE MM5AGM", 20.0),
                gen.Silence(0.5));
            gen.AddNoise(audio, 20.0);

            var decoded = Decode(audio);
            _out.WriteLine($"\"{decoded}\"");

            Assert.EndsWith("CQ DE MM5AGM", decoded);
            Assert.DoesNotContain("EEEE", decoded);
        }
    }
}
