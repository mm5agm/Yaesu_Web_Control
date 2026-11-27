using System;
using System.Linq;
using Xunit;
using Yaesu_Web_Control.Services.RadioMenu;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The FTDX10 menu table (CAT manual 2308-F), its front panel knobs, and
    /// the guard that stops an FTdx101 file loading into an FTDX10 menu, or the
    /// other way round, because the same EX address is a different item.
    /// </summary>
    public class Ftdx10MenuTests
    {
        private static RadioMenuItem Item(string address) =>
            Ftdx10Menu.Items.Single(i => i.Address == address);

        [Fact]
        public void The_FTDX10_has_its_own_menu_table()
            => Assert.Same(Ftdx10Menu.Items, RadioMenuCat.TableFor("FTdx10"));

        [Fact]
        public void Addresses_are_unique_and_six_digits()
        {
            Assert.Equal(Ftdx10Menu.Items.Count, Ftdx10Menu.Items.Select(i => i.Address).Distinct().Count());
            Assert.All(Ftdx10Menu.Items, i => Assert.Matches("^[0-9]{6}$", i.Address));
            Assert.InRange(Ftdx10Menu.Items.Count, 150, 230);
        }

        [Theory]
        [InlineData("010101", "AF TREBLE GAIN")]
        [InlineData("010320", "TONE FREQ")]
        [InlineData("020114", "CW WAVE SHAPE")]
        [InlineData("030115", "SPLIT FREQ DISPLAY")]
        [InlineData("030401", "HF MAX POWER")]
        public void Spot_checks_against_the_CAT_manual(string address, string function)
            => Assert.Equal(function, Item(address).Function);

        [Fact]
        public void The_same_address_is_a_different_item_on_the_FTdx101()
            => Assert.NotEqual(Item("010101").Function, Ftdx101Menu.Items.Single(i => i.Address == "010101").Function);

        [Fact]
        public void Tone_freq_has_the_50_CTCSS_tones()
        {
            Assert.Equal(50, Ftdx10Menu.CtcssTones.Length);
            Assert.Equal("67.0", Ftdx10Menu.CtcssTones[0]);
            Assert.Equal("254.1", Ftdx10Menu.CtcssTones[^1]);
            Assert.Equal(50, Item("010320").Values!.Count);
        }

        [Fact]
        public void Max_power_tops_out_at_100_watts()
        {
            Assert.Equal(100, Item("030401").Values!.Max());
            Assert.Equal(100, RadioKnobs.TableFor("FTdx10")!.Single(k => k.Command == "PC").Max);
            Assert.Equal(200, RadioKnobs.TableFor("FTdx101MP")!.Single(k => k.Command == "PC").Max);
        }

        [Fact]
        public void The_CAT_link_items_are_never_loadable()
        {
            var link = Ftdx10Menu.Items.Where(i => i.Link).Select(i => i.Address).ToArray();
            Assert.Equal(new[] { "030106", "030107", "030108", "030109", "030110" }, link);
            foreach (var a in link)
                Assert.Null(RadioMenuLoad.CheckRaw(Item(a), "0", "0", out _));
        }

        [Fact]
        public void Every_value_decodes_to_text_that_encodes_back_to_it()
        {
            foreach (var item in Ftdx10Menu.Items.Where(i => i.Values is not null && !i.Link))
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
        public void The_FTDX10_has_front_panel_knobs_and_modes()
        {
            Assert.NotNull(RadioKnobs.TableFor("FTdx10"));
            Assert.NotEmpty(RadioKnobs.ModesFor("FTdx10"));
            foreach (var knob in RadioKnobs.TableFor("FTdx10")!)
                Assert.DoesNotContain(knob.Command[..2], new[] { "TX", "MX", "AC", "KY", "KM", "BK", "VX" });
        }

        [Theory]
        [InlineData("FTdx101MP", "FTdx101MP", true)]
        [InlineData("FTdx101D", "FTdx101MP", true)]
        [InlineData("FTdx101MP", "FTdx101D", true)]
        [InlineData("FTdx10", "FTdx10", true)]
        [InlineData("ftdx10", "FTdx10", true)]
        [InlineData(null, "FTdx10", true)]
        [InlineData("", "FTdx101MP", true)]
        [InlineData("FTdx101MP", "FTdx10", false)]
        [InlineData("FTdx101D", "FTdx10", false)]
        [InlineData("FTdx10", "FTdx101MP", false)]
        [InlineData("FT-991A", "FTdx10", false)]
        public void Menu_lines_only_load_between_radios_with_the_same_menu(string? fileRadio, string model, bool same)
            => Assert.Equal(same, RadioMenuCat.SameMenu(fileRadio, model));
    }
}
