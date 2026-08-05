using PortAudioSharp;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// Enumerates PortAudio devices. Names are the stable persistence key
    /// (same approach as Voice Control's MME product names).
    /// </summary>
    public static class AudioDeviceEnumerator
    {
        private static readonly object Sync = new();
        private static bool _initialized;

        public static void EnsureInitialized()
        {
            lock (Sync)
            {
                if (_initialized) return;
                PortAudio.Initialize();
                _initialized = true;
            }
        }

        public static IReadOnlyList<AudioDeviceInfo> ListDevices()
        {
            EnsureInitialized();
            var list = new List<AudioDeviceInfo>();
            int count = PortAudio.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    DeviceInfo info = PortAudio.GetDeviceInfo(i);
                    if (string.IsNullOrWhiteSpace(info.name)) continue;
                    list.Add(new AudioDeviceInfo(
                        i,
                        info.name.Trim(),
                        info.maxInputChannels,
                        info.maxOutputChannels,
                        info.defaultSampleRate));
                }
                catch
                {
                    // Skip devices that won't describe themselves.
                }
            }
            return list;
        }

        public static IReadOnlyList<AudioDeviceInfo> ListInputs() =>
            ListDevices().Where(d => d.IsInput).ToList();

        public static IReadOnlyList<AudioDeviceInfo> ListOutputs() =>
            ListDevices().Where(d => d.IsOutput).ToList();

        /// <summary>Resolve a saved device name to a PortAudio index, or -1.</summary>
        public static int FindDeviceIndex(string? name, bool requireInput, bool requireOutput)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            EnsureInitialized();
            var devices = ListDevices();
            var match = devices.FirstOrDefault(d =>
                string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)
                && (!requireInput || d.IsInput)
                && (!requireOutput || d.IsOutput));
            return match?.Index ?? -1;
        }
    }
}
