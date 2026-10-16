using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The band plan's RTTY segments must select RTTY-L on every band. In
    /// RTTY-U the FTdx101's dial is still on the upper tone, so the radio's
    /// own decoder reads amateur RTTY reversed and prints rubbish (CQ WW RTTY,
    /// 2026-09-26). The segment table exists twice - the JS defaults and the
    /// JSON overlay the page loads on top of them - and the first fix changed
    /// only the JS, so the dropdown still selected RTTY-U. Both are checked.
    /// </summary>
    public class BandPlanRttyModeTests
    {
        [Theory]
        [InlineData("wwwroot/js/ui/band-plan.js",       @"RTTY:\s*\{[^}]*mode:\s*'(?<mode>[^']+)'")]
        [InlineData("wwwroot/bandplan.default.json",    @"""RTTY""\s*:\s*\{[^}]*""mode""\s*:\s*""(?<mode>[^""]+)""")]
        public void EveryRttySegmentSelectsRttyL(string relative, string pattern)
        {
            string text = File.ReadAllText(LocateRepoPath(relative));
            var matches = Regex.Matches(text, pattern);

            Assert.NotEmpty(matches);
            foreach (Match m in matches)
                Assert.Equal("RTTY-L", m.Groups["mode"].Value);
        }

        private static string LocateRepoPath(string relative)
        {
            string native = relative.Replace('/', Path.DirectorySeparatorChar);
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                $"Could not find {relative} walking up from {AppContext.BaseDirectory}");
        }
    }
}
