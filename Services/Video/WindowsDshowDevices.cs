using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// DirectShow video-input friendly names in the same index order OpenCV
    /// <c>CAP_DSHOW</c> uses. Enumeration binds to property bags only — it does
    /// not open the capture graph, so it is safe while Radio Display is streaming.
    /// </summary>
    internal static class WindowsDshowDevices
    {
        private static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        private static readonly Guid PropertyBagIid = new("55272A00-42CB-11CE-8135-00AA004BB851");

        public static IReadOnlyDictionary<int, string> ListFriendlyNames()
        {
            if (!OperatingSystem.IsWindows())
                return new Dictionary<int, string>();

            return ListFriendlyNamesWindows();
        }

        [SupportedOSPlatform("windows")]
        private static IReadOnlyDictionary<int, string> ListFriendlyNamesWindows()
        {
            var map = new Dictionary<int, string>();
            object? deviceEnumObj = null;
            IEnumMonikerOne? enumerator = null;
            try
            {
                var clsidType = Type.GetTypeFromCLSID(SystemDeviceEnum, throwOnError: false);
                if (clsidType is null)
                    return map;

                deviceEnumObj = Activator.CreateInstance(clsidType);
                if (deviceEnumObj is not ICreateDevEnum createDevEnum)
                    return map;

                var hr = createDevEnum.CreateClassEnumerator(VideoInputDeviceCategory, out enumerator, 0);
                if (hr != 0 || enumerator is null)
                    return map;

                var index = 0;
                while (enumerator.Next(1, out var moniker, IntPtr.Zero) == 0)
                {
                    try
                    {
                        var name = ReadFriendlyName(moniker);
                        if (!string.IsNullOrWhiteSpace(name))
                            map[index] = name.Trim();
                        index++;
                    }
                    finally
                    {
                        if (moniker != null)
                            Marshal.ReleaseComObject(moniker);
                    }
                }
            }
            catch
            {
                // Fall back to index-only labels in the caller.
            }
            finally
            {
                if (enumerator != null)
                    Marshal.ReleaseComObject(enumerator);
                if (deviceEnumObj != null)
                    Marshal.ReleaseComObject(deviceEnumObj);
            }

            return map;
        }

        [SupportedOSPlatform("windows")]
        private static string? ReadFriendlyName(IMoniker? moniker)
        {
            if (moniker is null)
                return null;

            object? bagObj = null;
            try
            {
                var iid = PropertyBagIid;
                moniker.BindToStorage(null!, null!, ref iid, out bagObj);
                if (bagObj is not IPropertyBag bag)
                    return null;

                object value = "";
                var hr = bag.Read("FriendlyName", ref value, IntPtr.Zero);
                if (hr != 0)
                    return null;
                return value as string;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (bagObj != null)
                    Marshal.ReleaseComObject(bagObj);
            }
        }

        [ComImport]
        [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator(
                [In, MarshalAs(UnmanagedType.LPStruct)] Guid deviceClass,
                out IEnumMonikerOne? enumMoniker,
                [In] int flags);
        }

        /// <summary>
        /// <c>celt == 1</c> form of IEnumMoniker.Next — array marshaling of the
        /// stock ComTypes interface is unreliable here.
        /// </summary>
        [ComImport]
        [Guid("0000010c-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumMonikerOne
        {
            [PreserveSig]
            int Next(int celt, out IMoniker rgelt, IntPtr pceltFetched);

            [PreserveSig]
            int Skip(int celt);

            [PreserveSig]
            int Reset();

            [PreserveSig]
            int Clone(out IEnumMonikerOne ppenum);
        }

        [ComImport]
        [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag
        {
            [PreserveSig]
            int Read(
                [In, MarshalAs(UnmanagedType.LPWStr)] string propertyName,
                [In, Out, MarshalAs(UnmanagedType.Struct)] ref object value,
                IntPtr errorLog);

            [PreserveSig]
            int Write(
                [In, MarshalAs(UnmanagedType.LPWStr)] string propertyName,
                [In] ref object value);
        }
    }
}
