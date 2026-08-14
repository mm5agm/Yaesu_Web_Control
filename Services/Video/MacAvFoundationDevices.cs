using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
        internal readonly record struct Device(int Index, string UniqueId, string LocalizedName);

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
                    result.Add(new Device(i, uid, name.Trim()));
                }

                return result;
            }
            finally
            {
                if (pool != IntPtr.Zero)
                    Native.MsgSend(pool, drain);
            }
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
        }
    }
}
