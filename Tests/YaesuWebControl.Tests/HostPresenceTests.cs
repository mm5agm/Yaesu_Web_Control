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
                    text, @"(withUrl|ywcHubConnection)\s*\(\s*[""']/radioHub[""']");
                bool usesHelper = System.Text.RegularExpressions.Regex.IsMatch(
                    text, @"keepHostAlive\s*\(");

                if (!connectsItself && !usesHelper)
                    offenders.Add(name);

                // Calling window.ywcHubConnection() without loading the script
                // that defines it fails at runtime and nowhere else: the page
                // renders, logs a warning to a console nobody has open, and
                // quietly stops holding the host up. _Layout supplies it for
                // every other page; these have no _Layout.
                if (text.Contains("ywcHubConnection", StringComparison.Ordinal)
                    && !text.Contains("js/ui/hub-connection.js", StringComparison.Ordinal))
                {
                    offenders.Add(name + " (calls ywcHubConnection but never loads hub-connection.js)");
                }
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
        /// Every /radioHub connection is built by wwwroot/js/ui/hub-connection.js
        /// and nowhere else.
        ///
        /// This is not tidiness. A connection built by hand gets
        /// withAutomaticReconnect()'s default policy, which retries at 0s, 2s,
        /// 10s and 30s and then gives up permanently — so one lost connection
        /// leaves that page dead until it is reloaded. On 2026-09-19 an idle
        /// About page was dropped after 24 minutes, never came back, and the
        /// host exited 30 seconds later while the page was still on screen.
        /// The shared builder retries forever and raises the timeouts to match
        /// the server's; a hand-rolled one silently opts out of both.
        /// </summary>
        [Fact]
        public void EveryHubConnectionUsesTheSharedBuilder()
        {
            string repo = Path.GetDirectoryName(LocateRepoPath("Pages"))!;
            string helper = Path.Combine(repo, "wwwroot", "js", "ui", "hub-connection.js");

            // wwwroot/js/<area> mirroring core/js/<area> is a build-time copy of
            // shared code, which cannot depend on this app's globals. Derived
            // from core/ rather than listed, so a new shared area needs no edit.
            var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string coreJs = Path.Combine(repo, "core", "js");
            if (Directory.Exists(coreJs))
            {
                foreach (string d in Directory.EnumerateDirectories(coreJs))
                    generated.Add(Path.GetFileName(d));
            }

            var sources = new List<string>();
            string wwwJs = Path.Combine(repo, "wwwroot", "js");
            foreach (string file in Directory.EnumerateFiles(wwwJs, "*.js", SearchOption.AllDirectories))
            {
                string area = Path.GetRelativePath(wwwJs, file).Split(Path.DirectorySeparatorChar)[0];
                if (generated.Contains(area)) continue;
                sources.Add(file);
            }
            sources.AddRange(Directory.EnumerateFiles(LocateRepoPath("Pages"), "*.cshtml", SearchOption.AllDirectories));

            var offenders = sources
                .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(helper), StringComparison.OrdinalIgnoreCase))
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                    File.ReadAllText(f), @"HubConnectionBuilder\s*\(\s*\)"))
                .Select(f => Path.GetRelativePath(repo, f).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(f => f)
                .ToList();

            Assert.True(offenders.Count == 0,
                "These build a SignalR connection directly instead of calling " +
                "window.ywcHubConnection(\"/radioHub\") from wwwroot/js/ui/hub-connection.js. " +
                "A hand-built connection takes withAutomaticReconnect()'s default policy, which " +
                "stops retrying after about 42 seconds and leaves the page dead:" + Environment.NewLine +
                string.Join(Environment.NewLine, offenders.Select(o => "    " + o)));
        }

        /// <summary>
        /// The shared builder still does the two things everything else trusts
        /// it for: a retry policy of its own, and a raised server timeout.
        /// </summary>
        [Fact]
        public void SharedBuilderRetriesForeverAndRaisesTimeouts()
        {
            string js = File.ReadAllText(LocateRepoPath("wwwroot/js/ui/hub-connection.js"));

            Assert.Contains("window.ywcHubConnection", js, StringComparison.Ordinal);
            Assert.Contains("nextRetryDelayInMilliseconds", js, StringComparison.Ordinal);
            Assert.Contains("serverTimeoutInMilliseconds", js, StringComparison.Ordinal);

            // Assert the policy is passed rather than the absence of the
            // argument-less form: this file explains that default in its own
            // header, and the first version of this line matched the comment.
            Assert.Matches(@"withAutomaticReconnect\s*\(\s*retryPolicy\s*\)", js);
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
