using Xunit;
using Yaesu_Web_Control.Services.RadioMenu;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The FTdx101 menu save: the table transcribed from the CAT manual's
    /// Table 2, the read-only EX traffic, and the save file.
    /// </summary>
    public class RadioMenuTests
    {
        private static RadioMenuItem Item(string address) =>
            Ftdx101Menu.Items.Single(i => i.Address == address);

        [Fact]
        public void Table_has_every_item_of_the_manual_once()
        {
            // 14+15+16+16+14+5+6 radio, 17+13+1 CW, 23+5+20+7+7 operation, 8+4+2 display.
            Assert.Equal(193, Ftdx101Menu.Items.Count);
            Assert.Equal(Ftdx101Menu.Items.Count, Ftdx101Menu.Items.Select(i => i.Address).Distinct().Count());
        }

        [Theory]
        [InlineData("010306", "MODE FM", "HCUT FREQ")]
        [InlineData("010410", "MODE PSK/DATA", "DATA OUT SELECT")]
        [InlineData("020112", "MODE CW", "CW BK-IN DELAY")]
        [InlineData("030111", "GENERAL", "CAT RATE")]
        [InlineData("030401", "TX GNRL", "HF MAX POWER")]
        [InlineData("030320", "TX AUDIO", "P PRMTRC EQ3 BWTH")]
        [InlineData("040202", "SCOPE", "SCOPE CTR")]
        public void Addresses_match_the_manual(string address, string group, string function)
        {
            var item = Item(address);
            Assert.Equal(group, item.Group);
            Assert.Equal(function, item.Function);
        }

        [Theory]
        [InlineData("010101", "0160", "160 ms")]
        [InlineData("010104", "01", "100 Hz")]
        [InlineData("010104", "00", "OFF")]
        [InlineData("010106", "67", "4000 Hz")]
        [InlineData("010110", "0", "50-3050")]
        [InlineData("010212", "1000", "MCVR")]
        [InlineData("010212", "0050", "FIX 50")]
        [InlineData("020112", "04", "200 ms")]
        [InlineData("020112", "07", "400 ms")]
        [InlineData("020112", "33", "3000 ms")]
        [InlineData("020113", "1", "6 ms")]   // read on the radio
        [InlineData("020205", "30", "3.0")]
        [InlineData("030111", "3", "38400 bps")]
        [InlineData("030117", "+05", "+5 kHz")]
        [InlineData("030118", "00", "OFF")]
        [InlineData("030118", "10", "10 min")]
        [InlineData("030202", "-15", "-15")]
        [InlineData("030304", "+00", "0 dB")]
        [InlineData("030309", "18", "3200 Hz")]
        [InlineData("030401", "200", "200 W")]
        [InlineData("040101", "MM5AGM      ", "MM5AGM")]
        [InlineData("010110", "9", "9")]    // off the end of the list: shown as sent
        public void Values_decode_as_the_radio_shows_them(string address, string raw, string expected)
            => Assert.Equal(expected, Item(address).Decode(raw));

        [Fact]
        public void Read_command_is_a_read_and_nothing_else()
        {
            Assert.Equal("EX030111;", RadioMenuCat.ReadCommand("030111"));
            Assert.Throws<ArgumentException>(() => RadioMenuCat.ReadCommand("0301113"));
            Assert.Throws<ArgumentException>(() => RadioMenuCat.ReadCommand("03011;"));
        }

        [Fact]
        public void Every_table_address_makes_a_read_command()
        {
            foreach (var item in Ftdx101Menu.Items)
                Assert.Matches(@"^EX\d{6};$", RadioMenuCat.ReadCommand(item.Address));
        }

        [Theory]
        [InlineData("010306", "EX01030667;", "67")]
        [InlineData("010306", "EX01030667", "67")]
        [InlineData("010306", "EX01030567;", null)]  // another item's answer
        [InlineData("010306", "FA014074000;", null)]
        [InlineData("010306", "EX010306;", null)]
        [InlineData("010306", "", null)]
        [InlineData("010306", null, null)]
        public void Answer_is_only_taken_for_its_own_address(string address, string? answer, string? expected)
            => Assert.Equal(expected, RadioMenuCat.ParseAnswer(address, answer));

        [Theory]
        [InlineData("FTdx101MP", true)]
        [InlineData("FTdx101D", true)]
        [InlineData("FT-710", false)]
        [InlineData("FTdx10", false)]
        [InlineData("FTDX3000", false)]
        public void Only_the_FTdx101_has_a_table_so_far(string model, bool has)
            => Assert.Equal(has, RadioMenuCat.TableFor(model) is not null);

        [Fact]
        public void Ini_groups_items_and_keeps_raw_values()
        {
            var ini = RadioMenuCat.ToIni("FTdx101MP", new DateTime(2026, 10, 25, 13, 5, 0), new[]
            {
                new RadioMenuReading(Item("010101"), "0160"),
                new RadioMenuReading(Item("010303"), "1500"),
                new RadioMenuReading(Item("030401"), null),
            }).Split(Environment.NewLine);

            Assert.Contains("[File]", ini);
            Assert.Contains("Radio = FTdx101MP", ini);
            Assert.Contains("Saved = 2026-10-25 13:05", ini);
            Assert.Contains("[RADIO SETTING > MODE SSB]", ini);
            Assert.Contains("AGC FAST DELAY = 160 ms".PadRight(40) + " ; 010101 = 0160", ini);
            Assert.Contains("[RADIO SETTING > MODE FM]", ini);
            Assert.Contains("[OPERATION SETTING > TX GNRL]", ini);
            Assert.Contains("HF MAX POWER = (no answer)".PadRight(40) + " ; 030401  [TX]", ini);
            Assert.Single(ini, l => l == "[RADIO SETTING > MODE SSB]");
        }
    }
}
