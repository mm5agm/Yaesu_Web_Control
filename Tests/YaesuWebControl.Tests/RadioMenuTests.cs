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
        [InlineData("040102", "01", "1 s")]    // read on the radio
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

        // ── Load

        private static string? Encode(string address, string text, string current, string? saved = null) =>
            RadioMenuLoad.Encode(Item(address), text, saved, current, out _);

        [Theory]
        [InlineData("030312", "100 Hz", "00", "01")]      // P PRMTRC EQ1 FREQ
        [InlineData("030312", "100", "00", "01")]         // unit left off
        [InlineData("030312", "off", "05", "00")]
        [InlineData("010101", "200 ms", "0160", "0200")]  // width from the radio's answer
        [InlineData("010101", "200MS", "0160", "0200")]
        [InlineData("030117", "-3 kHz", "+05", "-03")]    // signed like the answer
        [InlineData("030117", "3", "+05", "+03")]
        [InlineData("030117", "0", "+05", "+00")]
        [InlineData("030304", "+5 dB", "+00", "+05")]
        [InlineData("010212", "MCVR", "0050", "1000")]
        [InlineData("010212", "fix 20", "1000", "0020")]
        [InlineData("020113", "8 ms", "1", "2")]
        [InlineData("040102", "OFF", "01", "00")]
        [InlineData("020205", "3.5", "30", "35")]
        [InlineData("020112", "400 ms", "04", "07")]
        [InlineData("030401", "100 W", "200", "100")]
        [InlineData("030403", "40", "050", "040")]
        [InlineData("040101", " mm5agm ", "MM5AGM", "MM5AGM      ")]
        [InlineData("010110", "300-2700", "0", "3")]
        public void Values_encode_back_to_what_EX_sends(string address, string text, string current, string expected)
            => Assert.Equal(expected, Encode(address, text, current));

        [Theory]
        [InlineData("030312", "ON", "00")]          // not a choice: OFF or 100-700 Hz
        [InlineData("030312", "800 Hz", "00")]      // off the end
        [InlineData("010101", "170 ms", "0160")]    // between steps
        [InlineData("030401", "250 W", "200")]      // over the MP's 200 W
        [InlineData("030401", "4 W", "200")]
        [InlineData("030117", "21", "+05")]
        [InlineData("040101", "ABCDEFGHIJKLM", "MM5AGM")]  // 13 characters
        [InlineData("040101", "MM5;AGM", "MM5AGM")]
        [InlineData("040101", "", "MM5AGM")]
        [InlineData("030111", "38400 bps", "3")]    // CAT RATE: never written
        public void Values_the_radio_does_not_offer_are_refused(string address, string text, string current)
        {
            Assert.Null(RadioMenuLoad.Encode(Item(address), text, null, current, out var error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void Error_lists_what_the_item_takes()
        {
            RadioMenuLoad.Encode(Item("030312"), "ON", null, "00", out var error);
            Assert.Contains("OFF, 100 Hz", error);
            Assert.Contains("700 Hz", error);
        }

        [Fact]
        public void The_D_is_held_to_100_W()
        {
            var hf = Ftdx101Menu.ItemsD.Single(i => i.Address == "030401");
            Assert.Null(RadioMenuLoad.Encode(hf, "200 W", null, "100", out _));
            Assert.Equal("050", RadioMenuLoad.Encode(hf, "50 W", null, "100", out _));
            Assert.Same(Ftdx101Menu.ItemsD, RadioMenuCat.TableFor("FTdx101D"));
        }

        [Fact]
        public void An_unedited_line_goes_back_as_saved()
            => Assert.Equal("+00", Encode("030121", "0", "+07", saved: "+00"));

        [Fact]
        public void An_edited_line_wins_over_the_saved_value()
            => Assert.Equal("01", Encode("030312", "100 Hz", "00", saved: "00"));

        [Fact]
        public void Every_value_decodes_to_text_that_encodes_back_to_it()
        {
            // So no two values of an item show the same, and a saved file
            // always loads back to exactly what was read.
            foreach (var table in new[] { Ftdx101Menu.Items, Ftdx101Menu.ItemsD })
                foreach (var item in table.Where(i => i.Values is not null && !i.Link))
                {
                    bool signed = item.Values!.Min() < 0;
                    int width = item.Values!.Max(v => Math.Abs(v).ToString().Length);
                    foreach (var v in item.Values!)
                    {
                        var raw = (signed ? (v < 0 ? "-" : "+") : "") + Math.Abs(v).ToString().PadLeft(width, '0');
                        Assert.Equal(raw, RadioMenuLoad.Encode(item, item.Decode(raw), null, raw, out _));
                    }
                }
        }

        [Fact]
        public void The_CAT_link_items_are_never_loadable()
        {
            var link = Ftdx101Menu.Items.Where(i => i.Link).Select(i => i.Address).ToArray();
            Assert.Equal(new[] { "030109", "030110", "030111", "030112", "030113" }, link);
            foreach (var a in link)
                Assert.Null(RadioMenuLoad.CheckRaw(Item(a), "0", "0", out _));
        }

        [Theory]
        [InlineData("030312", "01", "00", "01")]
        [InlineData("030312", "08", "00", null)]    // not a choice
        [InlineData("030312", "1", "00", null)]     // wrong width
        [InlineData("030117", "03", "+05", null)]   // unsigned where the radio signs
        [InlineData("030117", "+03", "+05", "+03")]
        [InlineData("030401", "201", "200", null)]
        [InlineData("030401", "1e2", "200", null)]
        [InlineData("040101", "mm5agm/p", "MM5AGM", "MM5AGM/P    ")]
        public void Values_from_the_page_are_checked_again(string address, string raw, string current, string? expected)
            => Assert.Equal(expected, RadioMenuLoad.CheckRaw(Item(address), raw, current, out _));

        [Fact]
        public void Set_command_adds_the_value_and_refuses_a_second_command()
        {
            Assert.Equal("EX03031201;", RadioMenuCat.SetCommand("030312", "01"));
            Assert.Equal("EX040101MM5AGM;", RadioMenuCat.SetCommand("040101", "MM5AGM"));
            Assert.Throws<ArgumentException>(() => RadioMenuCat.SetCommand("030312", "01;TX1"));
            Assert.Throws<ArgumentException>(() => RadioMenuCat.SetCommand("030312", ""));
            Assert.Throws<ArgumentException>(() => RadioMenuCat.SetCommand("03031", "01"));
        }

        [Fact]
        public void Saved_file_parses_back()
        {
            var ini = RadioMenuCat.ToIni("FTdx101MP", new DateTime(2026, 10, 25, 13, 5, 0), new[]
            {
                new RadioMenuReading(Item("010101"), "0160"),
                new RadioMenuReading(Item("030304"), "+05"),
                new RadioMenuReading(Item("040101"), "MM5AGM"),
                new RadioMenuReading(Item("030401"), null),
            });
            var file = RadioMenuLoad.Parse("﻿" + ini.Replace("\n", "\r\n"));

            Assert.Equal("YWC", file.Program);
            Assert.Equal("FTdx101MP", file.Radio);
            Assert.Equal(4, file.Items.Count);
            Assert.Equal(new RadioMenuFileItem(file.Items[0].Line, "RADIO SETTING > MODE SSB", "AGC FAST DELAY", "160 ms", "010101", "0160"), file.Items[0]);
            Assert.Equal(("+5 dB", "030304", "+05"), (file.Items[1].Value, file.Items[1].Address, file.Items[1].SavedRaw));
            Assert.Equal("MM5AGM", file.Items[2].SavedRaw);
            Assert.Equal((RadioMenuLoad.NoAnswer, "030401", (string?)null), (file.Items[3].Value, file.Items[3].Address, file.Items[3].SavedRaw));
        }

        [Fact]
        public void Choices_follow_the_radios_width_and_sign()
        {
            var eq = RadioMenuLoad.Choices(Item("030312"), "00")!;
            Assert.Equal("00", eq[0]);
            Assert.All(eq, c => Assert.Equal(2, c.Length));

            var level = RadioMenuLoad.Choices(Item("030304"), "+05")!;
            Assert.Contains("+00", level);
            Assert.Contains("+05", level);
            Assert.All(level, c => Assert.True(c[0] is '+' or '-'));

            Assert.Null(RadioMenuLoad.Choices(Item("040101"), "MM5AGM"));
        }

        [Fact]
        public void A_line_without_its_comment_is_found_by_section_and_name()
        {
            var file = RadioMenuLoad.Parse("[OPERATION SETTING > TX AUDIO]\nP PRMTRC EQ1 FREQ = 100 Hz\n[NOWHERE]\nP PRMTRC EQ1 FREQ = 100 Hz\n");
            Assert.Equal("030312", RadioMenuLoad.Find(Ftdx101Menu.Items, file.Items[0])?.Address);
            Assert.Null(RadioMenuLoad.Find(Ftdx101Menu.Items, file.Items[1]));
        }
    }
}
