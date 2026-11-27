using Xunit;
using Yaesu_Web_Control.Services.RadioMenu;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The front-panel levels saved with the radio menu (MIC GAIN, AMC, PROC and
    /// so on): the CAT frames, the file section, and the pass that switches the
    /// main band through the modes and puts the mode back, never while the
    /// radio transmits.
    /// </summary>
    public class RadioKnobsTests
    {
        private static RadioKnob Knob(string command, string model = "FTdx101MP") =>
            RadioKnobs.TableFor(model)!.Single(k => k.Command == command);

        private static RadioKnobMode Mode(string name) => RadioKnobs.Ftdx101Modes.Single(m => m.Name == name);

        [Fact]
        public void Only_the_FTdx101_has_knobs()
        {
            Assert.NotNull(RadioKnobs.TableFor("FTdx101MP"));
            Assert.NotNull(RadioKnobs.TableFor("FTdx101D"));
            Assert.Null(RadioKnobs.TableFor("FT-991A"));
            Assert.Empty(RadioKnobs.ModesFor("FT-991A"));
        }

        [Fact]
        public void No_knob_is_a_transmit_command()
        {
            foreach (var model in new[] { "FTdx101MP", "FTdx101D" })
                foreach (var knob in RadioKnobs.TableFor(model)!)
                    Assert.DoesNotContain(knob.Command[..2], new[] { "TX", "MX", "AC", "KY", "KM", "BK", "VX" });
        }

        [Theory]
        [InlineData("MG", "MG050;", 50)]
        [InlineData("AO", "AO001;", 1)]
        [InlineData("PR0", "PR01;", 1)]
        [InlineData("GT0", "GT06;", 4)]   // AUTO-SLOW reads 6, is set as AUTO
        [InlineData("KP", "KP33;", 33)]
        [InlineData("RG0", "RG0247;", 247)]
        [InlineData("CO01", "CO010150;", 150)]
        [InlineData("PL", "PL036;", 36)]
        [InlineData("PC", "PC200;", 200)]
        [InlineData("ML1", "ML1100;", 100)]
        public void Answers_parse(string command, string answer, int value) =>
            Assert.Equal(value, RadioKnobs.ParseAnswer(Knob(command), answer));

        [Theory]
        [InlineData("MG", "PL050;")]   // another command's late answer
        [InlineData("MG", "MG50;")]    // wrong width
        [InlineData("PR0", "PR12;")]   // the mic EQ, not the processor
        [InlineData("ML1", "ML0001;")]
        [InlineData("MG", "?;")]
        [InlineData("MG", null)]
        public void Other_answers_are_refused(string command, string? answer) =>
            Assert.Null(RadioKnobs.ParseAnswer(Knob(command), answer));

        [Fact]
        public void Set_commands_are_padded_and_range_checked()
        {
            Assert.Equal("MG007;", RadioKnobs.SetCommand(Knob("MG"), 7));
            Assert.Equal("PR01;", RadioKnobs.SetCommand(Knob("PR0"), 1));
            Assert.Equal("PC200;", RadioKnobs.SetCommand(Knob("PC"), 200));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadioKnobs.SetCommand(Knob("PC", "FTdx101D"), 200));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadioKnobs.SetCommand(Knob("AO"), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => RadioKnobs.SetCommand(Knob("PC"), 4));
        }

        [Theory]
        [InlineData("MG", "50", 50)]
        [InlineData("PC", "100 W", 100)]
        [InlineData("PC", "100", 100)]
        [InlineData("PR0", "ON", 1)]
        [InlineData("PR0", "off", 0)]
        [InlineData("KP", "630 Hz", 33)]
        [InlineData("KP", "630", 33)]
        [InlineData("BP01", "1500 Hz", 150)]
        [InlineData("PA0", "AMP 2", 2)]
        [InlineData("GT0", "auto", 4)]
        [InlineData("KS", "25 wpm", 25)]
        public void Shown_values_encode(string command, string shown, int value) =>
            Assert.Equal(value, RadioKnobs.Encode(Knob(command), shown, out _));

        [Theory]
        [InlineData("MG", "101")]
        [InlineData("AO", "0")]
        [InlineData("PR0", "2")]
        [InlineData("PR0", "MAYBE")]
        [InlineData("KP", "635 Hz")]   // not a 10 Hz step
        [InlineData("KP", "250 Hz")]
        [InlineData("RA0", "24 dB")]
        [InlineData("MG", "loud")]
        public void Bad_values_do_not_encode(string command, string shown)
        {
            Assert.Null(RadioKnobs.Encode(Knob(command), shown, out var error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void Addresses_round_trip_and_cannot_be_EX_addresses()
        {
            var address = RadioKnobs.Address(Mode("DATA-U"), Knob("AO"));
            Assert.Equal("knob:C:AO", address);
            Assert.True(RadioKnobs.TryParseAddress(address, "FTdx101MP", out var slot));
            Assert.Equal(("DATA-U", "AO", (string?)null), (slot.Mode!.Name, slot.Knob.Command, slot.Band));
            Assert.False(RadioKnobs.TryParseAddress("knob:Z:AO", "FTdx101MP", out _));
            Assert.False(RadioKnobs.TryParseAddress("knob:2:TX", "FTdx101MP", out _));
            Assert.False(RadioKnobs.TryParseAddress(address, "FT-991A", out _));
            Assert.False(RadioKnobs.IsKnobAddress("030401"));
        }

        [Fact]
        public void Saved_file_with_knobs_parses_back()
        {
            var ini = RadioMenuCat.ToIni("FTdx101MP", new DateTime(2026, 11, 27, 15, 0, 0), new[]
            {
                new RadioMenuReading(Ftdx101Menu.Items.Single(i => i.Address == "010101"), "0160"),
            }) + RadioKnobs.ToIni(new[]
            {
                new RadioKnobReading(Mode("USB"), Knob("MG"), 50),
                new RadioKnobReading(Mode("USB"), Knob("PR0"), 1),
                new RadioKnobReading(Mode("DATA-U"), Knob("PC"), 25),
                new RadioKnobReading(Mode("DATA-U"), Knob("VG"), null),
            });
            Assert.Contains("[Front panel > USB]", ini);
            Assert.Contains("[Front panel > DATA-U]", ini);

            var file = RadioMenuLoad.Parse("﻿" + ini.Replace("\n", "\r\n"));
            Assert.Equal(5, file.Items.Count);
            Assert.Equal("010101", file.Items[0].Address);
            Assert.Equal(("MIC GAIN", "50", "knob:2:MG", "050"), (file.Items[1].Name, file.Items[1].Value, file.Items[1].Address, file.Items[1].SavedRaw));
            Assert.Equal(("ON", "knob:2:PR0"), (file.Items[2].Value, file.Items[2].Address));
            Assert.Equal(("25 W", "knob:C:PC"), (file.Items[3].Value, file.Items[3].Address));
            Assert.Equal((RadioMenuLoad.NoAnswer, "knob:C:VG", (string?)null), (file.Items[4].Value, file.Items[4].Address, file.Items[4].SavedRaw));
        }

        [Fact]
        public void A_knob_line_in_an_unknown_mode_has_no_address()
        {
            var file = RadioMenuLoad.Parse("[Front panel > SSTV]\nMIC GAIN = 50 ; MG = 050\n");
            Assert.Single(file.Items);
            Assert.Null(file.Items[0].Address);
        }

        [Theory]
        [InlineData("knob:RG0", "RF GAIN", "")]
        [InlineData("knob:PA0@20m", "IPO (20m)", "on 20m")]
        [InlineData("knob:2:MG", "MIC GAIN (USB)", "in USB")]
        [InlineData("knob:2:GT0@40m", "AGC (USB, 40m)", "in USB on 40m")]
        [InlineData("knob:3:BI", "BREAK-IN (CW-U)", "in CW-U")]
        public void Each_scope_has_its_own_address(string address, string label, string where)
        {
            Assert.True(RadioKnobs.TryParseAddress(address, "FTdx101MP", out var slot));
            Assert.Equal((address, label, where), (slot.Address, slot.Label, slot.Where));
        }

        [Theory]
        [InlineData("knob:2:RG0")]       // one for the whole radio: no mode
        [InlineData("knob:RG0@20m")]     // nor a band
        [InlineData("knob:PA0")]         // one per band needs the band
        [InlineData("knob:2:PA0@20m")]   // and no mode
        [InlineData("knob:2:GT0")]       // one per mode and band needs both
        [InlineData("knob:GT0@20m")]
        [InlineData("knob:MG")]          // one per mode needs the mode
        [InlineData("knob:2:BI")]        // BREAK-IN is only saved in CW
        [InlineData("knob:PA0@11m")]     // not a band
        public void Addresses_must_match_the_scope(string address) =>
            Assert.False(RadioKnobs.TryParseAddress(address, "FTdx101MP", out _));

        [Fact]
        public void Global_and_band_settings_save_and_parse_back()
        {
            var ini = RadioKnobs.ToIni(new[]
            {
                new RadioKnobReading(Mode("USB"), Knob("MG"), 50),
                new RadioKnobReading(null, Knob("RG0"), 247),
                new RadioKnobReading(null, Knob("KP"), 33),
                new RadioKnobReading(null, Knob("PA0"), 1, "20m"),
                new RadioKnobReading(Mode("USB"), Knob("GT0"), 3, "20m"),
            });
            // The settings not kept per mode come first, in their own section.
            Assert.True(ini.IndexOf("[Front panel]") < ini.IndexOf("[Front panel > USB]"));
            Assert.Contains("IPO (20m) = AMP 1", ini);
            Assert.Contains("KEY PITCH = 630 Hz", ini);

            var file = RadioMenuLoad.Parse(ini);
            var byName = file.Items.ToDictionary(i => i.Name);
            Assert.Equal(("knob:RG0", "247", "247"), (byName["RF GAIN"].Address, byName["RF GAIN"].Value, byName["RF GAIN"].SavedRaw));
            Assert.Equal(("knob:KP", "630 Hz"), (byName["KEY PITCH"].Address, byName["KEY PITCH"].Value));
            Assert.Equal(("knob:PA0@20m", "AMP 1"), (byName["IPO (20m)"].Address, byName["IPO (20m)"].Value));
            Assert.Equal(("knob:2:GT0@20m", "SLOW"), (byName["AGC (20m)"].Address, byName["AGC (20m)"].Value));
            Assert.Equal("knob:2:MG", byName["MIC GAIN"].Address);
            foreach (var item in file.Items)
                Assert.True(RadioKnobs.TryParseAddress(item.Address, "FTdx101MP", out _), item.Address);
        }

        [Fact]
        public void Only_transmit_settings_are_marked_TX()
        {
            var lines = RadioKnobs.ToIni(new[]
            {
                new RadioKnobReading(null, Knob("RG0"), 247),
                new RadioKnobReading(Mode("USB"), Knob("MG"), 50),
            }).Split('\n');
            Assert.DoesNotContain("[TX]", lines.Single(l => l.StartsWith("RF GAIN")));
            Assert.Contains("[TX]", lines.Single(l => l.StartsWith("MIC GAIN")));
        }

        [Fact]
        public void Volume_and_frequency_are_not_saved()
        {
            var commands = RadioKnobs.TableFor("FTdx101MP")!.Select(k => k.Command).ToList();
            Assert.DoesNotContain(commands, c => c.StartsWith("AG") || c.StartsWith("FA") || c.StartsWith("FB"));
            Assert.Equal(commands.Count, commands.Distinct().Count());
        }

        [Fact]
        public void NAR_loads_before_WIDTH()
        {
            var commands = RadioKnobs.TableFor("FTdx101MP")!.Select(k => k.Command).ToList();
            Assert.True(commands.IndexOf("NA0") < commands.IndexOf("SH00"));
        }

        [Theory]
        [InlineData("FA014074400;", "20m")]
        [InlineData("FA007100000;", "40m")]
        [InlineData("FA001850000;", "160m")]
        [InlineData("FA050313000;", "6m")]
        [InlineData("FA009500000;", "GEN")]
        [InlineData("FB014074400;", null)]
        [InlineData("?;", null)]
        public void Band_answers_parse(string answer, string? band) =>
            Assert.Equal(band, RadioKnobs.ParseBandAnswer(answer));

        [Fact]
        public void WIDTH_is_read_with_SH0_not_the_set_prefix()
        {
            // "SH00;" is not a read on the FTdx101 (#40); "SH0;" answers "SH0020;".
            Assert.Equal("SH0;", RadioKnobs.ReadCommand(Knob("SH00")));
            Assert.Equal(20, RadioKnobs.ParseAnswer(Knob("SH00"), "SH0020;"));
            Assert.Equal("SH0020;", RadioKnobs.SetCommand(Knob("SH00"), 20));
            Assert.Equal("MG;", RadioKnobs.ReadCommand(Knob("MG")));
        }

        [Theory]
        [InlineData("MG", "USB", true)]
        [InlineData("MG", "CW-U", false)]
        [InlineData("MG", "RTTY-L", false)]
        [InlineData("MG", "DATA-U", true)]
        [InlineData("BC0", "FM", false)]
        [InlineData("BC0", "AM", true)]
        [InlineData("BI", "CW-L", true)]
        [InlineData("BI", "USB", false)]
        public void Settings_the_radio_has_no_answer_for_are_left_out(string command, string mode, bool applies) =>
            Assert.Equal(applies, Knob(command).AppliesIn(Mode(mode)));

        /// <summary>A radio that keeps MIC GAIN per mode, answers MD0 and the knobs, and logs every command.</summary>
        private sealed class FakeRadio : IRadioKnobLink
        {
            public string Mode = "C";
            public readonly Dictionary<string, int> MicGain = new() { ["2"] = 40, ["C"] = 50 };
            public readonly List<string> Sent = new();
            private bool _transmitting;
            /// <summary>If set while transmitting, the radio goes back to receive after this many more
            /// transmit checks. Counting checks, not milliseconds, keeps the tests off the clock.</summary>
            public int? ReceiveAfterChecks;
            public bool Transmitting
            {
                get
                {
                    if (_transmitting && ReceiveAfterChecks is int left)
                    {
                        if (left <= 0) { _transmitting = false; ReceiveAfterChecks = null; }
                        else ReceiveAfterChecks = left - 1;
                    }
                    return _transmitting;
                }
                set => _transmitting = value;
            }
            public Action<string>? OnSend;
            public bool IgnoreModeChanges;

            public Task<string?> AskAsync(string command, CancellationToken ct)
            {
                Sent.Add(command);
                OnSend?.Invoke(command);
                return Task.FromResult<string?>(command switch
                {
                    "MD0;" => $"MD0{Mode};",
                    "MG;" => $"MG{MicGain.GetValueOrDefault(Mode, 30):000};",
                    _ => null,
                });
            }

            public Task TellAsync(string command, CancellationToken ct)
            {
                Sent.Add(command);
                OnSend?.Invoke(command);
                if (command.StartsWith("MD0") && !IgnoreModeChanges) Mode = command[3..^1];
                return Task.CompletedTask;
            }
        }

        private static RadioKnobPass Pass(FakeRadio radio) => new(radio, settleMs: 0, txWait: TimeSpan.FromMilliseconds(300));

        [Fact]
        public async Task The_pass_reads_in_each_mode_and_puts_the_mode_back()
        {
            var radio = new FakeRadio();
            var pass = Pass(radio);
            var read = new Dictionary<string, int?>();
            Assert.True(await pass.RunAsync(new[] { Mode("USB"), Mode("DATA-U"), Mode("AM") },
                async m => read[m.Name] = await pass.ReadAsync(Knob("MG"), default), default));

            Assert.Null(pass.Stopped);
            Assert.Equal(40, read["USB"]);
            Assert.Equal(50, read["DATA-U"]);
            Assert.Equal(30, read["AM"]);
            Assert.Equal("C", radio.Mode);
            Assert.Equal("MD0C;", radio.Sent.Last());
            // Already in DATA-U: no mode change sent for it.
            Assert.Equal(2, radio.Sent.Count(c => c is "MD02;" or "MD05;"));
        }

        [Fact]
        public async Task Nothing_is_changed_while_transmitting()
        {
            var radio = new FakeRadio { Transmitting = true };
            var pass = Pass(radio);
            Assert.False(await pass.RunAsync(new[] { Mode("USB") }, _ => Task.CompletedTask, default));
            Assert.Contains("transmitting", pass.Stopped);
            Assert.Empty(radio.Sent);
        }

        [Fact]
        public async Task Transmit_starting_mid_pass_stops_it_and_the_mode_goes_back_after_receive()
        {
            var radio = new FakeRadio();
            var pass = Pass(radio);
            var visited = new List<string>();
            var ok = await pass.RunAsync(new[] { Mode("USB"), Mode("AM"), Mode("FM") }, m =>
            {
                visited.Add(m.Name);
                if (m.Name == "USB")
                {
                    // Transmitting when the pass checks before AM, and at the first check
                    // while it waits to put the mode back; receiving at the next.
                    radio.Transmitting = true;
                    radio.ReceiveAfterChecks = 2;
                }
                return Task.CompletedTask;
            }, default);

            Assert.False(ok);
            Assert.Equal(new[] { "USB" }, visited);
            Assert.Contains("AM", pass.Stopped);
            Assert.Equal("C", radio.Mode);
        }

        [Fact]
        public async Task The_mode_is_not_put_back_while_still_transmitting()
        {
            var radio = new FakeRadio();
            var pass = Pass(radio);
            await pass.RunAsync(new[] { Mode("USB"), Mode("AM") }, _ =>
            {
                radio.Transmitting = true;
                return Task.CompletedTask;
            }, default);

            Assert.Equal("2", radio.Mode);
            Assert.DoesNotContain("MD0C;", radio.Sent);
            Assert.Contains("still transmitting", pass.Stopped);
        }

        [Fact]
        public async Task A_radio_that_will_not_change_mode_stops_the_pass()
        {
            var radio = new FakeRadio { IgnoreModeChanges = true };
            var pass = Pass(radio);
            var visited = 0;
            Assert.False(await pass.RunAsync(new[] { Mode("USB") }, _ => { visited++; return Task.CompletedTask; }, default));
            Assert.Equal(0, visited);
            Assert.Contains("USB", pass.Stopped);
        }

        [Fact]
        public async Task The_mode_goes_back_when_a_step_throws()
        {
            var radio = new FakeRadio();
            var pass = Pass(radio);
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                pass.RunAsync(new[] { Mode("USB") }, _ => throw new OperationCanceledException(), default));
            Assert.Equal("C", radio.Mode);
        }
    }
}
