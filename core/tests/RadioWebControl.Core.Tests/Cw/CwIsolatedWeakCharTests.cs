using System.Collections.Generic;
using System.Text;
using RadioWebControl.Core.Services.Cw;
using Xunit;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// A quiet one-element character alone between word gaps is the noise in
    /// the gap between overs, not the station. The shape is taken from
    /// bench/iwc-on8ga-7012-5k-1514z.wav, where the reader printed
    /// "TEST E I E I CQ ON8GA" off marks at about half the station's peak.
    /// See <see cref="CwElementDecoderOptions.IsolatedWeakCharRatio"/>.
    /// </summary>
    public class CwIsolatedWeakCharTests
    {
        private const double DitMs = 60.0;   // 20 WPM, the decoder's starting guess
        private const double HopMs = 5.0;
        private const double Weak  = 0.4;    // against a station at 1.0

        /// <summary>
        /// Key timeline in textbook timing. <paramref name="weak"/> gives the
        /// indices into <paramref name="text"/> whose marks are sent at
        /// <see cref="Weak"/> rather than full strength.
        /// </summary>
        private static List<(bool Down, double Ms, double Mag)> Keying(string text, params int[] weak)
        {
            var runs = new List<(bool, double, double)> { (false, 500.0, 0.0) };
            for (int c = 0; c < text.Length; c++)
            {
                if (text[c] == ' ') { runs[^1] = (false, 7 * DitMs, 0.0); continue; }
                double mag = System.Array.IndexOf(weak, c) >= 0 ? Weak : 1.0;
                string code = MorseTable.Encode(text[c])!;
                for (int e = 0; e < code.Length; e++)
                {
                    runs.Add((true, code[e] == '-' ? 3 * DitMs : DitMs, mag));
                    runs.Add((false, DitMs, 0.0));
                }
                runs[^1] = (false, 3 * DitMs, 0.0);
            }
            runs[^1] = (false, 2000.0, 0.0);
            return runs;
        }

        private static string Decode(List<(bool Down, double Ms, double Mag)> runs, double ratio = 0.7)
        {
            var decoder = new CwElementDecoder(new CwElementDecoderOptions { IsolatedWeakCharRatio = ratio });
            var text = new StringBuilder();
            double t = 0.0;
            foreach (var (down, ms, mag) in runs)
            {
                for (double end = t + ms; t < end; t += HopMs)
                {
                    text.Append(decoder.Push(new CwToneSample
                    {
                        TimeSeconds   = t / 1000.0,
                        KeyDown       = down,
                        Magnitude     = down ? mag : 0.01,
                        NoiseLevel    = 0.01,
                        SignalPresent = true,
                    }));
                }
            }
            text.Append(decoder.Flush());
            return text.ToString().Trim();
        }

        private const string Over = "CQ CQ DE TEST TEST";

        [Fact]
        public void A_weak_lone_dit_between_overs_is_withheld()
        {
            // Over, then a quiet E, then the next CQ.
            string text = Over + " E CQ";
            Assert.Equal(Over + " CQ", Decode(Keying(text, text.Length - 4)));
        }

        [Fact]
        public void With_the_gate_off_it_is_printed()
        {
            string text = Over + " E CQ";
            Assert.Equal(text, Decode(Keying(text, text.Length - 4), ratio: 0.0));
        }

        [Fact]
        public void A_full_strength_lone_E_is_printed()
        {
            string text = Over + " E CQ";
            Assert.Equal(text, Decode(Keying(text)));
        }

        [Fact]
        public void A_weak_E_inside_a_word_is_printed()
        {
            // TEST with its E quiet: a fading caller, not noise. It stands
            // between letters, not between word gaps.
            string text = Over + " TEST";
            Assert.Equal(text, Decode(Keying(text, text.Length - 3)));
        }

        [Fact]
        public void A_weak_lone_T_of_full_length_is_printed()
        {
            // A three-dit dah is nothing like a noise blip, so a quieter
            // station's T survives.
            string text = Over + " T CQ";
            Assert.Equal(text, Decode(Keying(text, text.Length - 4)));
        }

        [Fact]
        public void A_weak_run_of_dits_between_overs_is_withheld()
        {
            // "EEE" in the quiet: three blips, each too close to the next to
            // be a lone character, and the word is nothing else.
            string text = Over + " EEE CQ";
            int at = Over.Length + 1;
            Assert.Equal(Over + " CQ", Decode(Keying(text, at, at + 1, at + 2)));
        }

        [Fact]
        public void A_weak_E_starting_a_word_is_printed()
        {
            // A fading caller's ER: the E is quiet, the R is not.
            string text = Over + " ER CQ";
            int at = Over.Length + 1;
            Assert.Equal(text, Decode(Keying(text, at)));
        }
    }
}
