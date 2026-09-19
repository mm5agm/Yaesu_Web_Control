using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The Filter Function Display sits beside a real receiver, so anything
    /// drawn inside its passband is read as what the receiver is hearing.
    ///
    /// It used to fill the trapezium with Math.random() bars whenever no
    /// Remote Audio session was attached -- which is the normal case, and the
    /// permanent case on any installation that has never set Remote Audio up.
    /// The manual described those bars as "signals passing through the
    /// filter". On #161 Bruce VK2RT compared the panel with his FTdx101MP's
    /// own filter scope and asked why they disagreed; they disagreed because
    /// ours was showing noise it had made up.
    ///
    /// Drawing bars from live audio is fine. Drawing them from nothing is the
    /// defect, and it is invisible in review -- a random fill looks like a
    /// working panel.
    /// </summary>
    public class FilterScopeTests
    {
        [Fact]
        public void FilterScopeNeverInventsSignal()
        {
            string file = LocateRepoPath("wwwroot/js/ui/filter-scope-panel.js");
            string code = StripComments(File.ReadAllText(file));

            Assert.False(
                code.Contains("Math.random", StringComparison.Ordinal),
                "wwwroot/js/ui/filter-scope-panel.js generates random numbers in code " +
                "(not just in a comment). The one thing this panel must never do is " +
                "draw a signal that no receiver produced -- see #161. Bars come from " +
                "the spectrum provider or they are not drawn at all.");
        }

        /// <summary>
        /// Comments are stripped before the check above, because the file
        /// explains in prose what it must not do. Two earlier source-rule
        /// tests in this project passed or failed on their own explanatory
        /// comments; this avoids repeating that.
        /// </summary>
        private static string StripComments(string js)
        {
            js = Regex.Replace(js, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            js = Regex.Replace(js, @"(?m)//.*$", " ");
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

            throw new FileNotFoundException(
                $"Could not find {relative} above {AppContext.BaseDirectory}. This test reads " +
                "the app's frontend from source and needs the repository tree.");
        }
    }
}
