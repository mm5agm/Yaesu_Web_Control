using System;
using System.Linq;
using Xunit;
using Yaesu_Web_Control.Services.RadioMenu;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The FT-991A menu table (FT-991 CAT Operation Reference Book), with its
    /// three-digit EX addresses, and its own front panel knobs and modes.
    /// </summary>
    public class Ft991aMenuTests
    {
        private static RadioMenuItem Item(string address) =>
            Ft991aMenu.Items.Single(i => i.Address == address);

        [Fact]
        public void The_FT991A_has_its_own_menu_table()
            => Assert.Same(Ft991aMenu.Items, RadioMenuCat.TableFor("FT-991A"));

        [Fact]
        public void Addresses_are_three_digits_001_to_153_without_radio_id()
        {
            Assert.All(Ft991aMenu.Items, i => Assert.Matches("^[0-9]{3}$", i.Address));
            Assert.Equal(Enumerable.Range(1, 153).Where(n => n != 87), Ft991aMenu.Items.Select(i => int.Parse(i.Address)));
        }

        [Theory]
        [InlineData("001", "AGC FAST DELAY")]
        [InlineData("057", "CW BK-IN DELAY")]
        [InlineData("060", "PC KEYING")]
        [InlineData("100", "RTTY SHIFT FREQ")]
        [InlineData("137", "HF TX MAX POWER")]
        [InlineData("153", "WIRES DG-ID")]
        public void Spot_checks_against_the_CAT_manual(string address, string function)
            => Assert.Equal(function, Item(address).Function);

        [Theory]
        [InlineData("057", "0300", "300 ms")]
        [InlineData("064", "-1500", "-1500 Hz")]
        [InlineData("072", "1", "DATA")]
        [InlineData("072", "2", "USB")]
        [InlineData("116", "05", "200 kHz")]
        [InlineData("028", "3", "RS232C")]
        [InlineData("102", "00", "OFF")]
        [InlineData("102", "03", "200 Hz")]
        [InlineData("151", "14500000", "14.500000 MHz")]
        [InlineData("153", "00", "AUTO")]
        public void Values_decode_as_the_manual_gives_them(string address, string raw, string expected)
            => Assert.Equal(expected, Item(address).Decode(raw));

        [Fact]
        public void Read_and_set_use_the_three_digit_number()
        {
            Assert.Equal("EX057;", RadioMenuCat.ReadCommand("057"));
            Assert.Equal("EX0570300;", RadioMenuCat.SetCommand("057", "0300"));
            Assert.Equal("0300", RadioMenuCat.ParseAnswer("057", "EX0570300;"));
            Assert.Null(RadioMenuCat.ParseAnswer("057", "EX0580300;"));
            foreach (var item in Ft991aMenu.Items)
                Assert.Matches(@"^EX\d{3};$", RadioMenuCat.ReadCommand(item.Address));
        }

        [Fact]
        public void A_saved_file_reads_back_with_its_addresses()
        {
            var ini = RadioMenuCat.ToIni("FT-991A", new DateTime(2026, 11, 28, 10, 0, 0), new[]
            {
                new RadioMenuReading(Item("057"), "0300"),
                new RadioMenuReading(Item("137"), "100"),
            });
            var file = RadioMenuLoad.Parse(ini);
            Assert.Equal("FT-991A", file.Radio);
            Assert.Equal(("057", "0300", "300 ms"), (file.Items[0].Address, file.Items[0].SavedRaw, file.Items[0].Value));
            Assert.Equal(("137", "100"), (file.Items[1].Address, file.Items[1].SavedRaw));
            Assert.Same(Item("137"), RadioMenuLoad.Find(Ft991aMenu.Items, file.Items[1]));
        }

        [Fact]
        public void The_CAT_link_items_are_never_loadable()
        {
            var link = Ft991aMenu.Items.Where(i => i.Link).Select(i => i.Address).ToArray();
            Assert.Equal(new[] { "028", "029", "030", "031", "032", "033" }, link);
            foreach (var a in link)
                Assert.Null(RadioMenuLoad.CheckRaw(Item(a), "0", "0", out _));
        }

        [Fact]
        public void Time_zone_and_preset_frequency_are_saved_but_not_loaded()
        {
            Assert.Null(RadioMenuLoad.Encode(Item("027"), "+0900", "+0900", "+0900", out var e1));
            Assert.NotNull(e1);
            Assert.Null(RadioMenuLoad.Encode(Item("151"), "14.500000 MHz", null, "14500000", out var e2));
            Assert.NotNull(e2);
        }

        [Fact]
        public void Every_value_decodes_to_text_that_encodes_back_to_it()
            => MenuTableChecks.EveryValueRoundTrips(Ft991aMenu.Items);

        [Fact]
        public void Power_is_100_watts_on_HF_and_6m_and_50_on_2m_and_70cm()
        {
            Assert.Equal(100, Item("137").Values!.Max());
            Assert.Equal(100, Item("138").Values!.Max());
            Assert.Equal(50, Item("139").Values!.Max());
            Assert.Equal(50, Item("140").Values!.Max());
            Assert.True(Item("137").Tx);
        }

        [Fact]
        public void The_FT991A_has_its_own_knobs_and_modes()
        {
            var knobs = RadioKnobs.TableFor("FT-991A")!;
            var commands = knobs.Select(k => k.Command).ToArray();
            Assert.Contains("SH0", commands);
            Assert.DoesNotContain("AO", commands);
            Assert.DoesNotContain("AV", commands);
            Assert.DoesNotContain("SD", commands);
            var att = knobs.Single(k => k.Command == "RA0");
            Assert.Equal(new[] { "OFF", "ON" }, new[] { att.Show(0), att.Show(1) });
            var width = knobs.Single(k => k.Command == "SH0");
            Assert.Equal(21, width.Max);
            Assert.Equal("SH0;", RadioKnobs.ReadCommand(width));
            foreach (var knob in knobs)
                Assert.DoesNotContain(knob.Command[..2], new[] { "TX", "MX", "AC", "KY", "KM", "BK", "VX" });

            var modes = RadioKnobs.ModesFor("FT-991A");
            Assert.Same(RadioKnobs.Ft991aModes, modes);
            Assert.DoesNotContain(modes, m => m.Name == "PSK" || m.Code == "E");
            Assert.Equal("C", RadioKnobs.ModeForSection("Front panel > DATA-U", "FT-991A")!.Code);
            Assert.Null(RadioKnobs.ModeForSection("Front panel > PSK", "FT-991A"));
        }

        [Theory]
        [InlineData(145_500_000L, "2m")]
        [InlineData(433_500_000L, "70cm")]
        [InlineData(118_000_000L, "GEN")]
        public void VHF_and_UHF_have_their_own_band(long hz, string band)
            => Assert.Equal(band, RadioKnobs.BandOf(hz));

        [Theory]
        [InlineData("FT-991A", "FT-991A", true)]
        [InlineData("FT-991A", "FT-710", false)]
        [InlineData("FTdx101D", "FT-991A", false)]
        public void Menu_lines_only_load_between_radios_with_the_same_menu(string? fileRadio, string model, bool same)
            => Assert.Equal(same, RadioMenuCat.SameMenu(fileRadio, model));
    }
}
