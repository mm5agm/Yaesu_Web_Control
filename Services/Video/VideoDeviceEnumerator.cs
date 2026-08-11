using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenCvSharp;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Lists USB webcams / HDMI capture dongles exposed to OpenCV.
    /// Linux: V4L2 sysfs names. macOS: system_profiler camera names + probe.
    /// Windows / fallback: probe OpenCV indices.
    /// </summary>
    public static class VideoDeviceEnumerator
    {
        private const int MaxProbeIndex = 15;

        public static IReadOnlyList<VideoDeviceInfo> ListDevices()
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    var linux = EnumerateLinuxV4L2();
                    if (linux.Count > 0)
                        return linux;
                }

                if (OperatingSystem.IsMacOS())
                {
                    var mac = EnumerateMacOS();
                    if (mac.Count > 0)
                        return mac;
                }

                return ProbeIndices(friendlyNames: null);
            }
            catch
            {
                return Array.Empty<VideoDeviceInfo>();
            }
        }

        private static List<VideoDeviceInfo> EnumerateLinuxV4L2()
        {
            var result = new List<VideoDeviceInfo>();
            const string sys = "/sys/class/video4linux";
            if (!Directory.Exists(sys))
                return result;

            foreach (var dir in Directory.EnumerateDirectories(sys).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir); // video0
                if (!name.StartsWith("video", StringComparison.Ordinal) ||
                    !int.TryParse(name.AsSpan("video".Length), out var index) ||
                    index < 0)
                {
                    continue;
                }

                var labelPath = Path.Combine(dir, "name");
                var friendly = File.Exists(labelPath)
                    ? File.ReadAllText(labelPath).Trim()
                    : name;
                if (string.IsNullOrWhiteSpace(friendly))
                    friendly = name;

                result.Add(new VideoDeviceInfo
                {
                    Index = index,
                    Key = VideoDeviceKey.FromIndex(index),
                    Label = $"{friendly} ({name})"
                });
            }

            return result;
        }

        /// <summary>
        /// OpenCV on macOS only exposes indices; pair them with names from
        /// <c>system_profiler SPCameraDataType</c> (same AVFoundation order in practice).
        /// </summary>
        private static List<VideoDeviceInfo> EnumerateMacOS()
        {
            var names = ReadMacCameraNames();
            return ProbeIndices(names);
        }

        private static IReadOnlyList<string> ReadMacCameraNames()
        {
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/sbin/system_profiler",
                        ArgumentList = { "SPCameraDataType", "-json" },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var json = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(8000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return Array.Empty<string>();
                }

                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                    return Array.Empty<string>();

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("SPCameraDataType", out var cameras) ||
                    cameras.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<string>();
                }

                var names = new List<string>();
                foreach (var cam in cameras.EnumerateArray())
                {
                    var name = cam.TryGetProperty("_name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    names.Add(name.Trim());
                }

                return names;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static List<VideoDeviceInfo> ProbeIndices(IReadOnlyList<string>? friendlyNames)
        {
            var result = new List<VideoDeviceInfo>();
            for (var i = 0; i <= MaxProbeIndex; i++)
            {
                try
                {
                    using var cap = new VideoCapture(i, VideoCaptureAPIs.ANY);
                    if (!cap.IsOpened())
                        continue;

                    var w = (int)cap.Get(VideoCaptureProperties.FrameWidth);
                    var h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
                    var size = (w > 0 && h > 0) ? $" {w}×{h}" : "";
                    var friendly = (friendlyNames != null && i < friendlyNames.Count)
                        ? friendlyNames[i]
                        : null;
                    var label = string.IsNullOrWhiteSpace(friendly)
                        ? $"Camera {i}{size}"
                        : $"{friendly}{size}";

                    result.Add(new VideoDeviceInfo
                    {
                        Index = i,
                        Key = VideoDeviceKey.FromIndex(i),
                        Label = label
                    });
                }
                catch
                {
                    // skip
                }
            }

            return result;
        }
    }
}
