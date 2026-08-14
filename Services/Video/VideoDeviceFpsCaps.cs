using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Frame rates the capture pin advertises. Empty = unknown (use the
    /// 15 / 30 / 60 fallback list).
    /// </summary>
    internal static class VideoDeviceFpsCaps
    {
        private static readonly ConcurrentDictionary<string, int[]> Cache =
            new(StringComparer.Ordinal);

        public static void Remember(string? deviceKey, int[]? rates)
        {
            if (rates is not { Length: > 0 } || string.IsNullOrWhiteSpace(deviceKey))
                return;
            Cache[deviceKey] = rates;
        }

        public static int[] PeekRates(string? deviceKey) =>
            !string.IsNullOrWhiteSpace(deviceKey) && Cache.TryGetValue(deviceKey, out var rates)
                ? rates
                : [];

        public static int Peek(string? deviceKey) => VideoFpsOptions.Max(PeekRates(deviceKey));

        /// <summary>
        /// Cached rates, or a fresh query. Windows Media Foundation must not
        /// open the device while the capture loop holds it
        /// (<paramref name="allowDeviceOpen"/> = false).
        /// </summary>
        public static int[] RatesFor(string? deviceKey, bool allowDeviceOpen)
        {
            var cached = PeekRates(deviceKey);
            if (cached.Length > 0)
                return cached;

            if (!VideoDeviceKey.TryResolveOpenIndex(deviceKey, out var index))
                return [];

            var rates = Query(deviceKey!, index, allowDeviceOpen);
            Remember(deviceKey, rates);
            return rates;
        }

        public static int MaxFpsFor(string? deviceKey, bool allowDeviceOpen) =>
            VideoFpsOptions.Max(RatesFor(deviceKey, allowDeviceOpen));

        private static int[] Query(string deviceKey, int index, bool allowDeviceOpen)
        {
            try
            {
                if (OperatingSystem.IsLinux())
                    return QueryLinuxRates(index);

                if (OperatingSystem.IsMacOS())
                    return QueryMacRates(index, UniqueIdFromKey(deviceKey));

                if (OperatingSystem.IsWindows() && allowDeviceOpen)
                    return QueryWindowsRates(index);
            }
            catch
            {
                // Capability probe must not break the panel.
            }

            return [];
        }

        [SupportedOSPlatform("linux")]
        private static int[] QueryLinuxRates(int index) =>
            LinuxV4l2Devices.TryQueryFpsRates(index);

        [SupportedOSPlatform("macos")]
        private static int[] QueryMacRates(int index, string? uniqueId) =>
            MacAvFoundationDevices.TryQueryFpsRates(index, uniqueId);

        [SupportedOSPlatform("windows")]
        private static int[] QueryWindowsRates(int index) =>
            WindowsMfMjpegSession.QueryFrameRates(index);

        private static string? UniqueIdFromKey(string key)
        {
            if (!key.StartsWith("uid:", StringComparison.OrdinalIgnoreCase))
                return null;
            var uid = key["uid:".Length..].Trim();
            return uid.Length > 0 ? uid : null;
        }
    }
}
