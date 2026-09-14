namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Persistence keys for video capture devices.
    /// <c>index:N</c> is the OpenCV index (Windows / Linux, and legacy macOS).
    /// <c>uid:…</c> is an AVFoundation uniqueID (macOS) so Continuity Camera
    /// reshuffles do not open the wrong device.
    /// </summary>
    public static class VideoDeviceKey
    {
        public static string FromIndex(int index) => $"index:{index}";

        public static string FromUniqueId(string uniqueId) => $"uid:{uniqueId.Trim()}";

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
        /// Resolve a persisted key to the OpenCV index to open now.
        /// macOS <c>uid:</c> keys are looked up in the current AVFoundation list.
        /// </summary>
        public static bool TryResolveOpenIndex(string? key, out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(key))
                return false;

            key = key.Trim();
            if (key.StartsWith("uid:", StringComparison.OrdinalIgnoreCase))
            {
                if (!OperatingSystem.IsMacOS())
                    return false;

                var uid = key["uid:".Length..].Trim();
                if (uid.Length == 0)
                    return false;

                index = MacAvFoundationDevices.IndexOfUniqueId(uid);
                return index >= 0;
            }

            return TryParseIndex(key, out index);
        }
    }
}
