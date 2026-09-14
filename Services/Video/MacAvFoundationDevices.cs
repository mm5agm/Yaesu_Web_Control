using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Lists AVFoundation cameras in the same index space OpenCV
    /// <c>CAP_AVFOUNDATION</c> uses (video + muxed devices, then sort by
    /// <c>uniqueID</c>). ffmpeg / system_profiler order does not match that,
    /// which is why picking "FaceTime HD Camera" could open Continuity Camera.
    /// Enumeration never opens a capture session.
    /// </summary>
    internal static class MacAvFoundationDevices
    {
        internal readonly record struct Device(int Index, string UniqueId, string LocalizedName, int[] Rates)
        {
            public int MaxFps => VideoFpsOptions.Max(Rates);
        }

        private static readonly object Sync = new();
        private static bool _frameworkLoaded;

        public static IReadOnlyList<Device> List()
        {
            if (!OperatingSystem.IsMacOS())
                return Array.Empty<Device>();

            lock (Sync)
            {
                try
                {
                    EnsureFrameworkLoaded();
                    return ListCore();
                }
                catch
                {
                    return Array.Empty<Device>();
                }
            }
        }

        /// <summary>First OpenCV index whose uniqueID matches, or -1.</summary>
        public static int IndexOfUniqueId(string? uniqueId)
        {
            if (string.IsNullOrWhiteSpace(uniqueId))
                return -1;

            var want = uniqueId.Trim();
            foreach (var d in List())
            {
                if (string.Equals(d.UniqueId, want, StringComparison.Ordinal))
                    return d.Index;
            }

            return -1;
        }

        /// <summary>
        /// One row per uniqueID (first OpenCV index). Muxed+video duplicates
        /// stay in the raw OpenCV list for index fidelity, but the dropdown
        /// should not show the same camera twice.
        /// </summary>
        public static IReadOnlyList<Device> ListDistinct()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<Device>();
            foreach (var d in List())
            {
                if (string.IsNullOrWhiteSpace(d.UniqueId) || !seen.Add(d.UniqueId))
                    continue;
                result.Add(d);
            }

            return result;
        }

        [SupportedOSPlatform("macos")]
        private static void EnsureFrameworkLoaded()
        {
            if (_frameworkLoaded)
                return;

            NativeLibrary.TryLoad(
                "/System/Library/Frameworks/AVFoundation.framework/AVFoundation",
                out _);
            NativeLibrary.TryLoad(
                "/System/Library/Frameworks/Foundation.framework/Foundation",
                out _);
            _frameworkLoaded = true;
        }

        [SupportedOSPlatform("macos")]
        private static IReadOnlyList<Device> ListCore()
        {
            var poolClass = Native.objc_getClass("NSAutoreleasePool");
            var alloc = Native.sel_registerName("alloc");
            var init = Native.sel_registerName("init");
            var drain = Native.sel_registerName("drain");
            var pool = Native.MsgSend(Native.MsgSend(poolClass, alloc), init);

            try
            {
                var deviceClass = Native.objc_getClass("AVCaptureDevice");
                if (deviceClass == IntPtr.Zero)
                    return Array.Empty<Device>();

                var video = NsString("vide");
                var muxed = NsString("muxx");
                var selDevices = Native.sel_registerName("devicesWithMediaType:");
                var videoDevices = Native.MsgSend(deviceClass, selDevices, video);
                var muxedDevices = Native.MsgSend(deviceClass, selDevices, muxed);

                var ptrs = new List<IntPtr>();
                AppendDevicePtrs(videoDevices, ptrs);
                AppendDevicePtrs(muxedDevices, ptrs);

                var selUid = Native.sel_registerName("uniqueID");
                var selName = Native.sel_registerName("localizedName");
                var selCompare = Native.sel_registerName("compare:");
                // OpenCV cap_avfoundation_mac.mm: [d1.uniqueID compare:d2.uniqueID]
                ptrs.Sort((a, b) =>
                {
                    var ua = Native.MsgSend(a, selUid);
                    var ub = Native.MsgSend(b, selUid);
                    if (ua == IntPtr.Zero && ub == IntPtr.Zero) return 0;
                    if (ua == IntPtr.Zero) return -1;
                    if (ub == IntPtr.Zero) return 1;
                    return (int)Native.MsgSendNint(ua, selCompare, ub);
                });

                var result = new List<Device>(ptrs.Count);
                for (var i = 0; i < ptrs.Count; i++)
                {
                    var uid = NsToString(Native.MsgSend(ptrs[i], selUid)) ?? "";
                    var name = NsToString(Native.MsgSend(ptrs[i], selName));
                    if (string.IsNullOrWhiteSpace(name))
                        name = $"Camera {i}";
                    result.Add(new Device(i, uid, name.Trim(), RatesFromDevice(ptrs[i])));
                }

                return result;
            }
            finally
            {
                if (pool != IntPtr.Zero)
                    Native.MsgSend(pool, drain);
            }
        }

        /// <summary>Advertised rates from <c>formats</c> / frame-rate ranges.</summary>
        public static int[] TryQueryFpsRates(int index, string? uniqueId)
        {
            foreach (var d in List())
            {
                if (!string.IsNullOrWhiteSpace(uniqueId) &&
                    string.Equals(d.UniqueId, uniqueId, StringComparison.Ordinal))
                    return d.Rates;

                if (string.IsNullOrWhiteSpace(uniqueId) && d.Index == index)
                    return d.Rates;
            }

            return [];
        }

        public static int TryQueryMaxFps(int index, string? uniqueId) =>
            VideoFpsOptions.Max(TryQueryFpsRates(index, uniqueId));

        /// <summary>
        /// Lock a 60-capable pin near the encode width (same size/aspect for
        /// 15 / 30 / 60) and only change frame duration. Lives in
        /// <c>libYwcMacAvFps.dylib</c> because C# <c>objc_msgSend</c> of
        /// <c>CMTime</c> aborts on 59.94 fps HDMI pins. Must run on the AppKit
        /// thread after OpenCV has started the session.
        /// </summary>
        public static bool TrySetFrameRate(int index, string? uniqueId, int targetFps, int maxWidth, out string detail)
        {
            detail = "AVFoundation frame-rate not applied";
            if (!OperatingSystem.IsMacOS() || targetFps < 1)
                return false;

            if (string.IsNullOrWhiteSpace(uniqueId))
            {
                foreach (var d in List())
                {
                    if (d.Index == index)
                    {
                        uniqueId = d.UniqueId;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(uniqueId))
            {
                detail = "AVFoundation device not found for frame-rate set";
                return false;
            }

            try
            {
                var buf = new byte[512];
                var ok = NativeFps.Set(uniqueId.Trim(), targetFps, maxWidth, buf, buf.Length);
                var nul = Array.IndexOf(buf, (byte)0);
                detail = Encoding.UTF8.GetString(buf, 0, nul < 0 ? buf.Length : nul);
                if (string.IsNullOrWhiteSpace(detail))
                    detail = ok != 0 ? "AVFoundation frame-rate applied" : "AVFoundation frame-rate failed";
                return ok != 0;
            }
            catch (DllNotFoundException)
            {
                detail = "libYwcMacAvFps.dylib missing — rebuild on macOS";
                return false;
            }
            catch (Exception ex)
            {
                detail = "AVFoundation frame-rate failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        [SupportedOSPlatform("macos")]
        private static int[] RatesFromDevice(IntPtr device)
        {
            if (device == IntPtr.Zero)
                return [];

            var selFormats = Native.sel_registerName("formats");
            var selRanges = Native.sel_registerName("videoSupportedFrameRateRanges");
            var selMax = Native.sel_registerName("maxFrameRate");
            var selMin = Native.sel_registerName("minFrameRate");
            var selCount = Native.sel_registerName("count");
            var selAt = Native.sel_registerName("objectAtIndex:");

            var formats = Native.MsgSend(device, selFormats);
            if (formats == IntPtr.Zero)
                return [];

            var raw = new List<double>();
            var formatCount = Native.MsgSendNuint(formats, selCount);
            for (nuint i = 0; i < formatCount; i++)
            {
                var format = Native.MsgSendNuintArg(formats, selAt, i);
                if (format == IntPtr.Zero)
                    continue;

                var ranges = Native.MsgSend(format, selRanges);
                if (ranges == IntPtr.Zero)
                    continue;

                var rangeCount = Native.MsgSendNuint(ranges, selCount);
                for (nuint r = 0; r < rangeCount; r++)
                {
                    var range = Native.MsgSendNuintArg(ranges, selAt, r);
                    if (range == IntPtr.Zero)
                        continue;
                    var max = Native.MsgSendDouble(range, selMax);
                    var min = Native.MsgSendDouble(range, selMin);
                    if (min <= 0)
                        min = max;
                    raw.Add(max);
                    raw.Add(min);
                    if (Math.Abs(max - min) > 1)
                        VideoFpsOptions.AddPresetsInRange(raw, min, max);
                }
            }

            return VideoFpsOptions.UniqueSorted(raw);
        }

        [SupportedOSPlatform("macos")]
        private static void AppendDevicePtrs(IntPtr array, List<IntPtr> dest)
        {
            if (array == IntPtr.Zero)
                return;

            var selCount = Native.sel_registerName("count");
            var selAt = Native.sel_registerName("objectAtIndex:");
            var count = Native.MsgSendNuint(array, selCount);
            for (nuint i = 0; i < count; i++)
            {
                var device = Native.MsgSendNuintArg(array, selAt, i);
                if (device != IntPtr.Zero)
                    dest.Add(device);
            }
        }

        [SupportedOSPlatform("macos")]
        private static IntPtr NsString(string utf8)
        {
            var cls = Native.objc_getClass("NSString");
            var sel = Native.sel_registerName("stringWithUTF8String:");
            var ptr = Marshal.StringToCoTaskMemUTF8(utf8);
            try
            {
                return Native.MsgSend(cls, sel, ptr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }

        [SupportedOSPlatform("macos")]
        private static string? NsToString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero)
                return null;
            var utf8 = Native.MsgSend(nsString, Native.sel_registerName("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        private static class NativeFps
        {
            [DllImport("libYwcMacAvFps.dylib", EntryPoint = "YwcSetAvFoundationFps", CallingConvention = CallingConvention.Cdecl)]
            public static extern int Set(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string uniqueId,
                int fps,
                int maxWidth,
                byte[] err,
                int errLen);
        }

        private static class Native
        {
            private const string LibObjc = "/usr/lib/libobjc.A.dylib";

            [DllImport(LibObjc)]
            public static extern IntPtr objc_getClass(string name);

            [DllImport(LibObjc)]
            public static extern IntPtr sel_registerName(string name);

            [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
            public static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

            [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
            public static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

            [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
            public static extern nuint MsgSendNuint(IntPtr receiver, IntPtr selector);

            [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
            public static extern IntPtr MsgSendNuintArg(IntPtr receiver, IntPtr selector, nuint arg);

            [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
            public static extern nint MsgSendNint(IntPtr receiver, IntPtr selector, IntPtr arg);

            [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
            public static extern double MsgSendF64(IntPtr receiver, IntPtr selector);

            [DllImport(LibObjc, EntryPoint = "objc_msgSend_fpret")]
            public static extern double MsgSendFpret(IntPtr receiver, IntPtr selector);

            public static double MsgSendDouble(IntPtr receiver, IntPtr selector) =>
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? MsgSendF64(receiver, selector)
                    : MsgSendFpret(receiver, selector);
        }
    }
}
