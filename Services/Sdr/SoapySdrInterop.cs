// Yaesu Web Control – SoapySDR P/Invoke Wrapper
// Thin, isolated interop layer. No UI, no SignalR, no calibration.
// All public methods are safe to call even if SoapySDR.dll is not installed —
// EnumerateDevices() returns an empty list, all others throw InvalidOperationException.

using System.Runtime.InteropServices;

namespace Yaesu_Web_Control.Services.Sdr
{
    internal static class SoapySdrInterop
    {
        private const string DllName = "SoapySDR";

        // SoapySDR direction constants
        internal const int RX = 0;

        // SoapySDR IQ sample format: complex float 32 (two interleaved floats per sample)
        internal const string CF32 = "CF32";

        // ── Native struct ────────────────────────────────────────────────────────
        // Mirrors the C struct: typedef struct { size_t size; char** keys; char** vals; } SoapySDRKwargs;
        // On x64 Windows: size_t = 8 bytes, char** = 8 bytes → struct is 24 bytes.

        [StructLayout(LayoutKind.Sequential)]
        private struct SoapySDRKwargsNative
        {
            public nuint  Size;
            public IntPtr Keys;  // char**
            public IntPtr Vals;  // char**
        }

        // ── P/Invoke declarations ────────────────────────────────────────────────

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDR_getRootPath")]
        private static extern IntPtr NativeGetRootPath();

        // Returns a char** array; free with NativeStringsClear.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDR_listSearchPaths")]
        private static extern IntPtr NativeListSearchPaths(out nuint length);

        // Returns a char** array of full module paths; free with NativeStringsClear.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDR_listModules")]
        private static extern IntPtr NativeListModules(out nuint length);

        // Frees a char** array returned by listSearchPaths / listModules.
        // C signature: void SoapySDRStrings_clear(char ***elems, size_t length)
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRStrings_clear")]
        private static extern void NativeStringsClear(ref IntPtr elems, nuint length);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_enumerate")]
        private static extern IntPtr NativeEnumerate(IntPtr args, out nuint length);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRKwargsList_clear")]
        private static extern void NativeKwargsListClear(IntPtr args, nuint length);

