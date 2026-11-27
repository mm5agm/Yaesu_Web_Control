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
        [InlineData("PR0", "PR02;", 2)]
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
        [InlineData("PR0", "ON", 2)]
        [InlineData("PR0", "off", 1)]
        public void Shown_values_encode(string command, string shown, int value) =>
            Assert.Equal(value, RadioKnobs.Encode(Knob(command), shown, out _));

        [Theory]
        [InlineData("MG", "101")]
        [InlineData("AO", "0")]
        [InlineData("PR0", "2")]
        [InlineData("PR0", "MAYBE")]
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
            Assert.True(RadioKnobs.TryParseAddress(address, "FTdx101MP", out var mode, out var knob));
            Assert.Equal(("DATA-U", "AO"), (mode.Name, knob.Command));
            Assert.False(RadioKnobs.TryParseAddress("knob:Z:AO", "FTdx101MP", out _, out _));
            Assert.False(RadioKnobs.TryParseAddress("knob:2:TX", "FTdx101MP", out _, out _));
            Assert.False(RadioKnobs.TryParseAddress(address, "FT-991A", out _, out _));
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
                new RadioKnobReading(Mode("USB"), Knob("PR0"), 2),
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

        /// <summary>A radio that keeps MIC GAIN per mode, answers MD0 and the knobs, and logs every command.</summary>
        private sealed class FakeRadio : IRadioKnobLink
        {
            public string Mode = "C";
            public readonly Dictionary<string, int> MicGain = new() { ["2"] = 40, ["C"] = 50 };
            public readonly List<string> Sent = new();
            public bool Transmitting { get; set; }
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
                    radio.Transmitting = true;
                    _ = Task.Delay(100).ContinueWith(_ => radio.Transmitting = false);
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
