using Xunit;
using Yaesu_Web_Control.Services.RadioMenu;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// Closing YWC asks to save the radio menu when a front-panel level changed:
    /// which level reports count as the user turning a knob (not the radio
    /// settling after a mode change, a connect, or YWC's own save/load pass),
    /// and what the close check decides against the newest file.
    /// </summary>
    public class RadioMenuSessionTests
    {
        private static readonly DateTime T0 = new(2026, 11, 27, 18, 0, 0);
        private static DateTime At(double seconds) => T0.AddSeconds(seconds);

        private static RadioKnob Knob(string command) => RadioKnobs.TableFor("FTdx101MP")!.Single(k => k.Command == command);
        private static RadioKnobMode Mode(string name) => RadioKnobs.Ftdx101Modes.Single(m => m.Name == name);

        private static string FileText() => RadioMenuCat.ToIni("FTdx101MP", T0, new[]
        {
            new RadioMenuReading(Ftdx101Menu.Items.Single(i => i.Address == "010101"), "0160"),
        }) + RadioKnobs.ToIni(new[]
        {
            new RadioKnobReading(Mode("USB"), Knob("MG"), 50),
            new RadioKnobReading(Mode("DATA-U"), Knob("PC"), 25),
        });

        private static RadioMenuSaveInfo Saved(int daysAgo) => new("file.ini", T0.AddDays(-daysAgo), FileText());

        private static readonly Dictionary<string, int?> NoLive = new();

        /// <summary>A session in USB, past the settle time, with MIC GAIN first seen at 50.</summary>
        private static RadioMenuSessionChanges InUsb()
        {
            var c = new RadioMenuSessionChanges();
            c.OnMode("2", At(0));
            c.OnLevel("MG", 50, At(5));
            return c;
        }

        [Fact]
        public void The_first_value_seen_is_not_a_change()
        {
            Assert.Empty(InUsb().Touched());
        }

        [Fact]
        public void Turning_a_knob_is_a_change_in_that_mode()
        {
            var c = InUsb();
            c.OnLevel("MG", 20, At(10));
            var t = Assert.Single(c.Touched());
            Assert.Equal(("2", "MG", 20), t);
        }

        [Fact]
        public void Levels_just_after_a_mode_change_are_the_radio_reporting()
        {
            var c = InUsb();
            c.OnMode("C", At(10));
            c.OnLevel("MG", 30, At(10.5));
            c.OnLevel("MG", 31, At(11));
            Assert.Empty(c.Touched());
            c.OnLevel("MG", 35, At(13));
            Assert.Equal(("C", "MG", 35), Assert.Single(c.Touched()));
        }

        [Fact]
        public void Levels_during_YWCs_own_pass_are_not_changes()
        {
            var c = InUsb();
            c.PassStarted();
            c.OnLevel("MG", 0, At(20));
            c.OnLevel("MG", 50, At(40));
            c.PassEnded(At(41));
            c.OnLevel("MG", 60, At(42));
            Assert.Empty(c.Touched());
            c.OnLevel("MG", 70, At(44));
            Assert.Single(c.Touched());
        }

        [Fact]
        public void Connecting_settles_for_a_while()
        {
            var c = InUsb();
            c.Settle(At(10) + RadioMenuSessionChanges.ConnectSettle);
            c.OnLevel("MG", 10, At(15));
            Assert.Empty(c.Touched());
        }

        [Fact]
        public void Saving_clears_the_changes_but_keeps_the_values()
        {
            var c = InUsb();
            c.OnLevel("MG", 20, At(10));
            c.Saved();
            Assert.Empty(c.Touched());
            c.OnLevel("MG", 20, At(12));
            Assert.Empty(c.Touched());
            c.OnLevel("MG", 25, At(14));
            Assert.Single(c.Touched());
        }

        [Fact]
        public void No_mode_known_means_nothing_is_recorded()
        {
            var c = new RadioMenuSessionChanges();
            c.OnLevel("MG", 1, At(10));
            c.OnLevel("MG", 2, At(20));
            Assert.Empty(c.Touched());
        }

        [Fact]
        public void Close_does_not_ask_when_nothing_differs()
        {
            var (ask, _, _) = RadioMenuReminder.ExitCheck(Saved(2), T0, "FTdx101MP",
                new[] { ("2", "MG", 50) }, "C", new Dictionary<string, int?> { ["PC"] = 25 });
            Assert.False(ask);
        }

        [Fact]
        public void Close_asks_for_a_level_changed_in_another_mode()
        {
            var (ask, message, changes) = RadioMenuReminder.ExitCheck(Saved(2), T0, "FTdx101MP",
                new[] { ("2", "MG", 10) }, "C", new Dictionary<string, int?> { ["PC"] = 25 });
            Assert.True(ask);
            var ch = Assert.Single(changes);
            Assert.Equal("USB", ch.Mode);
            Assert.Contains("2 days ago", message);
            Assert.Contains("MIC GAIN changed from 50 to 10 in USB", message);
        }

        [Fact]
        public void Close_reads_the_current_mode_now_and_that_wins()
        {
            // Turned to 10 earlier, back to 50 now: not a change.
            var (ask, _, _) = RadioMenuReminder.ExitCheck(Saved(2), T0, "FTdx101MP",
                new[] { ("2", "MG", 10) }, "2", new Dictionary<string, int?> { ["MG"] = 50 });
            Assert.False(ask);

            // Changed in the current mode without the session seeing it (before YWC started).
            var (ask2, _, changes2) = RadioMenuReminder.ExitCheck(Saved(2), T0, "FTdx101MP",
                Array.Empty<(string, string, int)>(), "C", new Dictionary<string, int?> { ["PC"] = 50 });
            Assert.True(ask2);
            Assert.Equal("DATA-U", Assert.Single(changes2).Mode);
        }

        [Fact]
        public void Close_with_no_file_asks_only_if_a_level_was_changed()
        {
            Assert.False(RadioMenuReminder.ExitCheck(null, T0, "FTdx101MP",
                Array.Empty<(string, string, int)>(), "2", NoLive).Ask);
            var (ask, message, _) = RadioMenuReminder.ExitCheck(null, T0, "FTdx101MP",
                new[] { ("2", "MG", 10) }, "2", NoLive);
            Assert.True(ask);
            Assert.Contains("MIC GAIN in USB", message);
            Assert.Contains("never been saved", message);
        }

        [Fact]
        public void Close_never_asks_for_a_radio_without_levels()
        {
            Assert.False(RadioMenuReminder.ExitCheck(Saved(2), T0, "FT-991A",
                new[] { ("2", "MG", 10) }, "2", NoLive).Ask);
        }
    }
}
