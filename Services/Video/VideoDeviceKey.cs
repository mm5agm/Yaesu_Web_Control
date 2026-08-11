namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Persistence keys for video capture devices. Format: <c>index:N</c>.
    /// </summary>
    public static class VideoDeviceKey
    {
        public static string FromIndex(int index) => $"index:{index}";

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
    }
}
