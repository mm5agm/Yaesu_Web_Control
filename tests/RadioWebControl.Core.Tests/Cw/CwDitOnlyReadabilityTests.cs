using RadioWebControl.Core.Services.Cw;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// The readability check that looks at the text rather than the timing.
    ///
    /// The two timing checks share a blind spot, and it was measured on the
    /// ARRL practice files on 2026-08-27. Above about 30 WPM the adaptive
    /// de-glitch floor drops under MinElementMs, so nothing is discarded and
    /// the discarded-run fraction sees nothing; the spurious marks themselves
    /// are individually plausible, so the mark spread sees nothing either. At
    /// 40 WPM and 0 dB the reader emitted 511 characters that were 15.6%
    /// correct and reported them Readable 64% of the time - it was not copying
    /// badly, it was counting the band and presenting the count as text.
    ///
    /// Spurious short marks can only ever assemble into letters made of dits,
    /// which is why the fault is visible in the output and nowhere else. The
    /// sent text of the ten practice files runs 23.3% to 30.8% dit-only; the
    /// junk above ran 68% to 72%.
    ///
    /// The check scores and never edits. Nothing distinguishes a spurious S
    /// from the S in a callsign, so letters are never removed - a whole block
    /// is withheld or it is not.
    /// </summary>
    public class CwDitOnlyReadabilityTests
    {
        private readonly ITestOutputHelper _out;

        public CwDitOnlyReadabilityTests(ITestOutputHelper output) => _out = output;

        private static (string Text, CwDecoderEngine Engine) Run(float[] audio)
        {
            var engine = new CwDecoderEngine(new CwDecoderOptions { PitchHz = 600.0 });
            var text = new System.Text.StringBuilder();

            const int frame = 480;
            for (int i = 0; i < audio.Length; i += frame)
            {
                int n = Math.Min(frame, audio.Length - i);
                text.Append(engine.ProcessFrame(audio.AsSpan(i, n)));
            }
            text.Append(engine.Flush());
            return (text.ToString(), engine);
        }

        /// <summary>
        /// A long stretch of nothing but dit-only letters is what the fault
        /// produces, and it must not be offered as copy however clean the
        /// timing is. This signal is perfectly formed - the point is that
        /// perfect timing is not enough to make it text.
        /// </summary>
        [Fact]
        public void A_stream_of_dit_only_letters_is_not_offered_as_readable()
        {
            var gen = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(),
                gen.Generate("EEE III SSS HHH EEE III SSS HHH EEE III SSS HHH " +
                             "EEE III SSS HHH EEE III SSS HHH", 20.0),
                gen.Silence(0.5));

            var (text, engine) = Run(audio);
            _out.WriteLine($"readability {engine.Readability}, text \"{text.Trim()}\"");

            Assert.NotEqual(CwReadability.Readable, engine.Readability);
        }

        /// <summary>
        /// The guard against the obvious way to get the above for free. Real
        /// traffic is roughly a quarter dit-only letters and must be read
        /// normally - a check that quietly suppressed ordinary copy would be
        /// far worse than the fault it fixes.
        /// </summary>
        [Fact]
        public void Ordinary_traffic_is_still_read()
        {
            var gen = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(),
                gen.Generate("CQ CQ DE MM5AGM MM5AGM K RST 599 599 NAME COLIN " +
                             "QTH SCOTLAND HW CPY? BK TU 73 DE MM5AGM", 20.0),
                gen.Silence(0.5));

            var (text, engine) = Run(audio);
            _out.WriteLine($"readability {engine.Readability}, text \"{text.Trim()}\"");

            Assert.Equal(CwReadability.Readable, engine.Readability);
            Assert.Contains("MM5AGM", text);
        }
    }
}
