using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The host exits about 30 seconds after its last SignalR connection drops,
    /// so closing the last tab closes the app. Pages that use _Layout are safe
    /// without thinking about it: site.js opens that connection and keeps it.
    ///
    /// Pages that set <c>Layout = null</c> are not. Remote Audio was one — a
    /// listener sitting on it with no other tab open had the host exit
    /// underneath them mid-transmission, and nothing in the page said why it
    /// mattered. Radio Display escaped only because it happened to want live
    /// scope state.
    ///
    /// So the rule is enforced here rather than remembered: a layout-less page
    /// must connect to /radioHub, either directly or through
    /// wwwroot/js/ui/host-presence.js.
    /// </summary>
    public class HostPresenceTests
    {
        [Fact]
        public void EveryLayoutlessPageRegistersHostPresence()
        {
            string pages = LocateRepoPath("Pages");

            var offenders = new List<string>();
            var checkedPages = new List<string>();

            foreach (string file in Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);

                // Razor tolerates whitespace either side of the '='.
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"Layout\s*=\s*null"))
                    continue;

                string name = Path.GetRelativePath(pages, file).Replace('\\', '/');
                checkedPages.Add(name);

                // Match the call, not the string: the first draft of this
                // test looked for "/radioHub" anywhere, and a comment
                // explaining why the page needed one was enough to pass it.
                bool connectsItself = System.Text.RegularExpressions.Regex.IsMatch(
                    text, @"withUrl\s*\(\s*[""']/radioHub[""']");
                bool usesHelper = System.Text.RegularExpressions.Regex.IsMatch(
                    text, @"keepHostAlive\s*\(");

                if (!connectsItself && !usesHelper)
                    offenders.Add(name);
            }

            // If this ever finds nothing, the detection above has drifted —
            // say so rather than passing an empty sweep.
            Assert.True(checkedPages.Count > 0,
                $"No 'Layout = null' pages found under {pages}. This test sweeps for them " +
                "by source text, so an empty result means the sweep is broken, not that the " +
                "rule is satisfied.");

            Assert.True(offenders.Count == 0,
                "These pages set Layout = null, so they get neither site.js nor the /radioHub " +
                "connection it opens — and that connection is how the host knows a browser is " +
                "still here. Left as they are, the host will exit about 30 seconds after one " +
                "of these is the only page open:" + Environment.NewLine +
                string.Join(Environment.NewLine, offenders.Select(o => "    " + o)) +
                Environment.NewLine + Environment.NewLine +
                "Fix by loading signalr.min.js and calling keepHostAlive() from " +
                "/js/ui/host-presence.js, or by opening a /radioHub connection the page needs " +
                "anyway. Pages checked: " + string.Join(", ", checkedPages));
        }

        /// <summary>
        /// The helper exists and still exports the function the pages import.
        /// A rename would leave the assertion above passing on the filename
        /// alone while the call it vouches for was gone.
        /// </summary>
        [Fact]
        public void HostPresenceHelperExportsKeepHostAlive()
        {
            string js = File.ReadAllText(LocateRepoPath("wwwroot/js/ui/host-presence.js"));

            Assert.Contains("export function keepHostAlive", js, StringComparison.Ordinal);
            Assert.Contains("/radioHub", js, StringComparison.Ordinal);
        }

        /// <summary>
        /// Walk up from the test assembly to the repository root. The binary
        /// sits several levels below it and the depth differs between a local
        /// build and a published one, so search rather than assume.
        /// </summary>
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
                $"Could not find {relative} above {AppContext.BaseDirectory}. This test reads " +
                "the app's Razor pages from source and needs the repository tree.");
        }
    }
}
