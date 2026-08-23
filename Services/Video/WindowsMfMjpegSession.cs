using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Windows Media Foundation source-reader that keeps the capture pin as
    /// MJPEG and copies JPEG bytes as-is. OpenCV's DirectShow backend always
    /// decodes to BGR, which is why Radio Display stayed on YUY2 encode (~20 fps)
    /// while OBS holds 30 on the same dongle.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsMfMjpegSession : IJpegCaptureSession
    {
        private const int MfVersion = 0x00020070; // MF_VERSION
        private const int SourceReaderFirstVideoStream = unchecked((int)0xFFFFFFFC);
        private const int SourceReaderFlagError = 0x00000001;
        private const int SourceReaderFlagEndOfStream = 0x00000002;
        private const int MfENoMoreTypes = unchecked((int)0xC00D36B9);

        private static readonly Guid MfDevsourceAttributeSourceType = new("c60ac5fe-672d-41ef-afc3-1f319d7f80b0");
        private static readonly Guid MfDevsourceAttributeSourceTypeVidcapGuid = new("8ac3587a-4ba1-4d9f-abb4-946d5be8add6");
        private static readonly Guid MfDevsourceAttributeFriendlyName = new("60d0e559-52f8-4fa2-87e0-b834d243a97f");
        private static readonly Guid MfDevsourceAttributeSourceTypeVidcapSymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
        private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MfMtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid MfVideoFormatMjpg = new("47504A4D-0000-0010-8000-00AA00389B71");
        private static readonly Guid MfReadwriteDisableConverters = new("98d44c05-8a0d-4b80-8f1a-6f9a315fad27");

        private static readonly object StartupLock = new();
        private static bool _mfStarted;

        private static class AttrVtbl
        {
            // IUnknown (3) + IMFAttributes slots. SetUINT32=18, SetGUID=21, SetString=22.
            private const int SetUint32Slot = 21;
            private const int SetGuidSlot = 24;
            private const int SetStringSlot = 25;

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int SetGuidProc(IntPtr self, ref Guid key, ref Guid value);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int SetStringProc(IntPtr self, ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int SetUint32Proc(IntPtr self, ref Guid key, uint value);

            public static int SetGuid(IMFAttributes attrs, Guid key, Guid value) =>
                Call<SetGuidProc>(attrs, SetGuidSlot, (proc, p) => proc(p, ref key, ref value));

            public static int SetString(IMFAttributes attrs, Guid key, string value) =>
                Call<SetStringProc>(attrs, SetStringSlot, (proc, p) => proc(p, ref key, value));

            public static int SetUint32(IMFAttributes attrs, Guid key, uint value) =>
                Call<SetUint32Proc>(attrs, SetUint32Slot, (proc, p) => proc(p, ref key, value));

            private static int Call<T>(IMFAttributes attrs, int slot, Func<T, IntPtr, int> invoke)
                where T : Delegate
            {
                var p = Marshal.GetComInterfaceForObject(attrs, typeof(IMFAttributes));
                try
                {
                    var vtbl = Marshal.ReadIntPtr(p);
                    var fn = Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size));
                    return invoke(fn, p);
                }
                finally
                {
                    Marshal.Release(p);
                }
            }
        }

        private readonly IMFSourceReader _reader;
        private bool _disposed;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public double DeviceFps { get; private set; }
        public string DeviceName { get; }

        private WindowsMfMjpegSession(IMFSourceReader reader, int width, int height, double deviceFps, string deviceName)
        {
            _reader = reader;
            Width = width;
            Height = height;
            DeviceFps = deviceFps;
            DeviceName = deviceName;
        }

        public static WindowsMfMjpegSession? TryOpen(
            int dshowIndex, int targetFps, int maxWidth, ILogger logger, out bool noUsableMjpegPin,
            string? deviceKey = null, string? requestedSize = null)
        {
            noUsableMjpegPin = false;
            if (!OperatingSystem.IsWindows())
                return null;
            if (!EnsureStartup())
            {
                logger.LogWarning("Radio Display: MFStartup failed — Media Foundation MJPEG unavailable");
                return null;
            }

            var opened = false;
            try
            {
                var symlink = ResolveSymbolicLink(dshowIndex, logger, out var friendly);
                if (string.IsNullOrEmpty(symlink))
                {
                    logger.LogWarning(
                        "Radio Display: no Media Foundation device matched capture index {Index}",
                        dshowIndex);
                    return null;
                }

                var source = CreateDeviceSource(symlink);
                if (source is null)
                {
                    logger.LogWarning(
                        "Radio Display: MFCreateDeviceSource failed for '{Name}' (link={Link})",
                        friendly,
                        symlink);
                    return null;
                }

                IMFSourceReader? reader = null;
                try
                {
                    reader = CreateReader(source);
                    if (reader is null)
                    {
                        logger.LogWarning(
                            "Radio Display: MFCreateSourceReaderFromMediaSource failed for '{Name}'",
                            friendly);
                        return null;
                    }

                    var formats = new List<string>();
                    var selected = SelectRankedMjpegType(
                        reader, formats, targetFps, maxWidth,
                        out var width, out var height, out var deviceFps,
                        deviceKey, requestedSize);
                    logger.LogInformation(
                        "Radio Display: '{Name}' Media Foundation formats: {Formats}",
                        friendly,
                        formats.Count == 0 ? "(none reported)" : string.Join(", ", formats));

                    if (!selected)
                    {
                        logger.LogInformation(
                            "Radio Display: '{Name}' Media Foundation has no ranked MJPEG pin — falling back to OpenCV encode",
                            friendly);
                        noUsableMjpegPin = true;
                        return null;
                    }

                    opened = true;
                    return new WindowsMfMjpegSession(reader, width, height, deviceFps, friendly);
                }
                finally
                {
                    SafeRelease(source);
                    if (!opened && reader != null)
                        SafeRelease(reader);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Radio Display: Media Foundation MJPEG open failed");
                return null;
            }
            finally
            {
                if (!opened)
                    ReleaseStartup();
            }
        }

        /// <summary>
        /// Re-select the discrete MJPEG type at the locked size whose native
        /// frame rate is nearest <paramref name="targetFps"/>. Does not change
        /// width/height.
        /// </summary>
        public bool TrySetFrameRate(int targetFps)
        {
            if (_disposed || targetFps < 1)
                return false;

            var natives = EnumerateNativeTypes(_reader, formats: null);
            try
            {
                var pins = natives.Select(n => n.ToPin()).ToList();
                var match = VideoCapturePinRank.NearestFps(pins, Width, Height, jpeg: true, targetFps);
                if (match is null)
                    return false;

                NativeType? chosen = null;
                foreach (var n in natives)
                {
                    if (n.Width != match.Value.Width ||
                        n.Height != match.Value.Height ||
                        !n.Jpeg ||
                        Math.Abs(n.Fps - match.Value.Fps) > 0.01)
                        continue;
                    chosen = n;
                    break;
                }

                if (chosen is null)
                    return false;

                var hr = _reader.SetCurrentMediaType(SourceReaderFirstVideoStream, IntPtr.Zero, chosen.Type);
                if (hr < 0)
                    return false;

                DeviceFps = chosen.Fps;
                return true;
            }
            finally
            {
                foreach (var n in natives)
                    SafeRelease(n.Type);
            }
        }

        /// <summary>
        /// Native media-type frame rates. Opens a source reader but does not
        /// start streaming. Empty if MF is unavailable or the device is busy.
        /// </summary>
        public static int[] QueryFrameRates(int dshowIndex)
        {
            if (!OperatingSystem.IsWindows())
                return [];
            if (!EnsureStartup())
                return [];

            try
            {
                var symlink = ResolveSymbolicLinkQuiet(dshowIndex);
                if (string.IsNullOrEmpty(symlink))
                    return [];

                var source = CreateDeviceSource(symlink);
                if (source is null)
                    return [];

                IMFSourceReader? reader = null;
                try
                {
                    reader = CreateReader(source);
                    return reader is null ? [] : NativeFrameRates(reader);
                }
                finally
                {
                    SafeRelease(source);
                    if (reader != null)
                        SafeRelease(reader);
                }
            }
            catch
            {
                return [];
            }
            finally
            {
                ReleaseStartup();
            }
        }

        public static int QueryMaxFrameRate(int dshowIndex) =>
            VideoFpsOptions.Max(QueryFrameRates(dshowIndex));

        /// <summary>
        /// Native media types as pins, for the Radio Display capture-size
        /// list. Same open-but-do-not-stream contract as
        /// <see cref="QueryFrameRates"/>: empty if MF is unavailable or the
        /// capture loop already holds the device.
        /// </summary>
        public static List<VideoCapturePinRank.Pin> QueryPins(int dshowIndex)
        {
            if (!OperatingSystem.IsWindows())
                return [];
            if (!EnsureStartup())
                return [];

            try
            {
                var symlink = ResolveSymbolicLinkQuiet(dshowIndex);
                if (string.IsNullOrEmpty(symlink))
                    return [];

                var source = CreateDeviceSource(symlink);
                if (source is null)
                    return [];

                IMFSourceReader? reader = null;
                try
                {
                    reader = CreateReader(source);
                    if (reader is null)
                        return [];

                    var natives = EnumerateNativeTypes(reader, formats: null);
                    try
                    {
                        return natives.Select(n => n.ToPin()).ToList();
                    }
                    finally
                    {
                        foreach (var n in natives)
                            SafeRelease(n.Type);
                    }
                }
                finally
                {
                    SafeRelease(source);
                    if (reader != null)
                        SafeRelease(reader);
                }
            }
            catch
            {
                return [];
            }
            finally
            {
                ReleaseStartup();
            }
        }

        private static string? ResolveSymbolicLinkQuiet(int dshowIndex)
        {
            var names = WindowsDshowDevices.ListFriendlyNames();
            var friendly = names.TryGetValue(dshowIndex, out var dshowName) && !string.IsNullOrWhiteSpace(dshowName)
                ? dshowName.Trim()
                : $"Camera {dshowIndex}";

            var devices = EnumVideoDevicesQuiet();
            foreach (var (name, symlink) in devices)
            {
                if (string.Equals(name, friendly, StringComparison.OrdinalIgnoreCase))
                    return symlink;
            }

            if ((uint)dshowIndex < (uint)devices.Count)
                return devices[dshowIndex].Symlink;

            return WindowsDshowDevices.TryGetDevicePath(dshowIndex);
        }

        private static List<(string Name, string Symlink)> EnumVideoDevicesQuiet()
        {
            var list = new List<(string, string)>();
            var hr = MFCreateAttributes(out var attrs, 1);
            if (hr < 0 || attrs is null)
                return list;

            try
            {
                hr = AttrVtbl.SetGuid(attrs, MfDevsourceAttributeSourceType, MfDevsourceAttributeSourceTypeVidcapGuid);
                if (hr < 0)
                    return list;

                hr = MFEnumDeviceSources(attrs, out var activateArray, out var count);
                if (hr < 0 || activateArray == IntPtr.Zero || count == 0)
                    return list;

                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        var ptr = Marshal.ReadIntPtr(activateArray, (int)(i * (nuint)IntPtr.Size));
                        if (ptr == IntPtr.Zero)
                            continue;
                        var activate = (IMFActivate)Marshal.GetObjectForIUnknown(ptr);
                        Marshal.Release(ptr);
                        try
                        {
                            var nameKey = MfDevsourceAttributeFriendlyName;
                            var linkKey = MfDevsourceAttributeSourceTypeVidcapSymbolicLink;
                            var name = ReadAllocatedString(activate, ref nameKey) ?? $"Camera {i}";
                            var symlink = ReadAllocatedString(activate, ref linkKey);
                            if (!string.IsNullOrEmpty(symlink))
                                list.Add((name, symlink));
                        }
                        finally
                        {
                            SafeRelease(activate);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(activateArray);
                }
            }
            finally
            {
                SafeRelease(attrs);
            }

            return list;
        }

        private static int[] NativeFrameRates(IMFSourceReader reader)
        {
            var raw = new List<double>();
            for (uint i = 0; ; i++)
            {
                var hr = reader.GetNativeMediaType(SourceReaderFirstVideoStream, i, out var mediaType);
                if (hr == MfENoMoreTypes || hr < 0 || mediaType is null)
                    break;

                try
                {
                    raw.Add(ReadFrameRate(mediaType));
                }
                finally
                {
                    SafeRelease(mediaType);
                }
            }

            return VideoFpsOptions.UniqueSorted(raw);
        }

        public bool TryReadJpeg(out byte[]? jpeg)
        {
            jpeg = null;
            if (_disposed)
                return false;

            var hr = _reader.ReadSample(
                SourceReaderFirstVideoStream,
                0,
                out _,
                out var flags,
                out _,
                out var sample);
            try
            {
                if (hr < 0)
                    return false;
                if ((flags & (SourceReaderFlagError | SourceReaderFlagEndOfStream)) != 0)
                    return false;
                if (sample is null)
                    return false;

                var convHr = sample.ConvertToContiguousBuffer(out var buffer);
                if (convHr < 0 || buffer is null)
                    return false;

                try
                {
                    var lockHr = buffer.Lock(out var data, out _, out var current);
                    if (lockHr < 0 || data == IntPtr.Zero || current < 4)
                        return false;

                    try
                    {
                        if (Marshal.ReadByte(data) != 0xFF || Marshal.ReadByte(data, 1) != 0xD8)
                            return false;

                        var bytes = new byte[current];
                        Marshal.Copy(data, bytes, 0, current);
                        var end = FindJpegEoi(bytes);
                        jpeg = end >= 0 && end + 1 < bytes.Length
                            ? bytes.AsSpan(0, end + 1).ToArray()
                            : bytes;
                        return true;
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }
                finally
                {
                    SafeRelease(buffer);
                }
            }
            finally
            {
                if (sample != null)
                    SafeRelease(sample);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            SafeRelease(_reader);
            ReleaseStartup();
        }

        private static string? ResolveSymbolicLink(int dshowIndex, ILogger logger, out string friendly)
        {
            friendly = $"Camera {dshowIndex}";
            var names = WindowsDshowDevices.ListFriendlyNames();
            if (names.TryGetValue(dshowIndex, out var dshowName) && !string.IsNullOrWhiteSpace(dshowName))
                friendly = dshowName.Trim();

            var devices = EnumVideoDevices(logger);
            logger.LogInformation(
                "Radio Display: Media Foundation sees {Count} capture device(s): {Names}; want index {Index} '{Friendly}'",
                devices.Count,
                devices.Count == 0 ? "(none)" : string.Join(" | ", devices.Select(d => d.Name)),
                dshowIndex,
                friendly);

            foreach (var (name, symlink) in devices)
            {
                if (string.Equals(name, friendly, StringComparison.OrdinalIgnoreCase))
                    return symlink;
            }

            if ((uint)dshowIndex < (uint)devices.Count)
            {
                friendly = devices[dshowIndex].Name;
                return devices[dshowIndex].Symlink;
            }

            var dshowPath = WindowsDshowDevices.TryGetDevicePath(dshowIndex);
            if (!string.IsNullOrEmpty(dshowPath))
            {
                logger.LogInformation(
                    "Radio Display: Media Foundation enum empty; using DirectShow DevicePath for '{Friendly}'",
                    friendly);
                return dshowPath;
            }

            return null;
        }

        private static List<(string Name, string Symlink)> EnumVideoDevices(ILogger logger)
        {
            var list = new List<(string, string)>();
            var hr = MFCreateAttributes(out var attrs, 1);
            if (hr < 0 || attrs is null)
            {
                logger.LogWarning("Radio Display: MFCreateAttributes failed (hr=0x{Hr:X8})", hr);
                return list;
            }

            try
            {
                hr = AttrVtbl.SetGuid(attrs, MfDevsourceAttributeSourceType, MfDevsourceAttributeSourceTypeVidcapGuid);
                if (hr < 0)
                {
                    logger.LogWarning("Radio Display: SetGUID(SOURCE_TYPE=VIDCAP) failed (hr=0x{Hr:X8})", hr);
                    return list;
                }

                hr = MFEnumDeviceSources(attrs, out var activateArray, out var count);
                if (hr < 0 || activateArray == IntPtr.Zero || count == 0)
                {
                    logger.LogWarning(
                        "Radio Display: MFEnumDeviceSources returned hr=0x{Hr:X8}, count={Count}" +
                        (hr == unchecked((int)0xC00D36E6)
                            ? " (MF_E_ATTRIBUTENOTFOUND — SOURCE_TYPE did not stick; will try DirectShow DevicePath)."
                            : hr == 0
                                ? " (empty list: check Settings → Privacy & security → Camera → Let desktop apps access your camera)."
                                : "."),
                        hr,
                        count);
                    return list;
                }

                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        var ptr = Marshal.ReadIntPtr(activateArray, (int)(i * (nuint)IntPtr.Size));
                        if (ptr == IntPtr.Zero)
                            continue;
                        var activate = (IMFActivate)Marshal.GetObjectForIUnknown(ptr);
                        Marshal.Release(ptr);
                        try
                        {
                            var nameKey = MfDevsourceAttributeFriendlyName;
                            var linkKey = MfDevsourceAttributeSourceTypeVidcapSymbolicLink;
                            var name = ReadAllocatedString(activate, ref nameKey) ?? $"Camera {i}";
                            var symlink = ReadAllocatedString(activate, ref linkKey);
                            if (!string.IsNullOrEmpty(symlink))
                                list.Add((name, symlink));
                        }
                        finally
                        {
                            SafeRelease(activate);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(activateArray);
                }
            }
            finally
            {
                SafeRelease(attrs);
            }

            return list;
        }

        private static object? CreateDeviceSource(string symlink)
        {
            var hr = MFCreateAttributes(out var attrs, 2);
            if (hr < 0 || attrs is null)
                return null;

            try
            {
                if (AttrVtbl.SetGuid(attrs, MfDevsourceAttributeSourceType, MfDevsourceAttributeSourceTypeVidcapGuid) < 0)
                    return null;
                if (AttrVtbl.SetString(attrs, MfDevsourceAttributeSourceTypeVidcapSymbolicLink, symlink) < 0)
                    return null;

                hr = MFCreateDeviceSource(attrs, out var source);
                return hr >= 0 ? source : null;
            }
            finally
            {
                SafeRelease(attrs);
            }
        }

        private static IMFSourceReader? CreateReader(object source)
        {
            var hr = MFCreateAttributes(out var attrs, 1);
            if (hr < 0 || attrs is null)
                return null;

            try
            {
                if (AttrVtbl.SetUint32(attrs, MfReadwriteDisableConverters, 1) < 0)
                    return null;
                hr = MFCreateSourceReaderFromMediaSource(source, attrs, out var reader);
                return hr >= 0 ? reader : null;
            }
            finally
            {
                SafeRelease(attrs);
            }
        }

        private sealed class NativeType
        {
            public required IMFMediaType Type { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public double Fps { get; init; }
            public bool Jpeg { get; init; }

            public VideoCapturePinRank.Pin ToPin() => new(Width, Height, Fps, Jpeg);
        }

        private static bool SelectRankedMjpegType(
            IMFSourceReader reader,
            List<string> formats,
            int targetFps,
            int maxWidth,
            out int width,
            out int height,
            out double deviceFps,
            string? deviceKey = null,
            string? requestedSize = null)
        {
            width = 0;
            height = 0;
            deviceFps = 0;
            var natives = EnumerateNativeTypes(reader, formats);
            try
            {
                if (natives.Count == 0)
                    return false;

                var pins = natives.Select(n => n.ToPin()).ToList();
                VideoDeviceSizeCaps.RememberPins(deviceKey, pins);

                var passthrough = VideoCapturePinRank.PickRequestedSize(pins, requestedSize, targetFps)
                    ?? VideoCapturePinRank.PickMjpegPassthrough(pins, targetFps, maxWidth);
                if (passthrough is null)
                    return false;

                var match = VideoCapturePinRank.NearestFps(
                    pins, passthrough.Value.Width, passthrough.Value.Height, jpeg: true, targetFps)
                    ?? passthrough.Value;

                NativeType? chosen = null;
                foreach (var n in natives)
                {
                    if (n.Width != match.Width || n.Height != match.Height || !n.Jpeg)
                        continue;
                    if (chosen is not null && Math.Abs(n.Fps - match.Fps) >= Math.Abs(chosen.Fps - match.Fps))
                        continue;
                    chosen = n;
                }

                if (chosen is null)
                    return false;

                var setHr = reader.SetCurrentMediaType(SourceReaderFirstVideoStream, IntPtr.Zero, chosen.Type);
                if (setHr < 0)
                    return false;

                width = chosen.Width;
                height = chosen.Height;
                deviceFps = chosen.Fps;
                return true;
            }
            finally
            {
                foreach (var n in natives)
                    SafeRelease(n.Type);
            }
        }

        private static List<NativeType> EnumerateNativeTypes(IMFSourceReader reader, List<string>? formats)
        {
            var list = new List<NativeType>();
            for (uint i = 0; ; i++)
            {
                var hr = reader.GetNativeMediaType(SourceReaderFirstVideoStream, i, out var mediaType);
                if (hr == MfENoMoreTypes || hr < 0 || mediaType is null)
                    break;

                var subtypeKey = MfMtSubtype;
                if (mediaType.GetGUID(ref subtypeKey, out var subtype) < 0)
                {
                    SafeRelease(mediaType);
                    continue;
                }

                var sizeKey = MfMtFrameSize;
                if (mediaType.GetUINT64(ref sizeKey, out var packed) < 0)
                {
                    SafeRelease(mediaType);
                    continue;
                }

                var w = (int)(packed >> 32);
                var h = (int)(packed & 0xFFFFFFFF);
                var fps = ReadFrameRate(mediaType);
                var jpeg = subtype == MfVideoFormatMjpg;
                formats?.Add($"{FourCcFromSubtype(subtype)} {w}x{h}@{fps:0.#}");

                if (w < 2 || h < 2)
                {
                    SafeRelease(mediaType);
                    continue;
                }

                list.Add(new NativeType
                {
                    Type = mediaType,
                    Width = w,
                    Height = h,
                    Fps = fps,
                    Jpeg = jpeg
                });
            }

            return list;
        }

        private static double ReadFrameRate(IMFMediaType mediaType)
        {
            var key = MfMtFrameRate;
            if (mediaType.GetUINT64(ref key, out var packed) < 0)
                return 0;
            var numerator = (uint)(packed >> 32);
            var denominator = (uint)(packed & 0xFFFFFFFF);
            return denominator == 0 ? 0 : (double)numerator / denominator;
        }

        private static string FourCcFromSubtype(Guid subtype)
        {
            var bytes = subtype.ToByteArray();
            Span<char> chars = stackalloc char[4];
            for (var i = 0; i < 4; i++)
            {
                var c = (char)bytes[i];
                chars[i] = c is >= (char)32 and <= (char)126 ? c : '?';
            }

            return new string(chars);
        }

        private static string? ReadAllocatedString(IMFAttributes attrs, ref Guid key)
        {
            if (attrs.GetAllocatedString(ref key, out var ptr, out _) < 0 || ptr == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }

        private static int FindJpegEoi(byte[] bytes)
        {
            for (var i = bytes.Length - 2; i >= 2; i--)
            {
                if (bytes[i] == 0xFF && bytes[i + 1] == 0xD9)
                    return i + 1;
            }

            return -1;
        }

        private static bool EnsureStartup()
        {
            lock (StartupLock)
            {
                if (_mfStarted)
                    return true;
                var hr = MFStartup(MfVersion, 0);
                if (hr < 0)
                    return false;
                _mfStarted = true;
                return true;
            }
        }

        private static void ReleaseStartup()
        {
            // Keep MF started for the process. QueryFrameRates and OpenCV MSMF
            // race MFShutdown into MFEnumDeviceSources returning ATTRIBUTENOTFOUND.
        }

        private static void SafeRelease(object? com)
        {
            if (com == null)
                return;
            try { Marshal.ReleaseComObject(com); } catch { /* ignore */ }
        }

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

        [DllImport("mf.dll", ExactSpelling = true)]
        private static extern int MFEnumDeviceSources(
            IMFAttributes pAttributes,
            out IntPtr pppSourceActivate,
            out uint pcSourceActivate);

        [DllImport("mf.dll", ExactSpelling = true)]
        private static extern int MFCreateDeviceSource(IMFAttributes pAttributes, [MarshalAs(UnmanagedType.IUnknown)] out object ppSource);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        private static extern int MFCreateSourceReaderFromMediaSource(
            [MarshalAs(UnmanagedType.IUnknown)] object pMediaSource,
            IMFAttributes? pAttributes,
            out IMFSourceReader ppSourceReader);

        [ComImport]
        [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out int pbResult);
            [PreserveSig] int Compare(IMFAttributes other, int match, out int pbResult);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ip, out uint pcbSize);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
            [PreserveSig] int DeleteItem(ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
            [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
            [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        }

        [ComImport]
        [Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFActivate : IMFAttributes
        {
            [PreserveSig] int ActivateObject(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            [PreserveSig] int ShutdownObject();
            [PreserveSig] int DetachObject();
        }

        [ComImport]
        [Guid("44ae0fa8-ea31-4109-8d20-8e22a5012017")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType : IMFAttributes
        {
            [PreserveSig] int GetMajorType(out Guid pguidMajorType);
            [PreserveSig] int IsCompressedFormat(out int pfCompressed);
            [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
            [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
            [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
        }

        [ComImport]
        [Guid("70ae66f2-c809-4e4f-8915-bdcb406b6693")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(int dwStreamIndex, out int pfSelected);
            [PreserveSig] int SetStreamSelection(int dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
            [PreserveSig] int GetNativeMediaType(int dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);
            [PreserveSig] int GetCurrentMediaType(int dwStreamIndex, out IMFMediaType ppMediaType);
            [PreserveSig] int SetCurrentMediaType(int dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
            [PreserveSig] int SetCurrentPosition(ref Guid guidTimeFormat, IntPtr varPosition);
            [PreserveSig] int ReadSample(
                int dwStreamIndex,
                int dwControlFlags,
                out int pdwActualStreamIndex,
                out int pdwStreamFlags,
                out long pllTimestamp,
                out IMFSample? ppSample);
            [PreserveSig] int Flush(int dwStreamIndex);
            [PreserveSig] int GetServiceForStream(int dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
            [PreserveSig] int GetPresentationAttribute(int dwStreamIndex, ref Guid guidAttribute, IntPtr pvarAttribute);
        }

        [ComImport]
        [Guid("c40a00f2-b58f-4a2b-8c4e-68b64239dc6d")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample : IMFAttributes
        {
            [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
            [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
            [PreserveSig] int GetSampleTime(out long phnsSampleTime);
            [PreserveSig] int SetSampleTime(long hnsSampleTime);
            [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
            [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
            [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
            [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
            [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
            [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
        }

        [ComImport]
        [Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
            [PreserveSig] int SetCurrentLength(int cbCurrentLength);
            [PreserveSig] int GetMaxLength(out int pcbMaxLength);
        }
    }
}
