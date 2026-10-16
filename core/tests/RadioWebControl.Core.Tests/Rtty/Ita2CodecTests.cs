using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RadioWebControl.Core.Services.Rtty;
using Xunit;

namespace RadioWebControl.Core.Tests.Rtty
{
    /// <summary>
    /// The alphabet, both ways round.
    ///
    /// The round-trip tests are the point of this file. Only the decoding half
    /// is wired into an application today, so nothing else would notice if the
    /// encoding half were wrong - and it would be found out on the air, by
    /// somebody else, after a transmitter had already sent the wrong thing.
    /// Encoding every printable character and decoding it back is what lets the
    /// transmit side be written now and trusted later.
    /// </summary>
    public class Ita2CodecTests
    {
        // Spot checks against the published table, so a wholesale mistake in
        // the bit order cannot hide behind a round trip that is wrong both
        // ways. E and T are the one-bit codes; the shifts and space are the
        // three every listing agrees on.
        [Theory]
        [InlineData('E', 0b00001)]
        [InlineData('T', 0b10000)]
        [InlineData('A', 0b00011)]
        [InlineData('Z', 0b10001)]
        [InlineData('Q', 0b10111)]
        [InlineData(' ', 0b00100)]
        public void Letters_match_the_published_table(char c, int expected)
        {
            Assert.True(Ita2Codec.TryGetCode(c, RttyFigureSet.Ita2, out var code, out var figures));
            Assert.False(figures);
            Assert.Equal(expected, code);
        }

        [Fact]
        public void The_two_shifts_are_all_ones_and_the_figures_pattern()
        {
            Assert.Equal(31, Ita2Codec.Ltrs);
            Assert.Equal(27, Ita2Codec.Figs);

            // Neither shift prints anything in either table.
            Assert.Equal('\0', Ita2Codec.ToChar(Ita2Codec.Ltrs, figures: false));
            Assert.Equal('\0', Ita2Codec.ToChar(Ita2Codec.Ltrs, figures: true));
            Assert.Equal('\0', Ita2Codec.ToChar(Ita2Codec.Figs, figures: false));
            Assert.Equal('\0', Ita2Codec.ToChar(Ita2Codec.Figs, figures: true));
        }

        [Theory]
        [InlineData(RttyFigureSet.Ita2)]
        [InlineData(RttyFigureSet.UsTty)]
        public void Every_printable_character_survives_a_round_trip(RttyFigureSet set)
        {
            // Everything this table can carry, found by asking it rather than
            // by listing it here - a character added to the table should be
            // covered by this test without the test being edited.
            var printable = new List<char>();
            for (int code = 0; code < 32; code++)
            {
                foreach (var figures in new[] { false, true })
                {
                    var c = Ita2Codec.ToChar((byte)code, figures, set);
                    if (c != '\0' && c != '\r' && c != '\n' && !printable.Contains(c))
                        printable.Add(c);
                }
            }

            // A guard against a table that has collapsed, not a count to pin:
            // ITA-2 carries 49 (26 letters, space and 22 figures) and the
            // American set a few more, because it fills the national positions
            // ITA-2 leaves empty.
            Assert.True(printable.Count >= 45, $"only {printable.Count} printable characters");

            var text = new string(printable.ToArray());
            var codes = Ita2Codec.Encode(text, set);
            var back = new Ita2Decoder(set).Feed(codes);

            Assert.Equal(text, back);
        }

        [Theory]
        [InlineData(RttyFigureSet.Ita2)]
        [InlineData(RttyFigureSet.UsTty)]
        public void A_realistic_over_survives_a_round_trip(RttyFigureSet set)
        {
            const string text = "CQ CQ DE MM5AGM MM5AGM K";
            var back = new Ita2Decoder(set).Feed(Ita2Codec.Encode(text, set));
            Assert.Equal(text, back);
        }

        [Fact]
        public void Mixed_letters_and_figures_survive_a_round_trip()
        {
            const string text = "RST 599 QTH IS 55N 4W";
            var back = new Ita2Decoder().Feed(Ita2Codec.Encode(text));
            Assert.Equal(text, back);
        }

        [Fact]
        public void Lower_case_comes_back_folded_up()
        {
            var back = new Ita2Decoder().Feed(Ita2Codec.Encode("de mm5agm"));
            Assert.Equal("DE MM5AGM", back);
        }

        [Fact]
        public void Characters_the_table_cannot_carry_are_dropped_not_guessed()
        {
            // A wrong character on the air is worse than a missing one, so the
            // encoder must not substitute.
            var back = new Ita2Decoder().Feed(Ita2Codec.Encode("ABéCD"));
            Assert.Equal("ABCD", back);
        }

