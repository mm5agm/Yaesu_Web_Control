using System.Collections.Concurrent;
using System.Runtime.Versioning;

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

        public static void Remember(string? deviceKey, string[]? sizes)
        {
            if (sizes is not { Length: > 0 } || string.IsNullOrWhiteSpace(deviceKey))
                return;
            Cache[deviceKey] = sizes;
        }

        /// <summary>
        /// Record the sizes a session actually enumerated while opening. This
        /// is the exact list for the backend that will do the capturing, so it
        /// is preferred over any probe.
        /// </summary>
        public static void RememberPins(string? deviceKey, IEnumerable<VideoCapturePinRank.Pin> pins) =>
            Remember(deviceKey, VideoSizeOptions.FromPins(pins));

        public static string[] PeekSizes(string? deviceKey) =>
            !string.IsNullOrWhiteSpace(deviceKey) && Cache.TryGetValue(deviceKey, out var sizes)
                ? sizes
                : [];

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
