using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// MJPEG capture sizes the device advertises, as "WxH". Empty = unknown,
    /// which the panel shows as Auto-only. Mirrors
    /// <see cref="VideoDeviceFpsCaps"/>.
    /// </summary>
    internal static class VideoDeviceSizeCaps
    {
        private static readonly ConcurrentDictionary<string, string[]> Cache =
            new(StringComparer.Ordinal);

        private static readonly object FileLock = new();
        private static bool _loaded;

        /// <summary>Tests point this at a temp file; null = the app data folder.</summary>
        internal static string? FilePathOverride { get; set; }

        /// <summary>
        /// The sizes are kept on disk as well (#175). Only a real capture
        /// session fills the cache, because opening the dongle a second time
        /// from Status is what #173 stopped. Without the file, a fresh start
        /// offered Auto only until a stream had run. A size saved for a
        /// device that has since changed does no harm: the session falls
        /// back to Auto and saves the real list.
        /// </summary>
        internal static string FilePath => FilePathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "video-capture-sizes.json");

        public static void Remember(string? deviceKey, string[]? sizes)
        {
            if (sizes is not { Length: > 0 } || string.IsNullOrWhiteSpace(deviceKey))
                return;
            EnsureLoaded();
            if (Cache.TryGetValue(deviceKey, out var old) && old.SequenceEqual(sizes, StringComparer.Ordinal))
                return;
            Cache[deviceKey] = sizes;
            Save();
        }

        /// <summary>Forget everything in memory, so the next read loads the file again (tests).</summary>
        internal static void ResetForTests()
        {
            lock (FileLock)
            {
                Cache.Clear();
                _loaded = false;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;
            lock (FileLock)
            {
                if (_loaded)
                    return;
                _loaded = true;
                try
                {
                    var path = FilePath;
                    if (!File.Exists(path))
                        return;
                    var saved = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path));
                    if (saved is null)
                        return;
                    foreach (var (key, sizes) in saved)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && sizes is { Length: > 0 })
                            Cache.TryAdd(key, sizes);
                    }
                }
                catch
                {
                    // A damaged file only means Auto until a stream runs.
                }
            }
        }

        private static void Save()
        {
            lock (FileLock)
            {
                try
                {
                    var path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var snapshot = Cache.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(tmp, path, overwrite: true);
                }
                catch
                {
                    // Not being able to save the list must not break capture.
                }
            }
        }

        /// <summary>
        /// Record the sizes a session actually enumerated while opening. This
        /// is the exact list for the backend that will do the capturing, so it
        /// is preferred over any probe.
        /// </summary>
        public static void RememberPins(string? deviceKey, IEnumerable<VideoCapturePinRank.Pin> pins) =>
            Remember(deviceKey, VideoSizeOptions.FromPins(pins));

        public static string[] PeekSizes(string? deviceKey)
        {
            if (string.IsNullOrWhiteSpace(deviceKey))
                return [];
            EnsureLoaded();
            return Cache.TryGetValue(deviceKey, out var sizes) ? sizes : [];
        }

        /// <summary>
        /// Cached sizes, or a fresh probe. Windows Media Foundation must not
        /// open the device while the capture loop holds it
        /// (<paramref name="allowDeviceOpen"/> = false) — the cache filled by
        /// <see cref="RememberPins"/> on the last open covers that case.
        /// </summary>
        public static string[] SizesFor(string? deviceKey, bool allowDeviceOpen)
        {
            var cached = PeekSizes(deviceKey);
            if (cached.Length > 0)
                return cached;

            if (!VideoDeviceKey.TryResolveOpenIndex(deviceKey, out var index))
                return [];

            var sizes = Query(index, allowDeviceOpen);
            Remember(deviceKey, sizes);
            return sizes;
        }

        private static string[] Query(int index, bool allowDeviceOpen)
        {
            try
            {
                if (OperatingSystem.IsLinux())
                    return VideoSizeOptions.FromPins(QueryLinuxPins(index));

                if (OperatingSystem.IsWindows() && allowDeviceOpen)
                    return VideoSizeOptions.FromPins(QueryWindowsPins(index));

                // macOS goes through OpenCV/AVFoundation, which does not hand
                // back a pin list here. Auto only until a session caches one.
            }
            catch
            {
                // Capability probe must not break the panel.
            }

            return [];
        }

        [SupportedOSPlatform("linux")]
        private static List<VideoCapturePinRank.Pin> QueryLinuxPins(int index) =>
            LinuxV4l2Devices.TryQueryPins(index);

        [SupportedOSPlatform("windows")]
        private static List<VideoCapturePinRank.Pin> QueryWindowsPins(int index) =>
            WindowsMfMjpegSession.QueryPins(index);
    }
}
