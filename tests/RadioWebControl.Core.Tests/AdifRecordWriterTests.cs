using System;
using System.Text;
using RadioWebControl.Core.Services;
using Xunit;

namespace RadioWebControl.Core.Tests
{
    /// <summary>
    /// ADIF is length-prefixed, so a wrong length is not a formatting blemish -
    /// it desynchronises the reader and every field after it is read from the
    /// wrong offset. These tests are mostly about the length.
    /// </summary>
    public class AdifRecordWriterTests
    {
        private static AdifRecordWriter.Qso Sample() => new()
        {
            Callsign     = "W1AW",
            WhenUtc      = new DateTime(2026, 9, 1, 14, 32, 10, DateTimeKind.Utc),
            FrequencyMhz = 14.058,
            RstSent      = "599",
            RstReceived  = "579",
        };

        [Fact]
        public void A_record_has_the_fields_a_logger_needs_and_ends_with_EOR()
        {
            string s = AdifRecordWriter.Write(Sample());

            Assert.Contains("<CALL:4>W1AW", s);
            Assert.Contains("<QSO_DATE:8>20260901", s);
            Assert.Contains("<TIME_ON:6>143210", s);
            Assert.Contains("<MODE:2>CW", s);
            Assert.Contains("<RST_SENT:3>599", s);
            Assert.Contains("<RST_RCVD:3>579", s);
            Assert.EndsWith("<EOR>\n", s);
        }

        [Fact]
        public void The_band_is_derived_from_the_frequency()
        {
            Assert.Contains("<BAND:3>20m", AdifRecordWriter.Write(Sample()));
        }

        [Theory]
        [InlineData(1.840, "160m")]
        [InlineData(3.573, "80m")]
        [InlineData(7.030, "40m")]
        [InlineData(10.118, "30m")]
        [InlineData(14.058, "20m")]
        [InlineData(18.080, "17m")]
        [InlineData(21.040, "15m")]
        [InlineData(24.906, "12m")]
        [InlineData(28.020, "10m")]
        [InlineData(50.090, "6m")]
        [InlineData(70.100, "4m")]     // Region 1 - the FTdx101MP has it
        [InlineData(144.050, "2m")]
        public void Every_band_the_radio_covers_maps(double mhz, string band)
        {
            Assert.Equal(band, AdifRecordWriter.BandFor(mhz));
        }

        [Fact]
        public void A_frequency_off_the_amateur_bands_yields_no_band_rather_than_a_wrong_one()
        {
            Assert.Null(AdifRecordWriter.BandFor(5.000));   // WWV
            Assert.Null(AdifRecordWriter.BandFor(0.198));   // long wave
            var s = AdifRecordWriter.Write(new AdifRecordWriter.Qso
            {
                Callsign = "W1AW", FrequencyMhz = 5.000,
                WhenUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            Assert.DoesNotContain("<BAND:", s);
        }

        [Fact]
        public void The_length_prefix_counts_bytes_not_characters()
        {
            // ADIF counts octets. A name with an accent in it is where a
            // string.Length count silently under-reports and every field
            // after this one is read from the wrong offset.
            var s = AdifRecordWriter.Write(new AdifRecordWriter.Qso
            {
                Callsign = "W1AW", Name = "José",   // e + combining acute
                WhenUtc  = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            });

            int i = s.IndexOf("<NAME:", StringComparison.Ordinal);
            Assert.True(i >= 0);
            int colon = s.IndexOf(':', i) + 1;
            int close = s.IndexOf('>', colon);
            int declared = int.Parse(s.Substring(colon, close - colon));
            Assert.Equal(Encoding.UTF8.GetByteCount("José"), declared);
            Assert.Equal(6, declared);                 // 5 characters, 6 bytes
            Assert.NotEqual("José".Length, declared);   // not characters
        }

        [Fact]
        public void A_newline_in_a_comment_cannot_end_the_record_early()
        {
            // The comment is the one field an operator types freely, and a
            // line-oriented reader treats a bare newline as the end of a
            // record. Whitespace runs collapse to single spaces.
            var s = AdifRecordWriter.Write(new AdifRecordWriter.Qso
            {
                Callsign = "W1AW", Comment = "good\r\nsignal\there",
                WhenUtc  = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            });

            Assert.Contains("<COMMENT:16>good signal here", s);
            Assert.Equal("\n", s.Substring(s.Length - 1));
            Assert.DoesNotContain("\n", s.Substring(0, s.Length - 1));
        }

        [Fact]
        public void The_frequency_is_written_in_the_invariant_culture()
        {
            // A European decimal comma here is not a cosmetic problem: FREQ is
            // parsed as a number, and 14,058 is not 14.058.
            var was = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                Assert.Contains("<FREQ:9>14.058000", AdifRecordWriter.Write(Sample()));
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = was; }
        }

        [Fact]
        public void A_record_with_no_callsign_is_refused()
        {
            // An ADIF record without a call is not a QSO, and a logger will
            // import it silently as a blank row.
            Assert.Throws<ArgumentException>(() =>
                AdifRecordWriter.Write(new AdifRecordWriter.Qso { Callsign = "  " }));
        }

        [Fact]
        public void Empty_fields_are_omitted_rather_than_written_blank()
        {
            string s = AdifRecordWriter.Write(Sample());
            Assert.DoesNotContain("<NAME:", s);
            Assert.DoesNotContain("<QTH:", s);
            Assert.DoesNotContain("<COMMENT:", s);
        }

        [Fact]
        public void The_header_identifies_the_program_and_ends_with_EOH()
        {
            string h = AdifRecordWriter.Header("Yaesu Web Control", "2.4.3");
            Assert.Contains("<ADIF_VER:5>3.1.4", h);
            Assert.Contains("<PROGRAMID:17>Yaesu Web Control", h);
            Assert.Contains("<PROGRAMVERSION:5>2.4.3", h);
            Assert.Contains("<EOH>", h);
        }

        [Fact]
        public void What_this_writes_is_what_the_parser_reads_back()
        {
            // The two halves are meant to be a pair, so the round trip is the
            // test that matters most: anything the writer gets wrong about
            // lengths or delimiters shows up here as a wrong value rather
            // than as a malformed-looking string a human has to eyeball.
            string adif = AdifRecordWriter.Header("Yaesu Web Control", "2.4.3")
                        + AdifRecordWriter.Write(Sample());

            var r = Assert.Single(AdifParser.Parse(adif));
            Assert.Equal("W1AW", r.Callsign);
            Assert.Equal("20m",  r.Band);
            Assert.Equal("CW",   r.Mode);
            Assert.Equal(14_058_000L, AdifParser.FreqMHzToHz(r.Frequency));
        }

        [Fact]
        public void Two_records_appended_to_one_file_read_back_as_two()
        {
            // Logging is append-only, so the failure that matters is the
            // second record, not the first - a missing separator or a length
            // one byte out swallows it into the first and nothing complains.
            var second = Sample();
            second.Callsign = "G4ABC";
            second.FrequencyMhz = 7.030;

            string adif = AdifRecordWriter.Header("Yaesu Web Control", "2.4.3")
                        + AdifRecordWriter.Write(Sample())
                        + AdifRecordWriter.Write(second);

            var rs = AdifParser.Parse(adif);
            Assert.Equal(2, rs.Count);
            Assert.Equal("W1AW",  rs[0].Callsign);
            Assert.Equal("G4ABC", rs[1].Callsign);
            Assert.Equal("40m",   rs[1].Band);
        }

        /// <summary>e with a combining acute - two code points, three UTF-8 bytes.</summary>
        private const string Accented = "José";
    }
}
