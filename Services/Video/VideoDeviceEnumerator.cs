using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Lists USB webcams / HDMI capture dongles exposed to OpenCV.
    /// Uses the same backend for naming and probing that capture uses, so
    /// dropdown labels stay aligned with the device that actually opens.
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
                    return EnumerateMacOS();

                if (OperatingSystem.IsWindows())
                    return ProbeIndices(VideoCaptureAPIs.DSHOW, ReadEmptyNames());

                return ProbeIndices(VideoCaptureAPIs.ANY, ReadEmptyNames());
            }
            catch
            {
                return Array.Empty<VideoDeviceInfo>();
            }
        }

        private static IReadOnlyDictionary<int, string> ReadEmptyNames() =>
            new Dictionary<int, string>();

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
        /// macOS: name sources prefer ffmpeg AVFoundation (same index space as
        /// OpenCV CAP_AVFOUNDATION), then system_profiler. Probe with AVFOUNDATION
        /// only — never CAP_ANY — so labels match what <see cref="VideoCaptureService"/> opens.
        /// </summary>
        private static List<VideoDeviceInfo> EnumerateMacOS()
        {
            var names = ReadFfmpegAvFoundationNames();
            if (names.Count == 0)
                names = ReadMacSystemProfilerNames();
            return ProbeIndices(VideoCaptureAPIs.AVFOUNDATION, names);
        }

        /// <summary>
        /// Parse <c>ffmpeg -f avfoundation -list_devices</c> video lines:
        /// <c>[0] FaceTime HD Camera</c>. Indices match OpenCV AVFoundation.
        /// </summary>
        private static IReadOnlyDictionary<int, string> ReadFfmpegAvFoundationNames()
        {
            var ffmpeg = FindOnPath("ffmpeg");
            if (ffmpeg is null)
                return ReadEmptyNames();

            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        ArgumentList = { "-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", "" },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                // ffmpeg prints the device list to stderr
                var stderr = proc.StandardError.ReadToEnd();
                _ = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(6000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return ReadEmptyNames();
                }

                var map = new Dictionary<int, string>();
                var inVideo = false;
                foreach (var raw in stderr.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Contains("AVFoundation video devices", StringComparison.OrdinalIgnoreCase))
                    {
                        inVideo = true;
                        continue;
                    }
                    if (line.Contains("AVFoundation audio devices", StringComparison.OrdinalIgnoreCase))
                        break;
                    if (!inVideo)
                        continue;

                    // [AVFoundation …] [0] FaceTime HD Camera
                    var m = Regex.Match(line, @"\[(\d+)\]\s+(.+?)\s*$");
                    if (!m.Success)
                        continue;
                    if (!int.TryParse(m.Groups[1].Value, out var idx))
                        continue;
                    var name = m.Groups[2].Value.Trim();
                    if (name.Length == 0)
                        continue;
                    // Skip screen-capture entries OpenCV usually cannot open as cameras
                    if (name.Contains("Capture screen", StringComparison.OrdinalIgnoreCase))
                        continue;
                    map[idx] = name;
                }

                return map;
            }
            catch
            {
                return ReadEmptyNames();
            }
        }

        private static IReadOnlyDictionary<int, string> ReadMacSystemProfilerNames()
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
                    return ReadEmptyNames();
                }

                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                    return ReadEmptyNames();

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("SPCameraDataType", out var cameras) ||
                    cameras.ValueKind != JsonValueKind.Array)
                {
                    return ReadEmptyNames();
                }

                // system_profiler order is NOT guaranteed to match AVFoundation.
                // Keep as ordinal fallback only; labels still include (#N).
                var map = new Dictionary<int, string>();
                var i = 0;
                foreach (var cam in cameras.EnumerateArray())
                {
                    var name = cam.TryGetProperty("_name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    map[i++] = name.Trim();
                }

                return map;
            }
            catch
            {
                return ReadEmptyNames();
            }
        }

        private static List<VideoDeviceInfo> ProbeIndices(
            VideoCaptureAPIs api,
            IReadOnlyDictionary<int, string> friendlyNames)
        {
            var result = new List<VideoDeviceInfo>();
            for (var i = 0; i <= MaxProbeIndex; i++)
            {
                try
                {
                    using var cap = new VideoCapture(i, api);
                    if (!cap.IsOpened())
                        continue;

                    var w = (int)cap.Get(VideoCaptureProperties.FrameWidth);
                    var h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
                    var size = (w > 0 && h > 0) ? $" {w}×{h}" : "";
                    friendlyNames.TryGetValue(i, out var friendly);
                    // Always include the OpenCV index so a mismatched name is obvious.
                    var label = string.IsNullOrWhiteSpace(friendly)
                        ? $"Camera {i}{size}"
                        : $"{friendly}{size}  (#{i})";

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

        private static string? FindOnPath(string fileName)
        {
            if (File.Exists(fileName))
                return fileName;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // skip
                }
            }

            // Common Homebrew locations when PATH is minimal (launchd / tray apps).
            foreach (var candidate in new[]
                     {
                         "/opt/homebrew/bin/ffmpeg",
                         "/usr/local/bin/ffmpeg"
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
