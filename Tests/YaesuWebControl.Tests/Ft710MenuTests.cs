using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Yaesu_Web_Control.Services.RadioMenu;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The FT-710 menu table (CAT manual 2306-C) and its front panel knobs,
    /// which are the FTdx101's at 100 W.
    /// </summary>
    public class Ft710MenuTests
    {
        private static RadioMenuItem Item(string address) =>
            Ft710Menu.Items.Single(i => i.Address == address);

        [Fact]
        public void The_FT710_has_its_own_menu_table()
            => Assert.Same(Ft710Menu.Items, RadioMenuCat.TableFor("FT-710"));

        [Fact]
        public void Addresses_are_unique_and_six_digits()
        {
            Assert.Equal(Ft710Menu.Items.Count, Ft710Menu.Items.Select(i => i.Address).Distinct().Count());
            Assert.All(Ft710Menu.Items, i => Assert.Matches("^[0-9]{6}$", i.Address));
            Assert.InRange(Ft710Menu.Items.Count, 250, 330);
        }

        [Theory]
        [InlineData("020115", "PC KEYING")]
        [InlineData("030401", "HF MAX POWER")]
        [InlineData("040101", "MY CALL")]
        [InlineData("060101", "PRESET NAME")]
        [InlineData("060501", "PRESET NAME")]
        public void Spot_checks_against_the_CAT_manual(string address, string function)
            => Assert.Equal(function, Item(address).Function);

        [Fact]
        public void Max_power_tops_out_at_100_watts()
        {
            Assert.Equal(100, Item("030401").Values!.Max());
            Assert.Equal(100, RadioKnobs.TableFor("FT-710")!.Single(k => k.Command == "PC").Max);
        }

        [Fact]
        public void The_CAT_link_items_are_never_loadable()
        {
            var link = Ft710Menu.Items.Where(i => i.Link).ToArray();
            Assert.Equal(7 + 5 * 3, link.Length);
            Assert.All(link, i => Assert.StartsWith("CAT-", i.Function));
            foreach (var i in link)
                Assert.Null(RadioMenuLoad.CheckRaw(i, "0", "0", out _));
        }

        [Fact]
        public void Every_value_decodes_to_text_that_encodes_back_to_it()
            => MenuTableChecks.EveryValueRoundTrips(Ft710Menu.Items);

        [Fact]
        public void The_FT710_has_the_FTdx101_knobs_and_modes()
        {
            var knobs = RadioKnobs.TableFor("FT-710")!;
            Assert.Equal(RadioKnobs.TableFor("FTdx10")!.Select(k => k.Command), knobs.Select(k => k.Command));
            Assert.Same(RadioKnobs.Ftdx101Modes, RadioKnobs.ModesFor("FT-710"));
            foreach (var knob in knobs)
                Assert.DoesNotContain(knob.Command[..2], new[] { "TX", "MX", "AC", "KY", "KM", "BK", "VX" });
        }

        [Theory]
        [InlineData("FT-710", "FT-710", true)]
        [InlineData("FT-710", "FTdx10", false)]
        [InlineData("FTdx10", "FT-710", false)]
        [InlineData("FTdx101MP", "FT-710", false)]
        [InlineData("FT-991A", "FT-710", false)]
        public void Menu_lines_only_load_between_radios_with_the_same_menu(string? fileRadio, string model, bool same)
            => Assert.Equal(same, RadioMenuCat.SameMenu(fileRadio, model));
    }

    internal static class MenuTableChecks
    {
        /// <summary>Every value an item takes, in its widest form, decodes to text that Load turns back into it.</summary>
        public static void EveryValueRoundTrips(IEnumerable<RadioMenuItem> items)
        {
            foreach (var item in items.Where(i => i.Values is not null && !i.Link))
            {
                bool signed = item.Values!.Min() < 0;
                int width = item.Values!.Max(v => Math.Abs(v).ToString().Length);
                foreach (var v in item.Values!)
                {
                    var raw = (signed ? (v < 0 ? "-" : "+") : "") + Math.Abs(v).ToString().PadLeft(width, '0');
                    Assert.True(raw == RadioMenuLoad.Encode(item, item.Decode(raw), null, raw, out _),
                        $"{item.Address} {item.Function}: {raw} shows as '{item.Decode(raw)}'");
                }
            }
        }
    }
}
