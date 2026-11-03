using System.Collections.Generic;
using RadioWebControl.Core.Services.Cw;
using Xunit;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// A run of all-dah text is Morse, not Chatter. The shape is taken from
    /// the Morse Code Ninja Speed-Racing file, where OM sent five times read
    /// Chatter and the held copy was cleared. See
    /// <see cref="CwElementDecoderOptions.ChatterExemptAllDahs"/>.
    /// </summary>
    public class CwAllDahsReadabilityTests
    {
        private const double DitMs = 60.0;
        private const double HopMs = 5.0;

        private static List<(bool Down, double Ms)> Keying(string text)
        {
            var runs = new List<(bool, double)> { (false, 500.0) };
            foreach (char ch in text)
            {
                if (ch == ' ') { runs[^1] = (false, 7 * DitMs); continue; }
                foreach (char e in MorseTable.Encode(ch)!)
                {
                    runs.Add((true, e == '-' ? 3 * DitMs : DitMs));
                    runs.Add((false, DitMs));
                }
                runs[^1] = (false, 3 * DitMs);
            }
            return runs;
        }

        /// <summary>Readability straight after the last mark, before any silence can age it.</summary>
        private static CwReadability ReadabilityAfter(string text, bool exempt)
        {
            var decoder = new CwElementDecoder(new CwElementDecoderOptions { ChatterExemptAllDahs = exempt });
            double t = 0.0;
            foreach (var (down, ms) in Keying(text))
            {
                for (double end = t + ms; t < end; t += HopMs)
                {
                    decoder.Push(new CwToneSample
                    {
                        TimeSeconds   = t / 1000.0,
                        KeyDown       = down,
                        Magnitude     = down ? 1.0 : 0.01,
                        NoiseLevel    = 0.01,
                        SignalPresent = true,
                    });
                }
            }
            return decoder.Readability;
        }

        private const string Text = "CQ CQ DE TEST TEST OM OM OM OM OM OM";

        [Fact]
        public void A_run_of_OM_stays_readable()
            => Assert.Equal(CwReadability.Readable, ReadabilityAfter(Text, exempt: true));

        [Fact]
        public void Without_the_exemption_it_reads_as_chatter()
            => Assert.Equal(CwReadability.Chatter, ReadabilityAfter(Text, exempt: false));

        [Fact]
        public void Uniform_short_marks_are_still_chatter()
            // Blips at dit length with no dahs at all: the flicker the floor
            // exists to catch, and nothing this exemption may wave through.
            => Assert.Equal(CwReadability.Chatter, ReadabilityAfter("EEEEE EEEEE EEEEE EEEEE", exempt: true));
    }
}
