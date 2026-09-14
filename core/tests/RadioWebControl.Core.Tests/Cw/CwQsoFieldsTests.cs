using System.Linq;
using RadioWebControl.Core.Services.Cw;
using Xunit;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// The QSO field extractor is the one place in the CW path that is allowed
    /// to interpret rather than transcribe, so the tests are mostly about the
    /// limits of that licence: what it must not turn into a callsign, and what
    /// it must not turn into a digit.
    /// </summary>
    public class CwQsoFieldsTests
    {
        private static string Best(System.Collections.Generic.IReadOnlyList<CwQsoFields.Candidate> c)
            => c.Count > 0 ? c[0].Value : "";

        [Fact]
        public void The_callsign_after_DE_wins()
        {
            // Both are real calls and both are call-shaped. Only position
            // separates the station calling from the station being called.
            var c = CwQsoFields.Callsigns("CQ CQ DE MM5AGM MM5AGM K");
            Assert.Equal("MM5AGM", Best(c));
            Assert.Contains("DE", c[0].Why);
        }

        [Fact]
        public void Both_ends_of_a_call_and_answer_are_offered()
        {
            var c = CwQsoFields.Callsigns("MM5AGM DE W1AW W1AW KN");
            Assert.Equal("W1AW", Best(c));
            Assert.Contains(c, x => x.Value == "MM5AGM");
        }

        [Fact]
        public void A_call_sent_three_times_outranks_one_sent_once()
        {
            var c = CwQsoFields.Callsigns("G4ABC K W1AW W1AW W1AW");
            Assert.Equal("W1AW", Best(c));
        }

        [Theory]
        [InlineData("2E0AAA")]
        [InlineData("9A1A")]
        [InlineData("W1AW")]
        [InlineData("MM5AGM")]
        [InlineData("K0ND")]
        [InlineData("VK6IS")]
        [InlineData("OZ1JTE")]
        public void Real_callsign_shapes_are_recognised(string call)
        {
            Assert.Equal(call, Best(CwQsoFields.Callsigns("DE " + call + " K")));
        }

        [Theory]
        [InlineData("UR RST 5NN 5NN")]      // a report, not a call
        [InlineData("QRZ QRZ")]             // a procedural signal
        [InlineData("TNX FER QSO 73")]      // no digits at all in a call position
        public void Things_shaped_like_calls_that_are_not_calls_are_rejected(string text)
        {
            Assert.Empty(CwQsoFields.Callsigns(text));
        }

        [Fact]
        public void A_portable_suffix_stays_with_the_call()
        {
            Assert.Equal("MM5AGM/P", Best(CwQsoFields.Callsigns("DE MM5AGM/P K")));
        }

        [Fact]
        public void Cut_numbers_in_a_report_are_expanded()
        {
            // The decoder transcribes what was keyed. 5NN is what an operator
            // sends when they mean 599, and the log wants the number.
            Assert.Equal("599", Best(CwQsoFields.SignalReports("UR RST 5NN 5NN BK")));
        }

        [Fact]
        public void A_report_sent_in_plain_digits_is_read_too()
        {
            Assert.Equal("579", Best(CwQsoFields.SignalReports("UR RST 579 579")));
        }

        [Fact]
        public void Cut_numbers_are_not_expanded_in_ordinary_words()
        {
            // This is the whole reason expansion is scoped to report-shaped
            // words. TNX and NAME contain cut letters beside ordinary ones.
            Assert.Equal("TNX", CwQsoFields.ExpandCutNumbers("TNX"));
            Assert.Equal("NAME", CwQsoFields.ExpandCutNumbers("NAME"));

            // ANT, TNT and NNN are made ENTIRELY of cut letters, so only the
            // requirement for a real digit saves them - and ANT is one of the
            // commonest words in a ragchew. Without it, ANT reads as 190.
            Assert.Equal("ANT", CwQsoFields.ExpandCutNumbers("ANT"));
            Assert.Equal("TNT", CwQsoFields.ExpandCutNumbers("TNT"));
            Assert.Equal("NNN", CwQsoFields.ExpandCutNumbers("NNN"));
        }

        [Fact]
        public void A_word_with_no_cut_letters_is_left_exactly_as_it_is()
        {
            Assert.Equal("599", CwQsoFields.ExpandCutNumbers("599"));
            Assert.Equal("2026", CwQsoFields.ExpandCutNumbers("2026"));
        }

        [Fact]
        public void Numbers_that_cannot_be_a_report_are_not_offered_as_one()
        {
            // Readability only runs to 5 and strength to 9, so 749 and 600
            // are a serial, an age or a temperature - never an RST.
            Assert.Empty(CwQsoFields.SignalReports("749"));
            Assert.Empty(CwQsoFields.SignalReports("600"));
        }

        [Fact]
        public void A_name_is_read_with_or_without_the_IS()
        {
            Assert.Equal("BOB", Best(CwQsoFields.Names("MY NAME IS BOB BOB")));
            Assert.Equal("ARMIN", Best(CwQsoFields.Names("NAME ARMIN")));
            Assert.Equal("DAN", Best(CwQsoFields.Names("OP DAN HR")));
        }

        [Fact]
        public void A_QTH_is_read()
        {
            Assert.Equal("GLASGOW", Best(CwQsoFields.Locations("QTH GLASGOW GLASGOW")));
        }

        [Fact]
        public void Nothing_is_invented_from_nothing()
        {
            foreach (var text in new[] { null, "", "   ", "?? ? ??" })
            {
                Assert.Empty(CwQsoFields.Callsigns(text));
                Assert.Empty(CwQsoFields.SignalReports(text));
                Assert.Empty(CwQsoFields.Names(text));
                Assert.Empty(CwQsoFields.Locations(text));
            }
        }

        [Fact]
        public void A_whole_exchange_yields_every_field()
        {
            // A ragchew opening as it actually arrives, undotted and unpunctuated,
            // with the decoder's own unknown-symbol marks left in.
            const string qso =
                "MM5AGM DE W1AW W1AW GE OM TNX FER CALL UR RST 5NN 5NN " +
                "NAME IS BOB BOB QTH BOSTON BOSTON ?? HW CPY BK";

            Assert.Equal("W1AW", Best(CwQsoFields.Callsigns(qso)));
            Assert.Equal("599", Best(CwQsoFields.SignalReports(qso)));
            Assert.Equal("BOB", Best(CwQsoFields.Names(qso)));
            Assert.Equal("BOSTON", Best(CwQsoFields.Locations(qso)));
        }

        [Fact]
        public void Every_candidate_carries_the_evidence_that_ranked_it()
        {
            // Section 4.11h measured confidence at 1.00 on 592 characters of
            // junk, so the operator has to be able to see why a suggestion was
            // made. A blank reason would defeat the point of suggesting at all.
            var all = CwQsoFields.Callsigns("CQ DE G4ABC G4ABC K")
                .Concat(CwQsoFields.SignalReports("RST 5NN"))
                .Concat(CwQsoFields.Names("NAME JIM"));

            Assert.All(all, c => Assert.False(string.IsNullOrWhiteSpace(c.Why)));
            Assert.All(all, c => Assert.True(c.Score > 0.0));
        }
    }
}
