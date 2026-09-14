namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// Persistence / display key for a PortAudio device.
    /// On Windows the same USB CODEC is enumerated once per host API
    /// (MME, DirectSound, WASAPI, WDM-KS), so the key is
    /// <c>{name} [{hostApi}]</c>. Legacy settings that stored only the
    /// device name still resolve via name-only fallback.
    /// </summary>
    public static class AudioDeviceKey
    {
        public static string Format(string deviceName, string hostApiName) =>
            $"{deviceName.Trim()} [{hostApiName.Trim()}]";

        /// <summary>
        /// Parses a saved key. When the host-API suffix is present,
        /// <paramref name="hostApiName"/> is set; otherwise it is null
        /// (legacy name-only settings).
        /// </summary>
        public static void Parse(string? key, out string? hostApiName, out string deviceName)
        {
            hostApiName = null;
            deviceName = "";
            if (string.IsNullOrWhiteSpace(key)) return;

            var s = key.Trim();
            if (s.EndsWith(']'))
            {
                int open = s.LastIndexOf(" [", StringComparison.Ordinal);
                if (open > 0)
                {
                    var host = s[(open + 2)..^1].Trim();
                    var name = s[..open].Trim();
                    if (host.Length > 0 && name.Length > 0)
                    {
                        hostApiName = host;
                        deviceName = name;
                        return;
                    }
                }
            }

            deviceName = s;
        }
    }
}
