using Xunit;
using Yaesu_Web_Control.Services.RadioMenu;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The reminder to save the radio menu: none yet, a month old, a level
    /// changed in the current mode, or an older file without the levels.
    /// Also that an older file still loads as before.
    /// </summary>
    public class RadioMenuReminderTests
    {
        private static readonly DateTime Now = new(2026, 11, 27, 18, 0, 0);

        private static RadioKnob Knob(string command) => RadioKnobs.TableFor("FTdx101MP")!.Single(k => k.Command == command);
        private static RadioKnobMode Mode(string name) => RadioKnobs.Ftdx101Modes.Single(m => m.Name == name);

        private static string MenuOnly() => RadioMenuCat.ToIni("FTdx101MP", Now, new[]
        {
            new RadioMenuReading(Ftdx101Menu.Items.Single(i => i.Address == "010101"), "0160"),
        });

        private static string WithKnobs() => MenuOnly() + RadioKnobs.ToIni(new[]
        {
            new RadioKnobReading(Mode("USB"), Knob("MG"), 50),
            new RadioKnobReading(Mode("USB"), Knob("AO"), 50),
            new RadioKnobReading(Mode("DATA-U"), Knob("MG"), 30),
            new RadioKnobReading(Mode("DATA-U"), Knob("PC"), 25),
        });

        private static RadioMenuSaveInfo Save(string text, int daysAgo) => new("file.ini", Now.AddDays(-daysAgo), text);

        private static readonly Dictionary<string, int?> NoLive = new();

        [Fact]
        public void No_file_asks_to_save()
        {
            var r = RadioMenuReminder.Check(null, Now, "FTdx101MP", null, NoLive);
            Assert.Equal("none", r.Kind);
            Assert.Contains("not been saved", RadioMenuReminder.Message(r));
        }

        [Fact]
        public void A_recent_file_with_the_same_levels_says_nothing()
        {
            var r = RadioMenuReminder.Check(Save(WithKnobs(), 3), Now, "FTdx101MP", "C",
                new Dictionary<string, int?> { ["MG"] = 30, ["PC"] = 25, ["AO"] = 1 });
            Assert.Equal("ok", r.Kind);
            Assert.Equal("", RadioMenuReminder.Message(r));
        }

        [Fact]
        public void A_month_old_file_is_old()
        {
            Assert.Equal("ok", RadioMenuReminder.Check(Save(WithKnobs(), 29), Now, "FTdx101MP", null, NoLive).Kind);
            var r = RadioMenuReminder.Check(Save(WithKnobs(), 30), Now, "FTdx101MP", null, NoLive);
            Assert.Equal("old", r.Kind);
            Assert.Contains("30 days ago", RadioMenuReminder.Message(r));
        }

        [Fact]
        public void A_level_changed_in_the_current_mode_is_named()
        {
            // USB in the file: MIC GAIN 50, AMC 50. Now MIC GAIN 0; AMC the same.
            var r = RadioMenuReminder.Check(Save(WithKnobs(), 2), Now, "FTdx101MP", "2",
                new Dictionary<string, int?> { ["MG"] = 0, ["AO"] = 50, ["PC"] = 100 });
            Assert.Equal("changed", r.Kind);
            var change = Assert.Single(r.Changes);
            Assert.Equal(("MIC GAIN", "0", "50"), (change.Name, change.Now, change.Saved));
            Assert.Equal("Since your last save (2 days ago), MIC GAIN changed from 50 to 0 in USB.", RadioMenuReminder.Message(r));
        }

        [Fact]
        public void Changed_wins_over_old()
        {
            var r = RadioMenuReminder.Check(Save(WithKnobs(), 90), Now, "FTdx101MP", "C",
                new Dictionary<string, int?> { ["PC"] = 50 });
            Assert.Equal("changed", r.Kind);
            Assert.Contains("RF POWER", RadioMenuReminder.Message(r).ToUpperInvariant());
        }

        [Fact]
        public void A_level_the_radio_did_not_answer_is_not_a_change()
        {
            var r = RadioMenuReminder.Check(Save(WithKnobs(), 2), Now, "FTdx101MP", "2",
                new Dictionary<string, int?> { ["MG"] = null });
            Assert.Equal("ok", r.Kind);
        }

        [Fact]
        public void A_mode_the_file_has_no_section_for_compares_nothing()
        {
            var r = RadioMenuReminder.Check(Save(WithKnobs(), 2), Now, "FTdx101MP", "5",
                new Dictionary<string, int?> { ["MG"] = 99 });
            Assert.Equal("ok", r.Kind);
        }

        [Fact]
        public void A_value_edited_by_hand_is_what_is_compared()
        {
            // The first MIC GAIN line is the USB one (50).
            var text = new System.Text.RegularExpressions.Regex(@"(MIC GAIN\s*=\s*)50\b").Replace(WithKnobs(), "${1}20", 1);
            Assert.NotEqual(WithKnobs(), text);
            var r = RadioMenuReminder.Check(Save(text, 2), Now, "FTdx101MP", "2",
                new Dictionary<string, int?> { ["MG"] = 50 });
            Assert.Equal("changed", r.Kind);
            Assert.Equal("20", r.Changes[0].Saved);
        }

        [Fact]
        public void An_older_file_without_levels_asks_for_a_new_save()
        {
            var r = RadioMenuReminder.Check(Save(MenuOnly(), 2), Now, "FTdx101MP", "2",
                new Dictionary<string, int?> { ["MG"] = 0 });
            Assert.Equal("noknobs", r.Kind);
            Assert.Contains("Save again", RadioMenuReminder.Message(r));
        }

        [Fact]
        public void An_older_file_still_parses_as_menu_items_only()
        {
            var file = RadioMenuLoad.Parse("﻿" + MenuOnly().Replace("\n", "\r\n"));
            var item = Assert.Single(file.Items);
            Assert.Equal("010101", item.Address);
            Assert.DoesNotContain(file.Items, i => RadioKnobs.IsKnobAddress(i.Address));
        }
    }
}
