using System.Runtime.InteropServices;
using NAudio;
using NAudio.Wave;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Playback-device selection helper for the spoken confirmation announcements.
    ///
    /// <para>
    /// <see cref="System.Speech.Synthesis.SpeechSynthesizer"/> can only send its
    /// audio to the Windows <em>default</em> playback device
    /// (<c>SetOutputToDefaultAudioDevice</c>) or to a
    /// <see cref="Stream"/> (<c>SetOutputToWaveStream</c>) — it has no API to
    /// target a device by name or index. So to let the operator send
    /// confirmations to a specific speaker/headset (while their Windows default
    /// stays pointed at WSJT-X, rig audio, etc.), <see cref="VoiceTtsService"/>
    /// renders the phrase to a WAV in memory and plays it to the chosen WaveOut
    /// device itself. This mirrors the input side (see <see cref="MicrophoneCapture"/>).
    /// </para>
    ///
    /// <para>
    /// Device <em>names</em> are the persisted key rather than indices, because a
    /// device's WaveOut index shifts as other devices are plugged/unplugged,
    /// whereas the name the user picked is stable. Names are the MME product
    /// names — truncated to 31 chars by the OS, consistently, so they remain a
    /// stable match key (same behaviour as the mic list).
    /// </para>
    /// </summary>
    public static class AudioOutput
    {
        public sealed record OutputDevice(int Index, string Name);

        // NAudio.WinMM 2.2.1 ships WaveOutEvent (playback) but not the legacy
        // WaveOut class that carries the static DeviceCount / GetCapabilities
        // enumeration (unlike the input side, where WaveInEvent has them). So we
        // enumerate playback devices via the low-level MME P/Invoke that
        // WaveOut.GetCapabilities would have called internally.
        private static bool TryGetCaps(int index, out WaveOutCapabilities caps)
        {
            caps = new WaveOutCapabilities();
            int size = Marshal.SizeOf(caps);
            return WaveInterop.waveOutGetDevCaps((IntPtr)index, out caps, size) == MmResult.NoError;
        }

        /// <summary>
        /// Enumerate the WaveOut (playback) devices Windows exposes right now.
        /// The WAVE_MAPPER default device (index -1) is not listed — an empty
        /// selection already represents "Windows default" in the picker.
        /// </summary>
        public static IReadOnlyList<OutputDevice> ListOutputDevices()
        {
            var list = new List<OutputDevice>();
            int count = WaveInterop.waveOutGetNumDevs();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    if (TryGetCaps(i, out var caps) && !string.IsNullOrWhiteSpace(caps.ProductName))
                        list.Add(new OutputDevice(i, Normalize(caps.ProductName)));
                }
                catch
                {
                    // A device that won't describe itself is skipped rather
                    // than aborting the whole enumeration.
                }
            }
            return list;
        }

        // MME product names are a fixed 32-char buffer, so long names arrive
        // truncated at 31 chars — sometimes ending on a space (e.g. the Philips
        // monitor's "1 - PHL 40B1U5600 (2- AMD High "). That trailing space is
        // fragile: it survives NAudio's marshalling but gets stripped somewhere
        // in the browser → JSON → settings round-trip, so the saved name no
        // longer equals the enumerated one and an exact match fails. Trimming the
        // trailing whitespace on both the listed and the compared name makes the
        // key stable regardless of where the space was lost.
        private static string Normalize(string name) => name.TrimEnd();

        /// <summary>
        /// Resolve a saved device <em>name</em> to its current WaveOut index.
        /// Returns -1 when the name is empty or the device is no longer present
        /// — the caller then falls back to the Windows default (WAVE_MAPPER,
        /// which NAudio also represents as device number -1).
        /// </summary>
        public static int FindDeviceIndex(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            var target = Normalize(name);
            int count = WaveInterop.waveOutGetNumDevs();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    if (TryGetCaps(i, out var caps) &&
                        string.Equals(Normalize(caps.ProductName), target, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
                catch
                {
                    // Ignore an un-describable device and keep looking.
                }
            }
            return -1;
        }
    }
}
