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
        private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        private static readonly Guid IidPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");
        private static readonly Guid IidBaseFilter = new("56a86895-0ad4-11ce-b03a-0020af0ba770");

        /// <summary>Last <c>CreateClassEnumerator</c> HRESULT (0 = S_OK, 1 = S_FALSE / empty).</summary>
        internal static int LastCreateClassEnumeratorHr { get; private set; }

        public static IReadOnlyDictionary<int, string> ListFriendlyNames()
        {
            if (!OperatingSystem.IsWindows())
                return new Dictionary<int, string>();

            return ListFriendlyNamesSta();
        }

        /// <summary>
        /// DirectShow's system device enumerator is STA. HTTP/thread-pool
        /// threads are MTA and often return no monikers (empty dropdown).
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static IReadOnlyDictionary<int, string> ListFriendlyNamesSta()
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return ListFriendlyNamesWindows();

            IReadOnlyDictionary<int, string> result = new Dictionary<int, string>();
            Exception? error = null;
            using var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    result = ListFriendlyNamesWindows();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            })
            {
                IsBackground = true,
                Name = "YWC-DShowEnum"
            };

            try { thread.SetApartmentState(ApartmentState.STA); }
            catch (InvalidOperationException) { /* ignore */ }

            thread.Start();
            if (!done.Wait(4000))
                return new Dictionary<int, string>();

            if (error != null)
                return new Dictionary<int, string>();

            return result;
        }

        [SupportedOSPlatform("windows")]
        private static IReadOnlyDictionary<int, string> ListFriendlyNamesWindows()
        {
            var map = new Dictionary<int, string>();
            object? deviceEnumObj = null;
            IEnumMoniker? enumerator = null;
            LastCreateClassEnumeratorHr = unchecked((int)0x80004005); // E_FAIL until set
            try
            {
                var clsidType = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum, throwOnError: false);
                if (clsidType is null)
                    return map;

                deviceEnumObj = Activator.CreateInstance(clsidType);
                if (deviceEnumObj is not ICreateDevEnum createDevEnum)
                    return map;

                var category = VideoInputDeviceCategory;
                var hr = createDevEnum.CreateClassEnumerator(ref category, out enumerator, 0);
                LastCreateClassEnumeratorHr = hr;
                // S_FALSE (1) = category exists but has no devices.
                if (hr != 0 || enumerator is null)
                    return map;

                var monikers = new IMoniker[1];
                var index = 0;
                while (true)
                {
                    var nhr = enumerator.Next(1, monikers, IntPtr.Zero);
                    if (nhr != 0 || monikers[0] is null)
                        break;

                    var moniker = monikers[0];
                    monikers[0] = null!;
                    try
                    {
                        var name = ReadBagString(moniker, "FriendlyName");
                        if (!string.IsNullOrWhiteSpace(name))
                            map[index] = name.Trim();
                        index++;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(moniker);
                    }
                }
            }
            catch
            {
                // Fall back to index-only labels / ffmpeg in the caller.
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

        /// <summary>
        /// DirectShow <c>DevicePath</c> for OpenCV index <paramref name="dshowIndex"/>.
        /// Same symbolic link Media Foundation uses — lets us open the source
        /// without <c>MFEnumDeviceSources</c>.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static string? TryGetDevicePath(int dshowIndex)
        {
            if (!OperatingSystem.IsWindows() || dshowIndex < 0)
                return null;

            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return DevicePathAt(dshowIndex);

            string? path = null;
            using var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try { path = DevicePathAt(dshowIndex); }
                catch { /* ignore */ }
                finally { done.Set(); }
            })
            {
                IsBackground = true,
                Name = "YWC-DShowPath"
            };
            try { thread.SetApartmentState(ApartmentState.STA); }
            catch (InvalidOperationException) { /* ignore */ }
            thread.Start();
            done.Wait(4000);
            return path;
        }

        [SupportedOSPlatform("windows")]
        private static string? DevicePathAt(int dshowIndex)
        {
            object? deviceEnumObj = null;
            IEnumMoniker? enumerator = null;
            try
            {
                var clsidType = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum, throwOnError: false);
                if (clsidType is null)
                    return null;
                deviceEnumObj = Activator.CreateInstance(clsidType);
                if (deviceEnumObj is not ICreateDevEnum createDevEnum)
                    return null;

                var category = VideoInputDeviceCategory;
                if (createDevEnum.CreateClassEnumerator(ref category, out enumerator, 0) != 0 || enumerator is null)
                    return null;

                var monikers = new IMoniker[1];
                var index = 0;
                while (true)
                {
                    var nhr = enumerator.Next(1, monikers, IntPtr.Zero);
                    if (nhr != 0 || monikers[0] is null)
                        break;
                    var moniker = monikers[0];
                    monikers[0] = null!;
                    try
                    {
                        if (index == dshowIndex)
                            return ReadBagString(moniker, "DevicePath");
                        index++;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(moniker);
                    }
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (enumerator != null)
                    Marshal.ReleaseComObject(enumerator);
                if (deviceEnumObj != null)
                    Marshal.ReleaseComObject(deviceEnumObj);
            }

            return null;
        }

        /// <summary>
        /// Bind the DirectShow capture filter at OpenCV index
        /// <paramref name="dshowIndex"/>. Must run on an STA thread that will
        /// own the graph — do not call from the thread pool (the filter is
        /// apartment-bound).
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static object? BindFilterAt(int dshowIndex)
        {
            if (!OperatingSystem.IsWindows() || dshowIndex < 0)
                return null;
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                return null;
            return BindFilterAtWindows(dshowIndex);
        }

        [SupportedOSPlatform("windows")]
        private static object? BindFilterAtWindows(int dshowIndex)
        {
            object? deviceEnumObj = null;
            IEnumMoniker? enumerator = null;
            try
            {
                var clsidType = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum, throwOnError: false);
                if (clsidType is null)
                    return null;
                deviceEnumObj = Activator.CreateInstance(clsidType);
                if (deviceEnumObj is not ICreateDevEnum createDevEnum)
                    return null;

                var category = VideoInputDeviceCategory;
                if (createDevEnum.CreateClassEnumerator(ref category, out enumerator, 0) != 0 || enumerator is null)
                    return null;

                var monikers = new IMoniker[1];
                var index = 0;
                while (true)
                {
                    var nhr = enumerator.Next(1, monikers, IntPtr.Zero);
                    if (nhr != 0 || monikers[0] is null)
                        break;
                    var moniker = monikers[0];
                    monikers[0] = null!;
                    try
                    {
                        if (index == dshowIndex)
                        {
                            var iid = IidBaseFilter;
                            moniker.BindToObject(null!, null!, ref iid, out var filter);
                            return filter;
                        }

                        index++;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(moniker);
                    }
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (enumerator != null)
                    Marshal.ReleaseComObject(enumerator);
                if (deviceEnumObj != null)
                    Marshal.ReleaseComObject(deviceEnumObj);
            }

            return null;
        }

        [SupportedOSPlatform("windows")]
        private static string? ReadBagString(IMoniker? moniker, string propertyName)
        {
            if (moniker is null)
                return null;

            object? bagObj = null;
            try
            {
                var iid = IidPropertyBag;
                moniker.BindToStorage(null!, null!, ref iid, out bagObj);
                if (bagObj is not IPropertyBag bag)
                    return null;

                object value = "";
                var hr = bag.Read(propertyName, ref value, IntPtr.Zero);
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
                [In] ref Guid deviceClass,
                out IEnumMoniker enumMoniker,
                [In] int flags);
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