        // Returns a heap-allocated char* that must be freed with SoapySDR_free.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRKwargs_toString")]
        private static extern IntPtr NativeKwargsToString(IntPtr args);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDR_free")]
        private static extern void NativeFree(IntPtr ptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_makeStrArgs", CharSet = CharSet.Ansi)]
        private static extern IntPtr NativeMakeStrArgs(string args);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_unmake")]
        private static extern int NativeUnmake(IntPtr device);

        // args = NULL (IntPtr.Zero) — no extra frequency correction kwargs needed.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_setFrequency")]
        private static extern int NativeSetFrequency(
            IntPtr device, int direction, nuint channel, double frequency, IntPtr args);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_setSampleRate")]
        private static extern int NativeSetSampleRate(
            IntPtr device, int direction, nuint channel, double rate);

        // Some devices do not support AGC; we ignore the return value for this call.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_setGainMode")]
        private static extern int NativeSetGainMode(
            IntPtr device, int direction, nuint channel,
            [MarshalAs(UnmanagedType.U1)] bool automatic);

        // channels is a pointer to a size_t array; for single-channel use ref nuint.
        // errorMessage is char** — use out IntPtr and free if non-null.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_setupStream", CharSet = CharSet.Ansi)]
        private static extern IntPtr NativeSetupStream(
            IntPtr device, int direction, string format,
            ref nuint channels, nuint numChans,
            IntPtr args, out IntPtr errorMessage);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_activateStream")]
        private static extern int NativeActivateStream(
            IntPtr device, IntPtr stream, int flags, long timeNs, nuint numElems);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_deactivateStream")]
        private static extern int NativeDeactivateStream(
            IntPtr device, IntPtr stream, int flags, long timeNs);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_closeStream")]
        private static extern int NativeCloseStream(IntPtr device, IntPtr stream);

        // buffs = void* const* (array of per-channel buffer pointers).
        // numElems = number of IQ samples (each CF32 sample = 2 floats = 8 bytes).
        // timeoutUs: C `long` = 32-bit on Windows x64 MSVC.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SoapySDRDevice_readStream")]
        private static extern int NativeReadStream(
            IntPtr device, IntPtr stream, IntPtr buffs,
            nuint numElems, ref int flags, ref long timeNs, int timeoutUs);

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all detected SoapySDR devices.
        /// Throws <see cref="DllNotFoundException"/> when SoapySDR.dll (or one of its
        /// own dependencies) cannot be loaded — the caller decides how to report it.
        /// </summary>
        public static IReadOnlyList<SdrDeviceInfo> EnumerateDevices()
        {
            var devices = new List<SdrDeviceInfo>();

            IntPtr listPtr = NativeEnumerate(IntPtr.Zero, out nuint count);
            if (listPtr == IntPtr.Zero || count == 0)
                return devices;

            int kwargsSize = Marshal.SizeOf<SoapySDRKwargsNative>();

            for (nuint i = 0; i < count; i++)
            {
                IntPtr itemPtr = listPtr + (int)((ulong)i * (ulong)kwargsSize);
                IntPtr strPtr  = NativeKwargsToString(itemPtr);
                string kwargs  = Marshal.PtrToStringAnsi(strPtr) ?? string.Empty;
                NativeFree(strPtr);

                var pairs     = ParseKwargs(kwargs);
                string driver = pairs.GetValueOrDefault("driver", "unknown");
                string label  = pairs.GetValueOrDefault("label",  kwargs);

                devices.Add(new SdrDeviceInfo(Key: kwargs, Label: label, Driver: driver));
            }

            NativeKwargsListClear(listPtr, count);
            return devices;
        }

        /// <summary>Opens a device identified by its kwargs string.</summary>
        /// <exception cref="InvalidOperationException">Device could not be opened.</exception>
        public static IntPtr OpenDevice(string key)
        {
            IntPtr device = NativeMakeStrArgs(key);
            if (device == IntPtr.Zero)
                throw new InvalidOperationException($"SoapySDR: could not open device '{key}'");
            return device;
        }

        /// <summary>Closes a previously opened device. Safe to call with IntPtr.Zero.</summary>
        public static void CloseDevice(IntPtr device)
        {
            if (device != IntPtr.Zero)
                NativeUnmake(device);
        }

        /// <summary>Sets centre frequency, sample rate, and enables AGC on RX channel 0.</summary>
        public static void ConfigureRxChannel(IntPtr device, long frequencyHz, double sampleRateHz)
        {
            int ret = NativeSetFrequency(device, RX, 0, frequencyHz, IntPtr.Zero);
            if (ret != 0)
                throw new InvalidOperationException($"SoapySDR: SetFrequency failed (code {ret})");

            ret = NativeSetSampleRate(device, RX, 0, sampleRateHz);
            if (ret != 0)
                throw new InvalidOperationException($"SoapySDR: SetSampleRate failed (code {ret})");

            // AGC is best-effort; some devices do not support it.
            NativeSetGainMode(device, RX, 0, automatic: true);
        }

        /// <summary>Creates and activates an RX stream in CF32 format on channel 0.</summary>
        public static IntPtr OpenRxStream(IntPtr device)
        {
            nuint channel = 0;
            IntPtr stream = NativeSetupStream(
                device, RX, CF32, ref channel, 1, IntPtr.Zero, out IntPtr errorMsg);

            if (errorMsg != IntPtr.Zero)
            {
                string? msg = Marshal.PtrToStringAnsi(errorMsg);
                NativeFree(errorMsg);
                throw new InvalidOperationException($"SoapySDR: setupStream — {msg}");
            }

            if (stream == IntPtr.Zero)
                throw new InvalidOperationException("SoapySDR: setupStream returned null stream");

            int ret = NativeActivateStream(device, stream, 0, 0, 0);
            if (ret != 0)
                throw new InvalidOperationException($"SoapySDR: activateStream failed (code {ret})");

            return stream;
        }

        /// <summary>
        /// Reads IQ samples from the stream into <paramref name="buffer"/>.
        /// Buffer must be sized <paramref name="numSamples"/> × 2 floats (I, Q interleaved).
        /// Returns the number of samples read; negative values are SoapySDR error codes.
        /// </summary>
        public static int ReadIqSamples(
            IntPtr device, IntPtr stream,
            float[] buffer, int numSamples,
            int timeoutUs = 100_000)
        {
            var sampleHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr samplePtr = sampleHandle.AddrOfPinnedObject();

                // buffs = array of one channel pointer; pin the pointer array too.
                IntPtr[] bufPtrArray = [samplePtr];
                var bufArrayHandle = GCHandle.Alloc(bufPtrArray, GCHandleType.Pinned);
                try
                {
                    int  flags  = 0;
                    long timeNs = 0;
                    return NativeReadStream(
                        device, stream,
                        bufArrayHandle.AddrOfPinnedObject(),
                        (nuint)numSamples,
                        ref flags, ref timeNs,
                        timeoutUs);
                }
                finally { bufArrayHandle.Free(); }
            }
            finally { sampleHandle.Free(); }
        }

