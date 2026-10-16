using System.Collections.Generic;
using System.Text;
using RadioWebControl.Core.Services.Cw;
using Xunit;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// Two characters sent with too short a gap between them. The element
    /// decoder reads them as one symbol, and when that symbol is not a Morse
    /// character both letters used to vanish. The shape is taken from
    /// bench/good-sig-14017266-20261016d.wav: OY1CT at 21 WPM, a 105 ms
    /// (1.8 dit) gap between the Q and the D of "CQ DE", and the reader
    /// printing "C E" for "CQ DE". See
    /// <see cref="CwElementDecoderOptions.SplitInvalidMinGapDits"/>.
    /// </summary>
    public class CwRunTogetherTests
    {
        private const double DitMs = 60.0;   // 20 WPM, the decoder's starting guess
        private const double HopMs = 5.0;

        /// <summary>
        /// Key timeline for <paramref name="text"/> in textbook timing, except
        /// that the gap after the character at <paramref name="shortAfter"/>
        /// is <paramref name="shortGapDits"/> dits.
        /// </summary>
        private static List<(bool Down, double Ms)> Keying(string text, int shortAfter, double shortGapDits)
        {
            var runs = new List<(bool, double)> { (false, 500.0) };
            for (int c = 0; c < text.Length; c++)
            {
                if (text[c] == ' ') { runs[^1] = (false, 7 * DitMs); continue; }
                string code = MorseTable.Encode(text[c])!;
                for (int e = 0; e < code.Length; e++)
                {
                    runs.Add((true, code[e] == '-' ? 3 * DitMs : DitMs));
                    runs.Add((false, DitMs));
                }
                runs[^1] = (false, (c == shortAfter ? shortGapDits : 3.0) * DitMs);
            }
            runs[^1] = (false, 2000.0);
            return runs;
        }

        private static string Decode(List<(bool Down, double Ms)> runs, double splitDits)
        {
            var decoder = new CwElementDecoder(new CwElementDecoderOptions { SplitInvalidMinGapDits = splitDits });
            var text = new StringBuilder();
            double t = 0.0;
            foreach (var (down, ms) in runs)
            {
                for (double end = t + ms; t < end; t += HopMs)
                {
                    text.Append(decoder.Push(new CwToneSample
                    {
                        TimeSeconds   = t / 1000.0,
                        KeyDown       = down,
                        Magnitude     = down ? 1.0 : 0.01,
                        NoiseLevel    = 0.01,
                        SignalPresent = true,
                    }));
                }
            }
            text.Append(decoder.Flush());
            return text.ToString().Replace(" ", "");
        }

        // The short gap is after the second Q, index 3, so Q and D run
        // together as --.--.. - not a character.
        private static List<(bool Down, double Ms)> RunTogether() => Keying("CQCQDE", shortAfter: 3, shortGapDits: 1.8);

        [Fact]
        public void Without_the_split_the_merged_pair_is_lost()
        {
            Assert.DoesNotContain("CQCQ", Decode(RunTogether(), splitDits: 0.0));
        }

        [Fact]
        public void An_invalid_merge_is_split_where_the_sender_paused_longest()
        {
            Assert.Equal("CQCQDE", Decode(RunTogether(), splitDits: 1.4));
        }

        [Fact]
        public void A_valid_symbol_is_never_split()
        {
            // A (.-) and T (-) run together are .--, which is W. That is a
            // character, so it stands, even though the gap clears the floor.
            Assert.Equal("W", Decode(Keying("AT", shortAfter: 0, shortGapDits: 1.8), splitDits: 1.4));
        }

        [Fact]
        public void A_gap_below_the_floor_is_not_a_split_point()
        {
            // Q (--.-) and D (-..) with only a normal element gap between
            // them: nothing in the timing says where one ends, so nothing
            // is invented.
            Assert.DoesNotContain("QD", Decode(Keying("QD", shortAfter: 0, shortGapDits: 1.0), splitDits: 1.4));
        }
    }
}
