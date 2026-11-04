using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// Discussion #254 (Rick W2JAZ). The Meter Poll Interval went back to 200
    /// every time Settings was saved. The Save handler writes field by field
    /// onto the stored settings, and this field had no line in it, so the value
    /// was thrown away while the page still said it had saved.
    /// </summary>
    public class SettingsSaveTests
    {
        // On the form, and not saved. Advanced Mode is the gate that stops an
        // imported voice pack sending any CAT command it likes, so whether to
        // make it save is a decision of its own, not a tidy-up.
        private static readonly string[] KnownNotSaved = { "VoiceAdvancedModeEnabled" };

        [Fact]
        public void MeterPollIntervalIsSavedWithinItsRange()
        {
            string cs = File.ReadAllText(LocateRepoPath("Pages/Settings.cshtml.cs"));
            Assert.Matches(
                @"current\.MeterPollIntervalMs\s*=\s*Math\.Clamp\(\s*Settings\.MeterPollIntervalMs\s*,\s*50\s*,\s*1000\s*\)\s*;",
                cs);
        }

        [Fact]
        public void EveryFieldOnTheFormIsSaved()
        {
            string page = File.ReadAllText(LocateRepoPath("Pages/Settings.cshtml"));
            string cs = File.ReadAllText(LocateRepoPath("Pages/Settings.cshtml.cs"));

            var onForm = Regex.Matches(page, @"(?:asp-for|name)=""Settings\.(\w+)""")
                .Select(m => m.Groups[1].Value);
            var saved = new HashSet<string>(
                Regex.Matches(cs, @"current\.(\w+)\s*=[^=]").Select(m => m.Groups[1].Value));

            var missing = onForm.Distinct()
                .Where(f => !saved.Contains(f))
                .Except(KnownNotSaved)
                .OrderBy(f => f, StringComparer.Ordinal);

            Assert.Empty(missing);
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