        /// <summary>Deactivates and closes a stream. Safe to call with IntPtr.Zero.</summary>
        public static void CloseRxStream(IntPtr device, IntPtr stream)
        {
            if (stream == IntPtr.Zero) return;
            NativeDeactivateStream(device, stream, 0, 0);
            NativeCloseStream(device, stream);
        }

        /// <summary>
        /// Returns a plain-English summary of where SoapySDR is searching for
        /// device plugins and which plugins it actually found.  Useful for
        /// diagnosing "no devices found" when the core DLL loads correctly.
        /// </summary>
        public static string GetPluginDiagnostics()
        {
            try
            {
                var sb = new System.Text.StringBuilder();

                string root = Marshal.PtrToStringAnsi(NativeGetRootPath()) ?? "(unknown)";
                sb.AppendLine($"SoapySDR root: {root}");

                IntPtr searchPtr = NativeListSearchPaths(out nuint searchCount);
                var searchPaths  = ReadStringArray(searchPtr, searchCount);
                NativeStringsClear(ref searchPtr, searchCount);
                sb.AppendLine(searchPaths.Length == 0
                    ? "Plugin search paths: (none)"
                    : $"Plugin search paths: {string.Join(", ", searchPaths)}");

                IntPtr modPtr = NativeListModules(out nuint modCount);
                var modules   = ReadStringArray(modPtr, modCount);
                NativeStringsClear(ref modPtr, modCount);
                sb.AppendLine(modules.Length == 0
                    ? "Modules found: (none — copy SoapySDRPlay3.dll into one of the search paths above)"
                    : $"Modules found: {string.Join(", ", modules.Select(System.IO.Path.GetFileName))}");

                sb.AppendLine(DescribeLoadedNativeModules());

                return sb.ToString().Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Diagnostic error: {ex.Message}";
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        // Reports the on-disk path each SDR-related native DLL was actually
        // loaded from. This is the decisive check for "the dongle works in other
        // SDR apps but this one finds nothing": SoapySDRDevice_enumerate probes
        // every backend, so by the time this runs the rtlsdr module has already
        // pulled in librtlsdr.dll + libusb-1.0.dll — if either resolves to a copy
        // OUTSIDE the application folder, another SDR install on the PATH has
        // shadowed the ones we ship, and an incompatible build silently
        // enumerates zero devices. Equally telling is librtlsdr.dll *not*
        // appearing at all after an enumerate — that means the rtlsdr backend
        // failed to load its dependency.
        private static string DescribeLoadedNativeModules()
        {
            try
            {
                // File names (without extension) of the SoapySDR core, the
                // per-radio backend modules, and the libraries they depend on.
                string[] wanted =
                {
                    "soapysdr", "rtlsdrsupport", "librtlsdr", "libusb-1.0",
                    "airspysupport", "airspy", "hackrfsupport", "hackrf",
                };

                using var me = System.Diagnostics.Process.GetCurrentProcess();
                var loaded = me.Modules
                    .Cast<System.Diagnostics.ProcessModule>()
                    .Where(m => wanted.Contains(
                        System.IO.Path.GetFileNameWithoutExtension(m.ModuleName),
                        StringComparer.OrdinalIgnoreCase))
                    .Select(m => $"{m.ModuleName} <- {m.FileName}")
                    .Distinct()
                    .ToArray();

                return loaded.Length == 0
                    ? "Loaded native SDR DLLs: (none — no backend module has been loaded)"
                    : "Loaded native SDR DLLs: " + string.Join(" | ", loaded);
            }
            catch (Exception ex)
            {
                return $"Loaded native SDR DLLs: (could not enumerate — {ex.Message})";
            }
        }

        // Reads a native char** array into a managed string[].
        private static string[] ReadStringArray(IntPtr ptr, nuint count)
        {
            if (ptr == IntPtr.Zero || count == 0) return [];
            var result = new string[(int)count];
            for (int i = 0; i < (int)count; i++)
            {
                IntPtr strPtr = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                result[i] = Marshal.PtrToStringAnsi(strPtr) ?? string.Empty;
            }
            return result;
        }

        // Parses "key=val,key=val" into a case-insensitive dictionary.
        private static Dictionary<string, string> ParseKwargs(string kwargs)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in kwargs.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                    result[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
            }
            return result;
        }
    }
}