        [Fact]
        public void A_newline_goes_out_as_carriage_return_then_line_feed()
        {
            var codes = Ita2Codec.Encode("A\nB", startInLetters: false);

            var cr = codes.ToList().IndexOf(Ita2Codec.Cr);
            var lf = codes.ToList().IndexOf(Ita2Codec.Lf);

            Assert.True(cr >= 0 && lf >= 0, "both control codes are sent");
            Assert.True(cr < lf, "carriage return precedes line feed, or the head is still travelling");
        }

        // --- USOS -----------------------------------------------------------

        [Fact]
        public void With_usos_on_a_figure_after_a_space_is_shifted_again()
        {
            // "1 2" - the space unshifts the far end, so the 2 needs its own
            // FIGS or it prints as a W.
            var codes = Ita2Codec.Encode("1 2", usos: true, startInLetters: false);
            Assert.Equal(2, codes.Count(c => c == Ita2Codec.Figs));

            Assert.Equal("1 2", new Ita2Decoder(usos: true).Feed(codes));
        }

        [Fact]
        public void With_usos_off_the_shift_is_not_repeated()
        {
            var codes = Ita2Codec.Encode("1 2", usos: false, startInLetters: false);
            Assert.Equal(1, codes.Count(c => c == Ita2Codec.Figs));

            Assert.Equal("1 2", new Ita2Decoder(usos: false).Feed(codes));
        }

        [Fact]
        public void A_usos_receiver_reading_a_non_usos_sender_is_the_classic_mismatch()
        {
            // Worth pinning down: this is what the radio's two USOS switches
            // exist to resolve, and the failure is silent - digits arrive as
            // letters with nothing to say anything went wrong.
            var codes = Ita2Codec.Encode("1 2", usos: false, startInLetters: false);
            var read = new Ita2Decoder(usos: true).Feed(codes);

            Assert.Equal("1 W", read);
        }

        [Fact]
        public void The_decoder_starts_and_resets_in_letters()
        {
            var d = new Ita2Decoder();
            Assert.False(d.InFigures);

            d.Feed(Ita2Codec.Figs);
            Assert.True(d.InFigures);

            d.Reset();
            Assert.False(d.InFigures);
        }

        // --- shifts are not sent when they are not needed --------------------

        [Fact]
        public void A_run_of_letters_carries_one_shift_at_most()
        {
            var codes = Ita2Codec.Encode("HELLO", startInLetters: false);
            Assert.DoesNotContain(Ita2Codec.Figs, codes);
            Assert.Equal("HELLO".Length, codes.Count);
        }

        [Fact]
        public void Space_does_not_force_a_shift_of_its_own()
        {
            // Space sits at the same code in both tables. An encoder that
            // treated it as a letter would shift out of figures to send it and
            // back again, doubling the shifts on a line of numbers.
            var codes = Ita2Codec.Encode("12 34", usos: false, startInLetters: false);
            Assert.Equal(1, codes.Count(c => c == Ita2Codec.Figs));
            Assert.DoesNotContain(Ita2Codec.Ltrs, codes);
        }

        [Fact]
        public void Starting_in_letters_emits_a_leading_shift()
        {
            // The far end's shift state is whatever the last transmission left
            // behind, so an over normally opens by saying which table it means.
            var codes = Ita2Codec.Encode("A", startInLetters: true);
            Assert.Equal(Ita2Codec.Ltrs, codes[0]);
        }

        // --- where the two tables genuinely differ ---------------------------

        [Fact]
        public void The_two_figure_sets_disagree_where_the_standard_leaves_room()
        {
            // Same five bits, different character. This is why the figures set
            // is the operator's choice and cannot be detected off the air.
            const byte d = 0b01001;   // letters: D

            Assert.Equal('$', Ita2Codec.ToChar(d, figures: true, RttyFigureSet.UsTty));
            Assert.Equal('\0', Ita2Codec.ToChar(d, figures: true, RttyFigureSet.Ita2));

            const byte s = 0b00101;   // letters: S
            Assert.Equal('\'', Ita2Codec.ToChar(s, figures: true, RttyFigureSet.Ita2));
            Assert.Equal('\a', Ita2Codec.ToChar(s, figures: true, RttyFigureSet.UsTty));
        }

        [Fact]
        public void The_letters_table_is_the_same_in_both_sets()
        {
            for (int code = 0; code < 32; code++)
            {
                Assert.Equal(
                    Ita2Codec.ToChar((byte)code, figures: false, RttyFigureSet.Ita2),
                    Ita2Codec.ToChar((byte)code, figures: false, RttyFigureSet.UsTty));
            }
        }

        [Fact]
        public void No_two_letters_share_a_code()
        {
            var seen = new HashSet<char>();
            for (int code = 0; code < 32; code++)
            {
                var c = Ita2Codec.ToChar((byte)code, figures: false);
                if (c == '\0') continue;
                Assert.True(seen.Add(c), $"{c} appears twice in the letters table");
            }

            // 26 letters, space, CR and LF.
            Assert.Equal(29, seen.Count);
        }
    }
}
