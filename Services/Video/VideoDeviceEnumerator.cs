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
        private static readonly object CacheLock = new();
        private static IReadOnlyList<VideoDeviceInfo> _cache = Array.Empty<VideoDeviceInfo>();

        /// <summary>
        /// List capture devices. When <paramref name="allowProbe"/> is false
        /// (capture loop already holds a UVC device), return the last probe
        /// result instead of opening cameras — a second Open on the live
        /// HDMI dongle native-crashes the host process.
        /// </summary>
        public static IReadOnlyList<VideoDeviceInfo> ListDevices(bool allowProbe = true)
        {
            if (!allowProbe)
            {
                // These paths do not Open() the live capture graph.
                if (OperatingSystem.IsLinux())
                {
                    var linux = EnumerateLinuxV4L2();
                    if (linux.Count > 0)
                    {
                        lock (CacheLock)
                            _cache = linux;
                        return linux;
                    }
                }

                if (OperatingSystem.IsWindows())
                {
                    var named = FromFriendlyNames(ReadWindowsFriendlyNames());
                    if (named.Count > 0)
                    {
                        lock (CacheLock)
                            _cache = named;
                        return named;
                    }
                }

                if (OperatingSystem.IsMacOS())
                {
                    var names = ReadMacFriendlyNames();
                    if (names.Count > 0)
                    {
                        var named = FromFriendlyNames(names);
                        lock (CacheLock)
                            _cache = named;
                        return named;
                    }
                }

                lock (CacheLock)
                    return _cache;
            }

            try
            {
                IReadOnlyList<VideoDeviceInfo> list;
                if (OperatingSystem.IsLinux())
                {
                    var linux = EnumerateLinuxV4L2();
                    list = linux.Count > 0 ? linux : ProbeIndices(VideoCaptureAPIs.ANY, ReadEmptyNames());
                }
                else if (OperatingSystem.IsMacOS())
                {
                    list = EnumerateMacOS();
                }
                else if (OperatingSystem.IsWindows())
                {
                    list = EnumerateWindows();
                }
                else
                {
                    list = ProbeIndices(VideoCaptureAPIs.ANY, ReadEmptyNames());
                }

                lock (CacheLock)
                    _cache = list;
                return list;
            }
            catch
            {
                lock (CacheLock)
                    return _cache;
            }
        }

        private static IReadOnlyDictionary<int, string> ReadEmptyNames() =>
            new Dictionary<int, string>();

        /// <summary>
        /// DirectShow names are the OpenCV CAP_DSHOW index space. Probe only
        /// those indexes for a resolution suffix; if probe fails, still list
        /// the named device so HDMI dongles are not shown as "Camera 0".
        /// </summary>
        private static List<VideoDeviceInfo> EnumerateWindows()
        {
            var names = ReadWindowsFriendlyNames();
            if (names.Count == 0)
                return ProbeIndices(VideoCaptureAPIs.DSHOW, ReadEmptyNames());

            var probed = ProbeNamedIndices(VideoCaptureAPIs.DSHOW, names);
            return MergeUnprobedNames(probed, names);
        }

        /// <summary>
        /// DirectShow COM first (STA), then ffmpeg <c>-f dshow -list_devices</c>
        /// if COM returned no monikers — same index space as OpenCV CAP_DSHOW.
        /// </summary>
        private static IReadOnlyDictionary<int, string> ReadWindowsFriendlyNames()
        {
            var names = WindowsDshowDevices.ListFriendlyNames();
            if (names.Count > 0)
                return names;
            return ReadFfmpegDshowNames();
        }

        /// <summary>
        /// Parse <c>ffmpeg -f dshow -list_devices</c> video lines:
        /// <c>"USB Video"</c>. Indices match OpenCV DirectShow order.
        /// </summary>
        private static IReadOnlyDictionary<int, string> ReadFfmpegDshowNames()
        {
            var ffmpeg = FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
            if (ffmpeg is null)
                return ReadEmptyNames();

            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        ArgumentList = { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var stderr = proc.StandardError.ReadToEnd();
                _ = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(6000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return ReadEmptyNames();
                }

                var map = new Dictionary<int, string>();
                var inVideo = false;
                var index = 0;
                foreach (var raw in stderr.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
                    {
                        inVideo = true;
                        continue;
                    }
                    if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
                        break;
                    if (!inVideo)
                        continue;
                    if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var m = Regex.Match(line, "\"([^\"]+)\"");
                    if (!m.Success)
                        continue;
                    var name = m.Groups[1].Value.Trim();
                    if (name.Length == 0)
                        continue;
                    map[index++] = name;
                }

                return map;
            }
            catch
            {
                return ReadEmptyNames();
            }
        }

        private static List<VideoDeviceInfo> FromFriendlyNames(IReadOnlyDictionary<int, string> names)
        {
            var collisions = CollidingNames(names);
            var result = new List<VideoDeviceInfo>();
            foreach (var kv in names.OrderBy(k => k.Key))
            {
                if (string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                result.Add(new VideoDeviceInfo
                {
                    Index = kv.Key,
                    Key = VideoDeviceKey.FromIndex(kv.Key),
                    Label = FormatLabel(kv.Key, kv.Value, 0, 0, collisions.Contains(kv.Value.Trim()))
                });
            }
            return result;
        }

        private static List<VideoDeviceInfo> MergeUnprobedNames(
            IReadOnlyList<VideoDeviceInfo> probed,
            IReadOnlyDictionary<int, string> names)
        {
            var byIndex = probed.ToDictionary(d => d.Index);
            var collisions = CollidingNames(names);
            var result = new List<VideoDeviceInfo>();
            var max = -1;
            if (byIndex.Count > 0)
                max = Math.Max(max, byIndex.Keys.Max());
            if (names.Count > 0)
                max = Math.Max(max, names.Keys.Max());

            for (var i = 0; i <= max; i++)
            {
                if (byIndex.TryGetValue(i, out var existing))
                {
                    result.Add(existing);
                    continue;
                }

                if (!names.TryGetValue(i, out var friendly) || string.IsNullOrWhiteSpace(friendly))
                    continue;

                result.Add(new VideoDeviceInfo
                {
                    Index = i,
                    Key = VideoDeviceKey.FromIndex(i),
                    Label = FormatLabel(i, friendly, 0, 0, collisions.Contains(friendly.Trim()))
                });
            }

            return result;
        }

        private static HashSet<string> CollidingNames(IReadOnlyDictionary<int, string> names) =>
            names.Values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .GroupBy(v => v.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static string FormatLabel(int index, string? friendly, int width, int height, bool nameCollision)
        {
            var name = string.IsNullOrWhiteSpace(friendly) ? $"Camera {index}" : friendly.Trim();
            if (nameCollision)
                name = $"{name} (#{index})";
            if (width > 0 && height > 0)
                return $"{name}  {width}×{height}";
            return name;
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

                // Sysfs lists metadata + Pi codec nodes that OpenCV cannot open,
                // and Docker often maps only one /dev/videoN while sysfs still
                // shows the rest of the host.
                if (OperatingSystem.IsLinux() && !LinuxV4l2Devices.ShouldList(index, friendly))
                    continue;

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
        /// OpenCV CAP_AVFOUNDATION), then system_profiler. Do <strong>not</strong>
        /// probe-open here — AVFoundation Open/Read from a request thread hangs
        /// without an AppKit run loop and races the capture service. Opening is
        /// done once on the UI thread inside <see cref="VideoCaptureService"/>.
        /// </summary>
        private static List<VideoDeviceInfo> EnumerateMacOS()
        {
            var names = ReadMacFriendlyNames();
            if (names.Count > 0)
                return FromFriendlyNames(names);

            // No names available — last resort probe (may be empty under TCC).
            return ProbeIndices(VideoCaptureAPIs.AVFOUNDATION, names);
        }

        private static IReadOnlyDictionary<int, string> ReadMacFriendlyNames()
        {
            var names = ReadFfmpegAvFoundationNames();
            if (names.Count > 0)
                return names;
            return ReadMacSystemProfilerNames();
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

        private static List<VideoDeviceInfo> ProbeNamedIndices(
            VideoCaptureAPIs api,
            IReadOnlyDictionary<int, string> friendlyNames)
        {
            var collisions = CollidingNames(friendlyNames);
            var result = new List<VideoDeviceInfo>();
            foreach (var index in friendlyNames.Keys.OrderBy(k => k))
            {
                if (index < 0 || index > MaxProbeIndex)
                    continue;
                TryProbeOne(api, index, friendlyNames, collisions, result);
            }

            return result;
        }

        private static List<VideoDeviceInfo> ProbeIndices(
            VideoCaptureAPIs api,
            IReadOnlyDictionary<int, string> friendlyNames)
        {
            var collisions = CollidingNames(friendlyNames);
            var result = new List<VideoDeviceInfo>();
            for (var i = 0; i <= MaxProbeIndex; i++)
                TryProbeOne(api, i, friendlyNames, collisions, result);
            return result;
        }

        private static void TryProbeOne(
            VideoCaptureAPIs api,
            int index,
            IReadOnlyDictionary<int, string> friendlyNames,
            HashSet<string> collisions,
            List<VideoDeviceInfo> result)
        {
            try
            {
                using var cap = new VideoCapture(index, api);
                if (!cap.IsOpened())
                    return;

                var w = (int)cap.Get(VideoCaptureProperties.FrameWidth);
                var h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
                friendlyNames.TryGetValue(index, out var friendly);
                var collision = !string.IsNullOrWhiteSpace(friendly) && collisions.Contains(friendly.Trim());

                result.Add(new VideoDeviceInfo
                {
                    Index = index,
                    Key = VideoDeviceKey.FromIndex(index),
                    Label = FormatLabel(index, friendly, w, h, collision)
                });
            }
            catch
            {
                // skip
            }
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

            // Common locations when PATH is minimal (launchd / tray / Windows).
            foreach (var candidate in new[]
                     {
                         "/opt/homebrew/bin/ffmpeg",
                         "/usr/local/bin/ffmpeg",
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                         @"C:\ffmpeg\bin\ffmpeg.exe"
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
