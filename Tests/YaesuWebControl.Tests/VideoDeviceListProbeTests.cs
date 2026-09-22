using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// Burst-refreshing Index used to GET /api/video/devices on every load,
    /// and that endpoint probe-opened every camera. A second Open on an HDMI
    /// capture dongle native-crashes the host: the log just stops, no
    /// [Crash] line, no ApplicationStopping. 2026-09-21 22:17 was one.
    ///
    /// Page load must list names only. Only the operator's Refresh button
    /// may pass probe=1.
    /// </summary>
    public class VideoDeviceListProbeTests
    {
        [Fact]
        public void DevicesEndpointDoesNotProbeUnlessAsked()
        {
            string cs = File.ReadAllText(LocateRepoPath("Controllers/VideoController.cs"));

            Assert.Matches(@"ListDevices\s*\(\s*\[FromQuery\]\s*bool\s+probe\s*=\s*false\s*\)", cs);
            Assert.Matches(@"allowProbe:\s*probe\s*&&\s*!_capture\.IsCapturing", cs);

            // A later edit that probes from GET status would reopen the same
            // crash on every 4-second poll, not only on refresh.
            Assert.Contains(
                "VideoDeviceFpsCaps.RatesFor(s.VideoCaptureDeviceKey, allowDeviceOpen: false)",
                cs,
                StringComparison.Ordinal);
            Assert.Contains(
                "VideoDeviceSizeCaps.SizesFor(s.VideoCaptureDeviceKey, allowDeviceOpen: false)",
                cs,
                StringComparison.Ordinal);
        }

        [Fact]
        public void PageLoadDeviceListDoesNotProbeAndRefreshButtonDoes()
        {
            string js = File.ReadAllText(LocateRepoPath("wwwroot/js/video/radio-display-ui.js"));

            Assert.Matches(
                @"async function loadDeviceSelect\s*\(\s*selectedKey\s*,\s*\{\s*probe\s*=\s*false",
                js);
            Assert.Contains("/api/video/devices?probe=1", js, StringComparison.Ordinal);
            Assert.Contains("loadDeviceSelect(currentDeviceKey, { probe: true })", js, StringComparison.Ordinal);

            // init and status-driven reloads must stay on the default (no probe).
            Assert.True(
                Regex.Matches(js, @"loadDeviceSelect\s*\(\s*currentDeviceKey\s*\)").Count >= 1,
                "Expected at least one loadDeviceSelect(currentDeviceKey) without probe, " +
                "for init / status refresh.");
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
