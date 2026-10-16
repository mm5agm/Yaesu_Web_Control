using System.Collections.Generic;
using RadioWebControl.Core.Services.Cw;
using Xunit;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// Real Morse that is nearly all dits. Taken from
    /// bench/slow-cw-20261016e.wav, where "RST 5 5N 5 5N K" at 13 wpm read
    /// Chatter and was cleared from the screen although every character in it
    /// decoded. See <see cref="CwElementDecoderOptions.ChatterExemptDahs"/>.
    /// </summary>
    public class CwChatterExemptTests
    {
        private const double DitMs = 60.0;
        private const double HopMs = 5.0;

        private static CwReadability ReadabilityAfter(IEnumerable<(bool Down, double Ms)> runs, int exemptDahs)
        {
            var decoder = new CwElementDecoder(new CwElementDecoderOptions { ChatterExemptDahs = exemptDahs });
            double t = 0.0;
            foreach (var (down, ms) in runs)
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

        private static List<(bool Down, double Ms)> Keying(string text)
        {
            var runs = new List<(bool, double)> { (false, 500.0) };
            foreach (char ch in text)
            {
                foreach (char e in MorseTable.Encode(ch)!)
                {
                    runs.Add((true, e == '-' ? 3 * DitMs : DitMs));
                    runs.Add((false, DitMs));
                }
                runs[^1] = (false, 3 * DitMs);
            }
            return runs;
        }

        [Fact]
        public void Without_the_exemption_mostly_dits_reads_as_chatter()
        {
            Assert.Equal(CwReadability.Chatter, ReadabilityAfter(Keying("55N55N"), exemptDahs: 0));
        }

        [Fact]
        public void Two_dahs_in_the_window_are_not_chatter()
        {
            Assert.NotEqual(CwReadability.Chatter, ReadabilityAfter(Keying("55N55N"), exemptDahs: 2));
        }

        [Fact]
        public void Dits_alone_are_still_chatter()
        {
            Assert.Equal(CwReadability.Chatter, ReadabilityAfter(Keying("5555"), exemptDahs: 2));
        }
    }
}
