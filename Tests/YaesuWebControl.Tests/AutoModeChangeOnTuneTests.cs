using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Yaesu_Web_Control.Models;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// Discussion #169 (Bruce VK2RT). Clicking the spectrum set the mode from
    /// the band plan on every click, and there was no way to stop it. Operators
    /// do not follow the band plan: 40m SSB is used around 7.050, which our own
    /// table calls DATA-U, and RTTY runs well above 14.100, which it calls USB.
    ///
    /// The damage is on transmit, not receive. SSB MOD SOURCE defaults to MIC
    /// and DATA MOD SOURCE to REAR on the FTdx101MP/D and the FTdx10, so an
    /// unwanted mode change moves the transmit audio input from the rear/USB
    /// port to the front-panel microphone while receive carries on decoding
    /// normally -- invisible until the operator transmits.
    /// </summary>
    public class AutoModeChangeOnTuneTests
    {
        [Fact]
        public void DefaultsToOnSoExistingBehaviourIsUnchanged()
        {
            Assert.True(new ApplicationSettings().AutoModeChangeOnTune);
        }

        [Fact]
        public void SettingsFileWrittenBeforeThisExistedStillReadsAsOn()
        {
            // Every user upgrading has a file with no such key. Binding it to
            // false would silently take the automatic mode change away from
            // everyone who never asked for that.
            var s = JsonSerializer.Deserialize<ApplicationSettings>(
                """{ "SerialPort": "COM4", "BandPlan": "Region3" }""");

            Assert.NotNull(s);
            Assert.True(s!.AutoModeChangeOnTune);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RoundTripsThroughJson(bool value)
        {
            var written = new ApplicationSettings { AutoModeChangeOnTune = value };
            var read = JsonSerializer.Deserialize<ApplicationSettings>(
                JsonSerializer.Serialize(written));

            Assert.NotNull(read);
            Assert.Equal(value, read!.AutoModeChangeOnTune);
        }

        [Fact]
        public void SettingsPagePersistsIt()
        {
            string cs = File.ReadAllText(LocateRepoPath("Pages/Settings.cshtml.cs"));
            Assert.Matches(
                @"current\.AutoModeChangeOnTune\s*=\s*Settings\.AutoModeChangeOnTune\s*;",
                cs);
        }

        [Fact]
        public void IndexRendersTheFlagForTheBrowser()
        {
            string page = File.ReadAllText(LocateRepoPath("Pages/Index.cshtml"));
            Assert.Contains("window.ywcAutoModeChangeOnTune", page, StringComparison.Ordinal);

            string model = File.ReadAllText(LocateRepoPath("Pages/Index.cshtml.cs"));
            Assert.Matches(
                @"AutoModeChangeOnTune\s*=\s*settings\.AutoModeChangeOnTune\s*;",
                model);
        }

        [Fact]
        public void AutoModeForHzIsTheOnlyPlaceTheFlagIsRead()
        {
            string js = File.ReadAllText(LocateRepoPath("wwwroot/js/ui/band-plan.js"));

            // Off must mean "no opinion", which is what out-of-band already
            // returns, so every existing caller handles it with no change.
            Assert.Matches(
                @"export function autoModeForHz\s*\(\s*hz\s*\)\s*\{[^}]*ywcAutoModeChangeOnTune\s*===\s*false[^}]*return null;",
                js);

            // Absent (other pages, unit tests) must not read as off.
            Assert.DoesNotContain("!globalThis.ywcAutoModeChangeOnTune", js, StringComparison.Ordinal);
            Assert.DoesNotContain("!window.ywcAutoModeChangeOnTune", js, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("wwwroot/js/sdr/spectrum-panel.js")]      // click-to-tune
        [InlineData("wwwroot/js/ui/dx-spots-panel.js")]       // clicking a spot row
        [InlineData("wwwroot/js/ui/keyboard-shortcuts.js")]   // stepping spots from the keyboard
        public void EveryUnaskedForModeChangeGoesThroughTheGate(string relative)
        {
            string js = File.ReadAllText(LocateRepoPath(relative));

            Assert.Contains("autoModeForHz", js, StringComparison.Ordinal);

            // A call to the ungated modeForHz here would bypass the setting.
            // Comments mentioning it by name are fine; calls are not.
            Assert.DoesNotMatch(@"(?<!auto)(?<!\w)modeForHz\s*\(", StripComments(js));
        }

        [Fact]
        public void AfskRttyInDataLGetsTheRttyTuneOffset()
        {
            // Bruce runs RTTY as AFSK in DATA-L. With the switch off the tune
            // offset comes from the radio's own mode, and DATA-L used to get
            // zero: every click put the dial on the signal and the tones near
            // 0 Hz audio, where no decoder can copy them.
            string js = StripComments(File.ReadAllText(LocateRepoPath("wwwroot/js/sdr/spectrum-panel.js")));
            var offset = Regex.Match(js, @"_tuneOffsetHz\s*\(\s*mode\s*\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);

            Assert.True(offset.Success, "_tuneOffsetHz not found");
            Assert.Contains("'DATA-L'", offset.Groups["body"].Value, StringComparison.Ordinal);
            // DATA-U is FT8 and the other digital modes: dial onto the click.
            Assert.DoesNotContain("'DATA-U'", offset.Groups["body"].Value, StringComparison.Ordinal);

            // In AFSK the software makes the tones, so the radio's RTTY MARK
            // menu plays no part. Colin's FTdx101MP answers that menu with a
            // code YWC reads as 1275 Hz while its RTTY-L audio puts mark on
            // 2125 Hz; tying DATA-L to it would have landed clicks 850 Hz off.
            Assert.Contains("return AFSK_RTTY_MIDPOINT_AUDIO_HZ;", offset.Groups["body"].Value, StringComparison.Ordinal);
            Assert.Matches(@"const AFSK_RTTY_MIDPOINT_AUDIO_HZ = 2210;", js);
            Assert.DoesNotContain("_rttyMarkHz", js, StringComparison.Ordinal);
        }

        [Fact]
        public void RttyModesTuneTheDialOntoTheSignalNotAMarkFrequencyAway()
        {
            // Measured on the FTdx101MP 2026-09-23: in RTTY-L the dial is the
            // mark tone (a carrier at 810.000 whistles with the dial at
            // 810.000, not at 812.210). The old +2210 Hz offset put every
            // clicked RTTY signal outside the 500 Hz filter. Only half the
            // shift is allowed between the click and the dial.
            string js = StripComments(File.ReadAllText(LocateRepoPath("wwwroot/js/sdr/spectrum-panel.js")));
            var branch = Regex.Match(js, @"if \(mode === 'RTTY-L' \|\| mode === 'RTTY-U'\) \{(?<body>.*?)\n        \}", RegexOptions.Singleline);

            Assert.True(branch.Success, "RTTY branch of _tuneOffsetHz not found");
            Assert.DoesNotContain("_rttyMarkHz", branch.Groups["body"].Value, StringComparison.Ordinal);
            Assert.DoesNotContain("_rttyAnchorAudioHz", branch.Groups["body"].Value, StringComparison.Ordinal);
            Assert.Contains("_rttyShiftHz / 2", branch.Groups["body"].Value, StringComparison.Ordinal);
        }

        [Fact]
        public void PickingANamedSegmentStillCarriesThatSegmentsMode()
        {
            // The setting is about modes inferred from a frequency. Choosing
            // "RTTY" from a VFO's band dropdown is an explicit choice, and
            // gating it too would make that dropdown half-work.
            string js = StripComments(File.ReadAllText(LocateRepoPath("wwwroot/js/ui/site.js")));

            Assert.Contains("await window.setMode(vfo, mode);", js, StringComparison.Ordinal);
            Assert.DoesNotContain("autoModeForHz", js, StringComparison.Ordinal);
        }

        private static string StripComments(string js)
        {
            js = Regex.Replace(js, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            js = Regex.Replace(js, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
            return js;
        }

        private static string LocateRepoPath(string relative)
        {
            string native = relative.Replace('/', Path.DirectorySeparatorChar);
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Could not find {relative} above {AppContext.BaseDirectory}.");
        }
    }
}
