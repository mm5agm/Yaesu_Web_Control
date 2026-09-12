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
        // ---- the SSB table is the JavaScript's, entry for entry ------------

        [Fact]
        public void The_SSB_slide_table_matches_the_browser_copy()
        {
            var js = ParseSsbTable(File.ReadAllText(LocateJavaScript()));
            var cs = YaesuIfOutOffset.SsbSlideHz101.Select(e => (e.WidthHz, e.SlideHz)).ToList();
            Assert.Equal(js, cs);
        }

        // ---- the formula, on measured points ------------------------------

        [Theory]
        [InlineData("CW-U", 3500,  700, 0, 1400)]   // Colin's filter on 2026-09-12: dial 1.4 kHz off the nominal IF OUT
        [InlineData("CW-U",  500,  700, 0,    0)]   // narrower than the pitch: no slide
        [InlineData("CW-L", 2400,  600, 0,  900)]
        [InlineData("CW-U", 3500,  700, -200, 1200)] // IF shift adds directly
        [InlineData("USB",  2400,  700, 0,  350)]
        [InlineData("USB",  3000,  700, 0, 1400)]
        [InlineData("LSB",  2100,  700, 0,    0)]   // below the first measured width
        [InlineData("DATA-U", 3200, 700, 0, 1650)]
        [InlineData("RTTY-U", 500, 700, 100, 100)]  // shift only
        [InlineData("FM",   null,  700, 0,    0)]
        public void LO_slide_follows_mode_width_shift_and_pitch(string mode, int? widthHz, int pitchHz, int shiftHz, int expected)
        {
            Assert.Equal(expected, YaesuIfOutOffset.LoSlideHz("FTdx101MP", mode, widthHz, shiftHz, pitchHz));
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
            // SH code 20 in CW on the '101 is 3500 Hz; KP code 40 is 700 Hz.
            Assert.Equal(9_005_000 + 1_400, YaesuIfOutOffset.DialIfHz("FTdx101MP", "A", "CW-U", "20", 0, 40));
            Assert.Equal(8_900_000 + 1_400, YaesuIfOutOffset.DialIfHz("FTdx101MP", "B", "CW-U", "20", 0, 40));
            // Code 0 is the radio's default width and resolves to no known width — no width term.
            Assert.Equal(9_005_000, YaesuIfOutOffset.DialIfHz("FTdx101MP", "A", "CW-U", "0", 0, 40));
        }

        // ---- reading the JavaScript ----------------------------------------

        private static List<(int, int)> ParseSsbTable(string text)
        {
            int at = text.IndexOf("SSB_SLIDE_HZ_101", StringComparison.Ordinal);
            Assert.True(at >= 0, "SSB_SLIDE_HZ_101 not found in if-out-offset.js");
            int open  = text.IndexOf('[', at);
            int close = text.IndexOf("];", open, StringComparison.Ordinal);
            string block = text.Substring(open + 1, close - open - 1);

            var rows = Regex.Matches(block, @"\[\s*(\d+)\s*,\s*(\d+)\s*\]")
                .Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))
                .ToList();
            Assert.NotEmpty(rows);
            return rows;
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
                "compares the C# LO-slide table against the browser's copy and needs both.");
        }
    }
}
