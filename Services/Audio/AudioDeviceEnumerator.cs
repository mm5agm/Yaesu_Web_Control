using System.Runtime.InteropServices;
using System.Text;
using PortAudioSharp;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// Enumerates PortAudio devices. Persistence keys are
    /// <c>{name} [{hostApi}]</c> so Windows duplicates across MME / WASAPI /
    /// DirectSound / WDM-KS stay distinct. Legacy name-only settings still match.
    /// </summary>
    public static class AudioDeviceEnumerator
    {
        private static readonly object Sync = new();
        private static bool _initialized;

        // Prefer shared-mode / modern APIs when a legacy name matches several entries.
        private static readonly string[] PreferredHostApiSubstrings =
        {
            "WASAPI",
            "Core Audio",
            "ALSA",
            "JACK",
            "ASIO",
            "DirectSound",
            "MME",
        };

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
            var hostApiNames = LoadHostApiNames();
            var list = new List<AudioDeviceInfo>();
            int count = PortAudio.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    DeviceInfo info = PortAudio.GetDeviceInfo(i);
                    if (string.IsNullOrWhiteSpace(info.name)) continue;
                    string hostApiName = hostApiNames.TryGetValue(info.hostApi, out var n) && !string.IsNullOrEmpty(n)
                        ? n
                        : $"HostApi {info.hostApi}";
                    list.Add(new AudioDeviceInfo(
                        i,
                        info.name.Trim(),
                        hostApiName,
                        info.hostApi,
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

        /// <summary>
        /// Resolve a saved device key to a PortAudio index, or -1.
        /// Accepts <c>{name} [{hostApi}]</c> or a legacy bare name.
        /// </summary>
        public static int FindDeviceIndex(string? savedKey, bool requireInput, bool requireOutput)
        {
            if (string.IsNullOrWhiteSpace(savedKey)) return -1;
            EnsureInitialized();
            AudioDeviceKey.Parse(savedKey, out var hostApi, out var name);
            if (string.IsNullOrEmpty(name)) return -1;

            var candidates = ListDevices()
                .Where(d =>
                    string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)
                    && (!requireInput || d.IsInput)
                    && (!requireOutput || d.IsOutput))
                .ToList();

            if (candidates.Count == 0) return -1;

            if (!string.IsNullOrEmpty(hostApi))
            {
                var exact = candidates.FirstOrDefault(d =>
                    string.Equals(d.HostApiName, hostApi, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact.Index;
                // Host API from settings no longer present — fall through to name match.
            }

            if (candidates.Count == 1) return candidates[0].Index;
            return PreferHostApi(candidates).Index;
        }

        private static AudioDeviceInfo PreferHostApi(IReadOnlyList<AudioDeviceInfo> matches)
        {
            foreach (var prefer in PreferredHostApiSubstrings)
            {
                var hit = matches.FirstOrDefault(d =>
                    d.HostApiName.Contains(prefer, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return matches[0];
        }

        private static Dictionary<int, string> LoadHostApiNames()
        {
            var map = new Dictionary<int, string>();
            int count = Pa_GetHostApiCount();
            if (count <= 0) return map;
            for (int i = 0; i < count; i++)
            {
                IntPtr ptr = Pa_GetHostApiInfo(i);
                if (ptr == IntPtr.Zero) continue;
                try
                {
                    var info = Marshal.PtrToStructure<PaHostApiInfoNative>(ptr);
                    string name = ReadUtf8(info.name);
                    if (!string.IsNullOrWhiteSpace(name))
                        map[i] = name.Trim();
                }
                catch
                {
                    // Leave unnamed; caller falls back to "HostApi N".
                }
            }
            return map;
        }

        private static string ReadUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            int length = 0;
            while (Marshal.ReadByte(p, length) != 0) length++;
            if (length == 0) return "";
            var buf = new byte[length];
            Marshal.Copy(p, buf, 0, length);
            return Encoding.UTF8.GetString(buf);
        }

        // PortAudioSharp2 does not wrap Pa_GetHostApiInfo; P/Invoke the same
        // portaudio.dll it already loads (DLL name matches PortAudioSharp.Native).
        [DllImport("portaudio")]
        private static extern int Pa_GetHostApiCount();

        [DllImport("portaudio")]
        private static extern IntPtr Pa_GetHostApiInfo(int hostApi);

        [StructLayout(LayoutKind.Sequential)]
        private struct PaHostApiInfoNative
        {
            public int structVersion;
            public int type;
            public IntPtr name;
            public int deviceCount;
            public int defaultInputDevice;
            public int defaultOutputDevice;
        }
    }
}
