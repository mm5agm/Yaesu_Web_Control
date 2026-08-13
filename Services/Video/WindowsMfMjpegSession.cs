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
    internal sealed class WindowsMfMjpegSession : IDisposable
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
        private static int _startupCount;

        private readonly IMFSourceReader _reader;
        private bool _disposed;

        public int Width { get; }
        public int Height { get; }
        public string DeviceName { get; }

        private WindowsMfMjpegSession(IMFSourceReader reader, int width, int height, string deviceName)
        {
            _reader = reader;
            Width = width;
            Height = height;
            DeviceName = deviceName;
        }

        public static WindowsMfMjpegSession? TryOpen(int dshowIndex, int targetFps, ILogger logger)
        {
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
                        "Radio Display: MFCreateDeviceSource failed for '{Name}'",
                        friendly);
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
                    var selected = SelectMjpegType(reader, formats, out var width, out var height);
                    logger.LogInformation(
                        "Radio Display: '{Name}' Media Foundation formats: {Formats}",
                        friendly,
                        formats.Count == 0 ? "(none reported)" : string.Join(", ", formats));

                    if (!selected)
                    {
                        logger.LogWarning(
                            "Radio Display: '{Name}' exposes no MJPEG mode to Media Foundation — falling back to OpenCV encode",
                            friendly);
                        return null;
                    }

                    opened = true;
                    return new WindowsMfMjpegSession(reader, width, height, friendly);
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
                var type = MfDevsourceAttributeSourceType;
                var vidcap = MfDevsourceAttributeSourceTypeVidcapGuid;
                hr = attrs.SetGUID(ref type, ref vidcap);
                if (hr < 0)
                {
                    logger.LogWarning("Radio Display: SetGUID(SOURCE_TYPE=VIDCAP) failed (hr=0x{Hr:X8})", hr);
                    return list;
                }

                hr = MFEnumDeviceSources(attrs, out var activateArray, out var count);
                if (hr < 0 || activateArray == IntPtr.Zero || count == 0)
                {
                    logger.LogWarning(
                        "Radio Display: MFEnumDeviceSources returned hr=0x{Hr:X8}, count={Count}. " +
                        "If hr is 0 with count 0, Windows camera privacy is likely blocking Media Foundation " +
                        "(Settings → Privacy & security → Camera → Let desktop apps access your camera).",
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
                var type = MfDevsourceAttributeSourceType;
                var vidcap = MfDevsourceAttributeSourceTypeVidcapGuid;
                var linkKey = MfDevsourceAttributeSourceTypeVidcapSymbolicLink;
                if (attrs.SetGUID(ref type, ref vidcap) < 0)
                    return null;
                if (attrs.SetString(ref linkKey, symlink) < 0)
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
                var disable = MfReadwriteDisableConverters;
                attrs.SetUINT32(ref disable, 1);
                hr = MFCreateSourceReaderFromMediaSource(source, attrs, out var reader);
                return hr >= 0 ? reader : null;
            }
            finally
            {
                SafeRelease(attrs);
            }
        }

        private static bool SelectMjpegType(IMFSourceReader reader, List<string> formats, out int width, out int height)
        {
            width = 0;
            height = 0;
            (int Width, int Height, IMFMediaType Type)? best = null;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var hr = reader.GetNativeMediaType(SourceReaderFirstVideoStream, i, out var mediaType);
                    if (hr == MfENoMoreTypes || hr < 0 || mediaType is null)
                        break;

                    try
                    {
                        var subtypeKey = MfMtSubtype;
                        if (mediaType.GetGUID(ref subtypeKey, out var subtype) < 0)
                            continue;

                        var sizeKey = MfMtFrameSize;
                        if (mediaType.GetUINT64(ref sizeKey, out var packed) < 0)
                            continue;
                        var w = (int)(packed >> 32);
                        var h = (int)(packed & 0xFFFFFFFF);

                        formats.Add($"{FourCcFromSubtype(subtype)} {w}x{h}@{ReadFrameRate(mediaType):0.#}");

                        if (subtype != MfVideoFormatMjpg || w < 160 || h < 120)
                            continue;

                        if (best is null || BetterMjpegSize(w, h, best.Value.Width, best.Value.Height))
                        {
                            if (best != null)
                                SafeRelease(best.Value.Type);
                            best = (w, h, mediaType);
                            mediaType = null!;
                        }
                    }
                    finally
                    {
                        if (mediaType != null)
                            SafeRelease(mediaType);
                    }
                }

                if (best is null)
                    return false;

                var chosen = best.Value;
                var setHr = reader.SetCurrentMediaType(SourceReaderFirstVideoStream, IntPtr.Zero, chosen.Type);
                width = chosen.Width;
                height = chosen.Height;
                return setHr >= 0;
            }
            finally
            {
                if (best != null)
                    SafeRelease(best.Value.Type);
            }
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

        /// <summary>Prefer 1280×720 (USB2-friendly 30 fps), then 1920×1080, then anything else.</summary>
        private static bool BetterMjpegSize(int w, int h, int curW, int curH)
        {
            int Rank(int a, int b)
            {
                if (a == 1280 && b == 720) return 0;
                if (a == 1920 && b == 1080) return 1;
                return 2;
            }

            var r = Rank(w, h);
            var c = Rank(curW, curH);
            if (r != c)
                return r < c;
            return w * h > curW * curH;
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
                if (_startupCount == 0)
                {
                    var hr = MFStartup(MfVersion, 0);
                    if (hr < 0)
                        return false;
                }

                _startupCount++;
                return true;
            }
        }

        private static void ReleaseStartup()
        {
            lock (StartupLock)
            {
                if (_startupCount <= 0)
                    return;
                _startupCount--;
                if (_startupCount == 0)
                {
                    try { MFShutdown(); } catch { /* ignore */ }
                }
            }
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
