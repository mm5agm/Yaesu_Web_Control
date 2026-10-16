namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Persistence keys for video capture devices.
    /// <c>index:N</c> is the OpenCV index (Linux, and legacy Windows / macOS).
    /// <c>uid:…</c> names the device itself, so a reshuffle of the indexes cannot
    /// open the wrong one: an AVFoundation uniqueID on macOS (Continuity Camera),
    /// a DirectShow <c>DevicePath</c> on Windows. That matters on Windows as soon
    /// as there are two dongles of the same model — one per radio, say — because
    /// they share a friendly name and swap indexes on a replug, so an index key
    /// can silently start showing the other radio's screen.
    /// </summary>
    public static class VideoDeviceKey
    {
        public static string FromIndex(int index) => $"index:{index}";

        public static string FromUniqueId(string uniqueId) => $"uid:{uniqueId.Trim()}";

        /// <summary>
        /// Prefer the device's own identity over its index. An <c>index:N</c> key —
        /// one saved before this app knew how to ask, or picked from a list the
        /// platform gave no identity for — is upgraded the next time it is saved,
        /// because an index key can be handed a different device by a replug.
        /// Returns the key unchanged when there is nothing better to offer.
        /// </summary>
        public static string Upgrade(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "";

            key = key.Trim();
            if (key.StartsWith("uid:", StringComparison.OrdinalIgnoreCase))
                return key;

            if (!TryParseIndex(key, out var index) || !OperatingSystem.IsWindows())
                return key;

            var path = WindowsDshowDevices.TryGetDevicePath(index);
            return string.IsNullOrWhiteSpace(path) ? key : FromUniqueId(path);
        }

        public static bool IsPersistableKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            key = key.Trim();
            if (key.StartsWith("uid:", StringComparison.OrdinalIgnoreCase))
                return key.Length > 4;

            return TryParseIndex(key, out _);
        }

        public static bool TryParseIndex(string? key, out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(key))
                return false;

            key = key.Trim();
            if (key.StartsWith("index:", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(key.AsSpan("index:".Length), out index) && index >= 0;
            }

            // Legacy / bare integer
            return int.TryParse(key, out index) && index >= 0;
        }

        /// <summary>
        /// Resolve a persisted key to the OpenCV index to open now. A <c>uid:</c>
        /// key is looked up in the current device list — AVFoundation on macOS,
        /// DirectShow on Windows — and fails cleanly if that device is unplugged,
        /// which is the point: better no picture than the wrong radio's.
        /// </summary>
        public static bool TryResolveOpenIndex(string? key, out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(key))
                return false;

            key = key.Trim();
            if (key.StartsWith("uid:", StringComparison.OrdinalIgnoreCase))
            {
                var uid = key["uid:".Length..].Trim();
                if (uid.Length == 0)
                    return false;

                if (OperatingSystem.IsMacOS())
                {
                    index = MacAvFoundationDevices.IndexOfUniqueId(uid);
                    return index >= 0;
                }

                if (OperatingSystem.IsWindows())
                {
                    index = WindowsDshowDevices.IndexOfDevicePath(uid);
                    return index >= 0;
                }

                return false;
            }

            return TryParseIndex(key, out index);
        }
    }
}
