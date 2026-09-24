using System.Text.RegularExpressions;
using Xunit;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// YaesuIfOutOffset is a port of loSlideHz in wwwroot/js/sdr/if-out-offset.js.
    /// The browser uses the JavaScript to label the axis; the worker's crop
    /// window is centred by the C#. If the two disagree, a signal is drawn
    /// away from where it is by exactly the difference, and nothing else
    /// notices. These tests hold them together.
    /// </summary>
    public class YaesuIfOutOffsetTests
    {
        // ---- the constants are the JavaScript's ----------------------------

        [Theory]
        [InlineData("SSB_CARRIER_POINT_HZ_101", YaesuIfOutOffset.SsbCarrierPointHz101)]
        [InlineData("RTTY_CENTRE_HZ_101", YaesuIfOutOffset.RttyCentreHz101)]
        [InlineData("CW_MAX_SLIDE_WIDTH_HZ_101", YaesuIfOutOffset.CwMaxSlideWidthHz101)]
        public void The_constants_match_the_browser_copy(string name, int expected)
        {
            Assert.Equal(expected, ParseConstant(File.ReadAllText(LocateJavaScript()), name));
        }

        // ---- the formula, on points measured 2026-09-24 (#172) -------------
        // Measured with the receiver's audio as ground truth: the 810 kHz
        // carrier heard at a known tone, then located in the SDR stream.

        [Theory]
        [InlineData("USB",    2800, 700,    0,  1500)]   // every width 1100..4000 gave 1497
        [InlineData("USB",    1100, 700,    0,  1500)]
        [InlineData("LSB",    2800, 700,    0, -1500)]   // #172: was +1150, drawing LSB 2.6 kHz right of the dial
        [InlineData("USB",    3200, 700,  500,  2000)]   // IF shift adds...
        [InlineData("LSB",    3200, 700, -500, -1000)]   // ...and in LSB it subtracts
        [InlineData("DATA-U", 1500, 700,    0,  1500)]
        [InlineData("DATA-L", 1500, 700,    0, -1500)]   // VK2RT's #178 shot: a DATA-L signal drawn on the dial
        [InlineData("CW-U",   1200, 700,    0,   250)]
        [InlineData("CW-L",   2400, 700,    0,  -850)]   // measured -849: the mirror of CW-U
        [InlineData("CW-L",   2400, 700,  300, -1150)]
        [InlineData("CW-U",    500, 700,    0,     0)]   // narrower than the pitch: no slide
        [InlineData("CW-U",   4000, 700,    0,  1150)]   // stops growing at 3000: 3000..4000 all gave 1149
        [InlineData("RTTY-L", 1200, 700,    0,   -85)]
        [InlineData("RTTY-L", 1200, 700,  300,  -385)]
        [InlineData("RTTY-U", 1200, 700,  300,   215)]
        [InlineData("AM",     9000, 700,  300,     0)]   // the shift does nothing in AM
        [InlineData("FM",     null, 700,    0,     0)]
        public void LO_slide_follows_mode_sideband_width_shift_and_pitch(string mode, int? widthHz, int pitchHz, int shiftHz, int expected)
        {
            Assert.Equal(expected, YaesuIfOutOffset.LoSlideHz("FTdx101MP", mode, widthHz, shiftHz, pitchHz));
        }

        [Theory]
        [InlineData("DATA-L", 1000,   0, -1000)]   // measured -1003 with DATA SHIFT 1000, 2026-09-24
        [InlineData("DATA-U", 1000,   0,  1000)]   // measured +997
        [InlineData("DATA-U", 1000, 500,  1500)]   // IF shift still adds
        [InlineData("DATA-L", 1500,   0, -1500)]   // the menu default: as before
        [InlineData("LSB",    1000,   0, -1500)]   // SSB ignores DATA SHIFT
        [InlineData("USB",    1000,   0,  1500)]
        public void DATA_slide_follows_the_DATA_SHIFT_menu(string mode, int dataShiftHz, int shiftHz, int expected)
        {
            Assert.Equal(expected, YaesuIfOutOffset.LoSlideHz("FTdx101MP", mode, 2400, shiftHz, 700, dataShiftHz));
            Assert.Equal(9_005_000 + expected,
                YaesuIfOutOffset.DialIfHz("FTdx101MP", "A", mode, "13", shiftHz, 40, dataShiftHz));
        }

        [Fact]
        public void An_unmeasured_model_gets_no_slide()
        {
            Assert.Equal(0, YaesuIfOutOffset.LoSlideHz("FTdx10", "CW-U", 3000, 0, 700));
            Assert.Null(YaesuIfOutOffset.DialIfHz("FTdx10", "A", "CW-U", "18", 0, 40));
        }

        [Fact]
        public void The_dial_IF_is_IF_OUT_plus_the_slide_from_raw_CAT_codes()
        {
            // SH code 13 in CW on the '101 is 1200 Hz; KP code 40 is 700 Hz.
            Assert.Equal(9_005_000 + 250, YaesuIfOutOffset.DialIfHz("FTdx101MP", "A", "CW-U", "13", 0, 40));
            Assert.Equal(8_900_000 + 250, YaesuIfOutOffset.DialIfHz("FTdx101MP", "B", "CW-U", "13", 0, 40));
            Assert.Equal(9_005_000 - 250, YaesuIfOutOffset.DialIfHz("FTdx101MP", "A", "CW-L", "13", 0, 40));
            // Code 0 is the radio's default width and resolves to no known width — no width term.
            Assert.Equal(9_005_000, YaesuIfOutOffset.DialIfHz("FTdx101MP", "A", "CW-U", "0", 0, 40));
        }

        // ---- reading the JavaScript ----------------------------------------

        private static int ParseConstant(string text, string name)
        {
            var m = Regex.Match(text, @"const\s+" + name + @"\s*=\s*(-?\d+)\s*;");
            Assert.True(m.Success, $"{name} not found in if-out-offset.js");
            return int.Parse(m.Groups[1].Value);
        }

        private static string LocateJavaScript()
        {
            const string relative = "wwwroot/js/sdr/if-out-offset.js";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException(
                $"Could not find {relative} above {AppContext.BaseDirectory}. This test " +
                "compares the C# LO-slide constants against the browser's copy and needs both.");
        }
    }
}
