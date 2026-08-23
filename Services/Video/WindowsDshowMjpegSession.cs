using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// DirectShow capture of the device's native MJPEG pin (the pin OBS uses).
    /// OpenCV <c>CAP_DSHOW</c> always inserts a colour converter and lands on
    /// YUY2; this graph sets <c>IAMStreamConfig</c> to MJPEG and copies JPEG
    /// bytes from a Sample Grabber with no decoder.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsDshowMjpegSession : IJpegCaptureSession
    {
        private static readonly Guid ClsidFilterGraph = new("e436ebb3-524f-11ce-9f53-0020af0ba770");
        private static readonly Guid ClsidSampleGrabber = new("C1F400A0-3F08-11d3-9F0B-006008039E37");
        private static readonly Guid ClsidNullRenderer = new("C1F400A4-3F08-11d3-9F0B-006008039E37");
        private static readonly Guid IidAmStreamConfig = new("C6E13340-30AC-11d0-A18C-00A0C9118956");
        private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid MediaSubtypeMjpg = new("47504A4D-0000-0010-8000-00AA00389B71");
        private static readonly Guid FormatVideoInfo = new("05589f80-c356-11ce-bf01-00aa0055595a");
        private static readonly Guid FormatVideoInfo2 = new("F72A76A0-EB0A-11d0-ACE4-0000C0CC16BA");

        private const int PinDirOutput = 1;
        private const int PinDirInput = 0;
        private const int FilterStateRunning = 2;
        private const int ReadWaitMs = 1000;

        private readonly object _graphObj;
        private readonly IGraphBuilder _graph;
        private readonly IMediaControl _control;
        private readonly object _captureObj;
        private readonly object _grabberObj;
        private readonly object _nullObj;
        private readonly ISampleGrabber _grabber;
        private readonly GrabberCallback _callback;
        private readonly AutoResetEvent _frameReady = new(false);
        private readonly object _frameLock = new();
        private readonly ILogger _logger;
        /// <summary>Trailing-data samples that together prove the device merges frames.</summary>
        private const int MergedFrameEvidence = 3;

        /// <summary>Opening samples traced when YWC_VIDEO_TRACE_SAMPLES is set.</summary>
        private const int SampleTraceCount = 30;

        private long _trailingSamples;
        private long _mergedSamples;
        private long _samplesSeen;
        private readonly Stopwatch _sinceOpen = Stopwatch.StartNew();
        private readonly bool _traceSamples =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YWC_VIDEO_TRACE_SAMPLES"));
        private int _mergesFrames;
        private long _unterminatedSamples;
        private long _imagelessSamples;
        private readonly List<StreamCap> _caps;
        private byte[]? _latestJpeg;
        private bool _disposed;

        public bool DeviceMergesFrames => Volatile.Read(ref _mergesFrames) != 0;


        public int Width { get; private set; }
        public int Height { get; private set; }
        public double DeviceFps { get; private set; }

        private WindowsDshowMjpegSession(
            object graphObj,
            IGraphBuilder graph,
            IMediaControl control,
            object captureObj,
            object grabberObj,
            object nullObj,
            ISampleGrabber grabber,
            GrabberCallback callback,
            List<StreamCap> caps,
            int width,
            int height,
            double deviceFps,
            ILogger logger)
        {
            _graphObj = graphObj;
            _graph = graph;
            _control = control;
            _captureObj = captureObj;
            _grabberObj = grabberObj;
            _nullObj = nullObj;
            _grabber = grabber;
            _callback = callback;
            _caps = caps;
            _logger = logger;
            Width = width;
            Height = height;
            DeviceFps = deviceFps;
            callback.Owner = this;
        }

        public static WindowsDshowMjpegSession? TryOpen(
            int dshowIndex, int targetFps, int maxWidth, ILogger logger, out bool noUsableMjpegPin,
            bool preferNativeMode = false, string? deviceKey = null, string? requestedSize = null)
        {
            noUsableMjpegPin = false;
            if (!OperatingSystem.IsWindows())
                return null;
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                logger.LogWarning("Radio Display: DirectShow MJPEG open skipped (capture thread is not STA)");
                return null;
            }

            var names = WindowsDshowDevices.ListFriendlyNames();
            var friendly = names.TryGetValue(dshowIndex, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n.Trim()
                : $"Camera {dshowIndex}";

            object? graphObj = null;
            object? captureObj = null;
            object? grabberObj = null;
            object? nullObj = null;
            List<StreamCap>? caps = null;
            var opened = false;
            try
            {
                captureObj = WindowsDshowDevices.BindFilterAt(dshowIndex);
                if (captureObj is null)
                {
                    logger.LogWarning(
                        "Radio Display: DirectShow BindToObject failed for index {Index} '{Name}'",
                        dshowIndex, friendly);
                    return null;
                }

                var graphType = Type.GetTypeFromCLSID(ClsidFilterGraph, throwOnError: false);
                var grabberType = Type.GetTypeFromCLSID(ClsidSampleGrabber, throwOnError: false);
                var nullType = Type.GetTypeFromCLSID(ClsidNullRenderer, throwOnError: false);
                if (graphType is null || grabberType is null || nullType is null)
                {
                    logger.LogWarning(
                        "Radio Display: DirectShow Sample Grabber is not registered (qedit) — cannot passthrough MJPEG");
                    return null;
                }

                graphObj = Activator.CreateInstance(graphType);
                grabberObj = Activator.CreateInstance(grabberType);
                nullObj = Activator.CreateInstance(nullType);
                if (graphObj is null || grabberObj is null || nullObj is null)
                    return null;

                var graph = (IGraphBuilder)graphObj;
                var control = (IMediaControl)graphObj;
                var capture = (IBaseFilter)captureObj;
                var grabberFilter = (IBaseFilter)grabberObj;
                var nullFilter = (IBaseFilter)nullObj;
                var grabber = (ISampleGrabber)grabberObj;

                var hr = graph.AddFilter(capture, "YWC Capture");
                if (hr < 0)
                {
                    logger.LogWarning("Radio Display: IGraphBuilder.AddFilter(capture) hr=0x{Hr:X8}", hr);
                    return null;
                }

                hr = graph.AddFilter(grabberFilter, "YWC Grabber");
                if (hr < 0)
                {
                    logger.LogWarning("Radio Display: IGraphBuilder.AddFilter(grabber) hr=0x{Hr:X8}", hr);
                    return null;
                }

                hr = graph.AddFilter(nullFilter, "YWC Null");
                if (hr < 0)
                    return null;

                var capturePin = FindPin(capture, PinDirOutput, requireStreamConfig: true)
                    ?? FindPin(capture, PinDirOutput, requireStreamConfig: false);
                if (capturePin is null)
                {
                    logger.LogWarning("Radio Display: '{Name}' has no DirectShow output pin", friendly);
                    return null;
                }

                var streamConfig = QueryStreamConfig(capturePin);
                if (streamConfig is null)
                {
                    logger.LogWarning("Radio Display: '{Name}' capture pin has no IAMStreamConfig", friendly);
                    return null;
                }

                caps = EnumerateCaps(streamConfig, out var formats);
                logger.LogInformation(
                    "Radio Display: '{Name}' DirectShow pins: {Formats}",
                    friendly,
                    formats.Count == 0 ? "(none reported)" : string.Join(", ", formats));

                var pins = caps.Select(c => c.Pin).ToList();
                VideoDeviceSizeCaps.RememberPins(deviceKey, pins);

                // Order matters. The env var is the diagnostic override and
                // outranks everything. The operator's chosen size comes next —
                // above preferNativeMode, the automatic recovery from a device
                // caught merging frames. That looks backwards until you try it:
                // the latch is process-lifetime and my VIXLW dongle trips it on
                // the first open, after which every mode in the dropdown
                // reopened as 1920x1080 and the control did nothing but move
                // the downscale width. A control that silently ignores you is
                // worse than one that lets you pick a mode that merges — and
                // the merged samples are dropped either way.
                var chosen = ForcedPin(pins, logger)
                    ?? RequestedPin(pins, requestedSize, targetFps, logger)
                    ?? (preferNativeMode ? VideoCapturePinRank.PickNativeMjpeg(pins) : null)
                    ?? VideoCapturePinRank.PickMjpegCapture(pins, targetFps, maxWidth);
                if (chosen is null)
                {
                    logger.LogInformation(
                        "Radio Display: '{Name}' DirectShow has no MJPEG pin — falling back",
                        friendly);
                    noUsableMjpegPin = true;
                    return null;
                }

                StreamCap? match = null;
                foreach (var cap in caps)
                {
                    if (cap.Pin.Width != chosen.Value.Width ||
                        cap.Pin.Height != chosen.Value.Height ||
                        !cap.Pin.Jpeg)
                        continue;
                    if (match is not null &&
                        Math.Abs(cap.Pin.Fps - chosen.Value.Fps) >= Math.Abs(match.Pin.Fps - chosen.Value.Fps))
                        continue;
                    match = cap;
                }

                if (match is null)
                    return null;

                hr = streamConfig.SetFormat(ref match.Type);
                if (hr < 0)
                {
                    logger.LogWarning(
                        "Radio Display: IAMStreamConfig.SetFormat {W}x{H} MJPG@{Fps:0.#} hr=0x{Hr:X8}",
                        match.Pin.Width, match.Pin.Height, match.Pin.Fps, hr);
                    return null;
                }

                var grabType = MjpegHintType();
                hr = grabber.SetMediaType(ref grabType);
                if (hr < 0)
                {
                    logger.LogWarning("Radio Display: ISampleGrabber.SetMediaType(MJPG) hr=0x{Hr:X8}", hr);
                    return null;
                }

                grabber.SetOneShot(0);
                grabber.SetBufferSamples(1);

                var grabberIn = FindPin(grabberFilter, PinDirInput, requireStreamConfig: false);
                var grabberOut = FindPin(grabberFilter, PinDirOutput, requireStreamConfig: false);
                var nullIn = FindPin(nullFilter, PinDirInput, requireStreamConfig: false);
                if (grabberIn is null || grabberOut is null || nullIn is null)
                    return null;

                hr = graph.ConnectDirect(capturePin, grabberIn, IntPtr.Zero);
                if (hr < 0)
                {
                    var pmt = AllocMediaTypePtr(ref match.Type);
                    try
                    {
                        hr = graph.ConnectDirect(capturePin, grabberIn, pmt);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pmt);
                    }
                }

                if (hr < 0)
                {
                    logger.LogWarning(
                        "Radio Display: ConnectDirect(capture→grabber) MJPG {W}x{H} hr=0x{Hr:X8}",
                        match.Pin.Width, match.Pin.Height, hr);
                    return null;
                }

                hr = graph.ConnectDirect(grabberOut, nullIn, IntPtr.Zero);
                if (hr < 0)
                {
                    logger.LogWarning("Radio Display: ConnectDirect(grabber→null) hr=0x{Hr:X8}", hr);
                    return null;
                }

                if (!GrabberIsMjpeg(grabber, logger))
                    return null;

                var callback = new GrabberCallback();
                hr = grabber.SetCallback(callback, 1);
                if (hr < 0)
                {
                    logger.LogWarning("Radio Display: ISampleGrabber.SetCallback hr=0x{Hr:X8}", hr);
                    return null;
                }

                try { control.Run(); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Radio Display: IMediaControl.Run failed");
                    return null;
                }

                try
                {
                    control.GetState(2000, out var state);
                    if (state != FilterStateRunning)
                    {
                        logger.LogWarning("Radio Display: DirectShow graph state={State} (want running)", state);
                    }
                }
                catch
                {
                    // Some filters return VFW_S_STATE_INTERMEDIATE; keep going.
                }

                opened = true;
                return new WindowsDshowMjpegSession(
                    graphObj, graph, control, captureObj, grabberObj, nullObj, grabber,
                    callback, caps, match.Pin.Width, match.Pin.Height, match.Pin.Fps, logger);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Radio Display: DirectShow MJPEG open failed");
                return null;
            }
            finally
            {
                if (!opened)
                {
                    TryStop(graphObj);
                    SafeRelease(grabberObj);
                    SafeRelease(nullObj);
                    SafeRelease(captureObj);
                    SafeRelease(graphObj);
                    FreeCaps(caps);
                }
            }
        }

        public bool TrySetFrameRate(int targetFps)
        {
            if (_disposed || targetFps < 1)
                return false;

            var match = VideoCapturePinRank.NearestFps(
                _caps.Select(c => c.Pin).ToList(), Width, Height, jpeg: true, targetFps);
            if (match is null)
                return false;

            StreamCap? cap = null;
            foreach (var c in _caps)
            {
                if (c.Pin.Width != match.Value.Width ||
                    c.Pin.Height != match.Value.Height ||
                    !c.Pin.Jpeg ||
                    Math.Abs(c.Pin.Fps - match.Value.Fps) > 0.01)
                    continue;
                cap = c;
                break;
            }

            if (cap is null)
                return false;

            try { _control.Stop(); }
            catch { /* still try SetFormat */ }

            var capture = (IBaseFilter)_captureObj;
            var pin = FindPin(capture, PinDirOutput, requireStreamConfig: true)
                ?? FindPin(capture, PinDirOutput, requireStreamConfig: false);
            if (pin is null)
            {
                TryRun();
                return false;
            }

            var config = QueryStreamConfig(pin);
            if (config is null)
            {
                TryRun();
                return false;
            }

            var hr = config.SetFormat(ref cap.Type);
            TryRun();
            if (hr < 0)
                return false;

            DeviceFps = cap.Pin.Fps;
            return true;
        }

        public bool TryReadJpeg(out byte[]? jpeg)
        {
            jpeg = null;
            if (_disposed)
                return false;
            if (!_frameReady.WaitOne(ReadWaitMs))
                return false;

            lock (_frameLock)
            {
                jpeg = _latestJpeg;
                _latestJpeg = null;
                return jpeg is { Length: > 0 };
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _callback.Owner = null;
            try { _grabber.SetCallback(null, 0); } catch { /* ignore */ }
            TryStop(_graphObj);
            FreeCaps(_caps);
            SafeRelease(_grabberObj);
            SafeRelease(_nullObj);
            SafeRelease(_captureObj);
            SafeRelease(_graphObj);
            _frameReady.Dispose();
        }

        private void OnFrame(byte[] jpeg)
        {
            lock (_frameLock)
                _latestJpeg = jpeg;
            try { _frameReady.Set(); } catch { /* disposed */ }
        }

        private void TryRun()
        {
            try { _control.Run(); } catch { /* ignore */ }
        }

        private static bool GrabberIsMjpeg(ISampleGrabber grabber, ILogger logger)
        {
            var mt = new AMMediaType();
            var hr = grabber.GetConnectedMediaType(ref mt);
            try
            {
                if (hr < 0)
                {
                    logger.LogWarning("Radio Display: GetConnectedMediaType hr=0x{Hr:X8}", hr);
                    return false;
                }

                if (mt.subType != MediaSubtypeMjpg)
                {
                    logger.LogWarning(
                        "Radio Display: Sample Grabber connected as {Sub} — decoder inserted, not MJPEG passthrough",
                        FourCcFromSubtype(mt.subType));
                    return false;
                }

                return true;
            }
            finally
            {
                FreeMediaType(ref mt);
            }
        }

        private static AMMediaType MjpegHintType() => new()
        {
            majorType = MediaTypeVideo,
            subType = MediaSubtypeMjpg,
            formatType = Guid.Empty
        };

        private static IAMStreamConfig? QueryStreamConfig(IPin pin)
        {
            var p = Marshal.GetComInterfaceForObject(pin, typeof(IPin));
            try
            {
                var iid = IidAmStreamConfig;
                var hr = Marshal.QueryInterface(p, in iid, out var cfg);
                if (hr < 0 || cfg == IntPtr.Zero)
                    return null;
                try
                {
                    return (IAMStreamConfig)Marshal.GetObjectForIUnknown(cfg);
                }
                finally
                {
                    Marshal.Release(cfg);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.Release(p);
            }
        }

        private static IPin? FindPin(IBaseFilter filter, int direction, bool requireStreamConfig)
        {
            if (filter.EnumPins(out var enumerator) < 0 || enumerator is null)
                return null;

            try
            {
                enumerator.Reset();
                while (enumerator.Next(1, out var pin, IntPtr.Zero) == 0 && pin is not null)
                {
                    if (pin.QueryDirection(out var dir) < 0 || dir != direction)
                    {
                        SafeRelease(pin);
                        continue;
                    }

                    if (!requireStreamConfig)
                        return pin;

                    if (QueryStreamConfig(pin) is not null)
                        return pin;

                    SafeRelease(pin);
                }
            }
            finally
            {
                SafeRelease(enumerator);
            }

            return null;
        }

        private static List<StreamCap> EnumerateCaps(IAMStreamConfig config, out List<string> formats)
        {
            formats = [];
            var list = new List<StreamCap>();
            if (config.GetNumberOfCapabilities(out var count, out var capSize) < 0 || count <= 0)
                return list;

            if (capSize < 64)
                capSize = 256;
            var scc = Marshal.AllocCoTaskMem(capSize);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    if (config.GetStreamCaps(i, out var pmt, scc) < 0 || pmt == IntPtr.Zero)
                        continue;

                    var mt = Marshal.PtrToStructure<AMMediaType>(pmt)!;
                    Marshal.FreeCoTaskMem(pmt);
                    if (!TryReadVideo(mt, out var width, out var height, out var fps))
                    {
                        FreeMediaType(ref mt);
                        continue;
                    }

                    var jpeg = mt.subType == MediaSubtypeMjpg;
                    formats.Add($"{FourCcFromSubtype(mt.subType)} {width}x{height}@{fps:0.#}");
                    if (width < 2 || height < 2)
                    {
                        FreeMediaType(ref mt);
                        continue;
                    }

                    list.Add(new StreamCap
                    {
                        Pin = new VideoCapturePinRank.Pin(width, height, fps, jpeg),
                        Type = mt
                    });
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(scc);
            }

            return list;
        }

        private static bool TryReadVideo(AMMediaType mt, out int width, out int height, out double fps)
        {
            width = 0;
            height = 0;
            fps = 0;
            if (mt.formatPtr == IntPtr.Zero || mt.formatSize < 56)
                return false;

            var bmiOff = mt.formatType == FormatVideoInfo2 ? 72
                : mt.formatType == FormatVideoInfo ? 48
                : mt.formatSize >= 72 ? 72 : 48;
            if (mt.formatSize < bmiOff + 16)
                return false;

            var avg = Marshal.ReadInt64(mt.formatPtr, 40);
            width = Marshal.ReadInt32(mt.formatPtr, bmiOff + 4);
            height = Math.Abs(Marshal.ReadInt32(mt.formatPtr, bmiOff + 8));
            fps = avg > 0 ? 10_000_000.0 / avg : 0;
            return width >= 2 && height >= 2;
        }

        private static IntPtr AllocMediaTypePtr(ref AMMediaType mt)
        {
            var p = Marshal.AllocCoTaskMem(Marshal.SizeOf<AMMediaType>());
            Marshal.StructureToPtr(mt, p, false);
            return p;
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

        private static void FreeCaps(List<StreamCap>? caps)
        {
            if (caps is null)
                return;
            foreach (var c in caps)
            {
                var mt = c.Type;
                FreeMediaType(ref mt);
            }

            caps.Clear();
        }

        private static void FreeMediaType(ref AMMediaType mt)
        {
            if (mt.formatSize != 0 && mt.formatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mt.formatPtr);
                mt.formatPtr = IntPtr.Zero;
                mt.formatSize = 0;
            }

            if (mt.unk != IntPtr.Zero)
            {
                Marshal.Release(mt.unk);
                mt.unk = IntPtr.Zero;
            }
        }

        private static void TryStop(object? graphObj)
        {
            if (graphObj is null)
                return;
            try
            {
                var control = (IMediaControl)graphObj;
                control.Stop();
            }
            catch
            {
                // tearing down
            }
        }

        private static void SafeRelease(object? com)
        {
            if (com is null)
                return;
            try { Marshal.ReleaseComObject(com); } catch { /* ignore */ }
        }

        private sealed class StreamCap
        {
            public required VideoCapturePinRank.Pin Pin { get; init; }
            public AMMediaType Type;
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        [ComDefaultInterface(typeof(ISampleGrabberCB))]
        private sealed class GrabberCallback : ISampleGrabberCB
        {
            public WindowsDshowMjpegSession? Owner;

            [PreserveSig]
            public int SampleCB(double sampleTime, IntPtr sample) => 0;

            [PreserveSig]
            public int BufferCB(double sampleTime, IntPtr buffer, int bufferLen)
            {
                var owner = Owner;
                if (owner is null || buffer == IntPtr.Zero || bufferLen < 4)
                    return 0;
                if (Marshal.ReadByte(buffer) != 0xFF || Marshal.ReadByte(buffer, 1) != 0xD8)
                {
                    owner.NoteSampleTrace(sampleTime, bufferLen, 0, "no SOI, dropped");
                    return 0;
                }

                var bytes = new byte[bufferLen];
                Marshal.Copy(buffer, bytes, 0, bufferLen);

                // Trim to the end of the FIRST image, found by walking markers.
                // Scanning backwards for FF D9 takes the LAST EOI in the buffer,
                // which appends anything sitting past the real frame end — the
                // decoder then renders that tail as extra image content.
                var end = FindFirstImageEnd(bytes, out var hasScan);
                byte[] jpeg;
                if (end > 0)
                {
                    // A terminated image with no scan carries no pixels — some
                    // grabbers emit a bare FF D8 FF D9 after a replug until the
                    // source relocks. Publishing those keeps the frame counter
                    // and the status badge running over a picture that can never
                    // appear, so drop them and let the stall watchdog say so.
                    if (!hasScan)
                    {
                        owner.NoteSampleTrace(sampleTime, bufferLen, end, "no scan, dropped");
                        owner.NoteImagelessSample(bytes.Length, end);
                        return 0;
                    }

                    if (end < bytes.Length)
                    {
                        // A tail holding another SOI means the driver put more than
                        // one picture in this sample, and the first one is itself a
                        // tiled repeat. Never show those. A tail without an SOI is
                        // only padding, so trim it and publish as normal.
                        if (TailHasSoi(bytes, end))
                        {
                            owner.NoteSampleTrace(sampleTime, bufferLen, end, "MERGED, dropped");
                            owner.NoteMergedSample(bytes, end);
                            return 0;
                        }

                        owner.NoteSampleTrace(sampleTime, bufferLen, end, "padded tail, trimmed");
                        owner.NoteTrailingBytes(bytes, end);
                    }
                    else
                    {
                        owner.NoteSampleTrace(sampleTime, bufferLen, end, "clean");
                    }

                    jpeg = end == bytes.Length ? bytes : bytes.AsSpan(0, end).ToArray();
                }
                else
                {
                    owner.NoteSampleTrace(sampleTime, bufferLen, 0, "no EOI, published whole");
                    owner.NoteUnterminatedFrame(bytes.Length);
                    jpeg = bytes;
                }

                owner.OnFrame(jpeg);
                return 0;
            }
        }

        /// <summary>True when the bytes after the first image begin another JPEG.</summary>
        private static bool TailHasSoi(byte[] bytes, int end)
        {
            for (var i = end; i < bytes.Length - 1; i++)
            {
                if (bytes[i] == 0xFF && bytes[i + 1] == 0xD8)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The operator's Radio Display capture size, if they set one and the
        /// device still offers it as MJPEG. A stale choice logs and falls
        /// through to the ranked pick rather than refusing to open.
        /// </summary>
        private static VideoCapturePinRank.Pin? RequestedPin(
            IReadOnlyList<VideoCapturePinRank.Pin> pins,
            string? requestedSize,
            int targetFps,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(requestedSize))
                return null;

            var pick = VideoCapturePinRank.PickRequestedSize(pins, requestedSize, targetFps);
            if (pick is null)
            {
                logger.LogWarning(
                    "Radio Display: capture size {Size} has no MJPEG pin on this device — using automatic",
                    requestedSize);
                return null;
            }

            logger.LogInformation(
                "Radio Display: capture size {W}x{H}@{Fps} selected in Settings",
                pick.Value.Width, pick.Value.Height, pick.Value.Fps);
            return pick;
        }

        /// <summary>
        /// Diagnostic override: YWC_VIDEO_FORCE_PIN=WxH pins the capture to that
        /// MJPEG mode instead of the ranked pick. Temporary — it exists to test
        /// which modes this grabber can actually render cleanly.
        /// </summary>
        private static VideoCapturePinRank.Pin? ForcedPin(
            IReadOnlyList<VideoCapturePinRank.Pin> pins, ILogger logger)
        {
            var spec = Environment.GetEnvironmentVariable("YWC_VIDEO_FORCE_PIN");
            if (string.IsNullOrWhiteSpace(spec))
                return null;

            var parts = spec.Split('x', 'X');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var w) ||
                !int.TryParse(parts[1], out var h))
            {
                logger.LogWarning("Radio Display: YWC_VIDEO_FORCE_PIN='{Spec}' not WxH — ignored", spec);
                return null;
            }

            VideoCapturePinRank.Pin? best = null;
            foreach (var p in pins)
            {
                if (!p.Jpeg || p.Width != w || p.Height != h)
                    continue;
                if (best is null || p.Fps > best.Value.Fps)
                    best = p;
            }

            if (best is null)
                logger.LogWarning("Radio Display: YWC_VIDEO_FORCE_PIN={Spec} has no MJPEG pin — ignored", spec);
            else
                logger.LogWarning("Radio Display: YWC_VIDEO_FORCE_PIN={Spec} — forcing {W}x{H}@{Fps}",
                    spec, best.Value.Width, best.Value.Height, best.Value.Fps);
            return best;
        }

        /// <summary>
        /// Index just past the EOI of the first complete JPEG in <paramref name="bytes"/>,
        /// or -1 if the buffer holds no terminated image. Walks the marker structure
        /// rather than searching for FF D9: that byte pair occurs freely inside marker
        /// segment payloads (an APPn thumbnail is itself a JPEG and ends in one), so a
        /// naive search can stop short or run long. <paramref name="hasScan"/> reports
        /// whether that image actually contained entropy-coded pixel data.
        /// </summary>
        private static int FindFirstImageEnd(byte[] bytes, out bool hasScan)
        {
            hasScan = false;
            if (bytes.Length < 2 || bytes[0] != 0xFF || bytes[1] != 0xD8)
                return -1;

            var i = 2;
            while (i < bytes.Length - 1)
            {
                if (bytes[i] != 0xFF) { i++; continue; }

                var marker = bytes[i + 1];
                if (marker == 0xFF) { i++; continue; }              // fill byte
                if (marker == 0xD9) return i + 2;                   // EOI
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    i += 2;                                          // standalone
                    continue;
                }

                if (i + 4 > bytes.Length) return -1;
                var segmentLength = (bytes[i + 2] << 8) | bytes[i + 3];
                if (segmentLength < 2) return -1;

                if (marker == 0xDA)
                {
                    hasScan = true;
                    // Start of scan: entropy-coded data follows. FF inside it is
                    // either stuffed (FF 00) or a restart marker; anything else
                    // ends the scan.
                    var j = i + 2 + segmentLength;
                    while (j < bytes.Length - 1)
                    {
                        if (bytes[j] == 0xFF)
                        {
                            var next = bytes[j + 1];
                            if (next != 0x00 && !(next >= 0xD0 && next <= 0xD7))
                                break;
                        }
                        j++;
                    }

                    if (j >= bytes.Length - 1) return -1;
                    i = j;
                    continue;
                }

                i += 2 + segmentLength;
            }

            return -1;
        }

        /// <summary>
        /// Records a sample that carried bytes past the end of its first image.
        /// Logged sparsely: this is diagnostic evidence for an intermittent
        /// corruption, not a per-frame condition worth flooding the log with.
        /// </summary>
        /// <summary>
        /// Traces the opening <see cref="SampleTraceCount"/> samples of a session,
        /// only when YWC_VIDEO_TRACE_SAMPLES is set. #132: merged samples always
        /// start within ~3 s of the graph running and stop on their own, and the
        /// byte counts repeat exactly across independent cold opens - 29087 /
        /// 28262 / 825 at 800x600, 110480 / 107862 / 2618 at 1280x960. A copy race
        /// would tear at a different point every time, so this says whether the
        /// burst is literally the first N samples in sequence, which is what a
        /// priming or stale buffer looks like.
        /// </summary>
        private void NoteSampleTrace(double sampleTime, int bufferLen, int end, string outcome)
        {
            if (!_traceSamples)
                return;

            var n = Interlocked.Increment(ref _samplesSeen);
            if (n > SampleTraceCount)
                return;

            _logger.LogInformation(
                "Radio Display trace: sample {N} at {Elapsed} ms, stamp {Stamp} s, " +
                "{Length} bytes, first image {End} bytes, {Outcome}",
                n, _sinceOpen.ElapsedMilliseconds, sampleTime.ToString("F4"), bufferLen, end, outcome);
        }

        private void NoteTrailingBytes(byte[] bytes, int end)
        {
            var occurrence = Interlocked.Increment(ref _trailingSamples);
            if (occurrence != 1 && occurrence % 100 != 0)
                return;

            _logger.LogWarning(
                "Radio Display: DirectShow sample carried {Extra} padding bytes past the first JPEG " +
                "(sample {Total} bytes, occurrence {Occurrence}). Trimmed to the first image. " +
                "Tail starts {Tail}",
                bytes.Length - end, bytes.Length, occurrence, TailPreview(bytes, end));

            if (occurrence == 1)
                DumpSample(bytes, end, "padded");
        }

        /// <summary>
        /// Records a dropped sample that held more than one picture. A mode the
        /// device renders correctly produces none of these at all, so a handful is
        /// already conclusive rather than borderline.
        /// </summary>
        private void NoteMergedSample(byte[] bytes, int end)
        {
            var occurrence = Interlocked.Increment(ref _mergedSamples);
            if (occurrence >= MergedFrameEvidence)
                Volatile.Write(ref _mergesFrames, 1);

            if (occurrence != 1 && occurrence % 100 != 0)
                return;

            _logger.LogWarning(
                "Radio Display: DirectShow sample held more than one picture " +
                "(sample {Total} bytes, first image {First} bytes, tail {Tail} bytes, " +
                "occurrence {Occurrence}). Dropped — the device is merging frames. " +
                "Tail starts {Preview}",
                bytes.Length, end, bytes.Length - end, occurrence, TailPreview(bytes, end));

            if (occurrence == 1)
                DumpSample(bytes, end, "merged");
        }

        /// <summary>
        /// First bytes of whatever followed the first image, as hex.
        /// </summary>
        /// <remarks>
        /// Dumped and decoded on 2026-08-23 (issue #132). The tail is a real
        /// second image: a complete JPEG header — DQT, DQT, SOF0 at the same
        /// size as the frame in front of it, four DHTs, SOS — followed by
        /// scan data and no EOI. So it is the *beginning of the next frame,
        /// truncated*, and <see cref="TailHasSoi"/> is right to treat the
        /// sample as unusable. Decoding the leading image confirmed it too:
        /// it renders as the green striped corruption users report.
        ///
        /// The tail size is constant per mode — 2618 bytes at 1280x960 and
        /// 1920x1080, 1827 at 1280x720, 825 at 800x600 — while the image in
        /// front of it varies by 10 KB. ffmpeg reading the same device in the
        /// same modes saw no merged sample in 360 frames across six cold
        /// opens, and no corruption. Working theory is therefore that the
        /// sample buffer is being torn: the driver is still writing the next
        /// frame while <c>BufferCB</c> copies, and 2618 bytes is simply how
        /// far ahead the writer is. If that holds, <see cref="TailHasSoi"/>
        /// only catches a torn frame when the torn-in region happens to begin
        /// with an SOI, and the rest reach the browser unflagged.
        /// </remarks>
        private static string TailPreview(byte[] bytes, int end, int count = 32)
        {
            var available = Math.Min(count, bytes.Length - end);
            return available <= 0 ? "(none)" : Convert.ToHexString(bytes, end, available);
        }

        /// <summary>
        /// Writes the whole sample and its tail to the user data folder, but
        /// only when YWC_VIDEO_DUMP_TAIL is set. Off by default: this is a few
        /// hundred kilobytes a call and is only ever wanted while diagnosing.
        /// Fires once per session, on the first occurrence.
        /// </summary>
        private void DumpSample(byte[] bytes, int end, string kind)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YWC_VIDEO_DUMP_TAIL")))
                return;

            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MM5AGM",
                    "Yaesu Web Control");
                Directory.CreateDirectory(folder);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                var samplePath = Path.Combine(folder, $"video-{kind}-{stamp}-sample.bin");
                var tailPath = Path.Combine(folder, $"video-{kind}-{stamp}-tail.bin");
                File.WriteAllBytes(samplePath, bytes);
                File.WriteAllBytes(tailPath, bytes[end..]);

                _logger.LogWarning(
                    "Radio Display: wrote {Sample} and {Tail} for diagnosis",
                    samplePath, tailPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Radio Display: could not write the sample dump");
            }
        }

        /// <summary>Records a dropped sample whose first image held no scan data.</summary>
        private void NoteImagelessSample(int length, int end)
        {
            var occurrence = Interlocked.Increment(ref _imagelessSamples);
            if (occurrence != 1 && occurrence % 100 != 0)
                return;

            _logger.LogWarning(
                "Radio Display: DirectShow sample of {Length} bytes held a {ImageLength}-byte JPEG " +
                "with no image data (occurrence {Occurrence}). Dropped — the capture device is " +
                "delivering empty frames.",
                length, end, occurrence);
        }

        /// <summary>Records a sample with no terminated image; forwarded unchanged.</summary>
        private void NoteUnterminatedFrame(int length)
        {
            var occurrence = Interlocked.Increment(ref _unterminatedSamples);
            if (occurrence != 1 && occurrence % 100 != 0)
                return;

            _logger.LogWarning(
                "Radio Display: DirectShow sample of {Length} bytes had no complete JPEG " +
                "(occurrence {Occurrence}). Forwarded unchanged.",
                length, occurrence);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AMMediaType
        {
            public Guid majorType;
            public Guid subType;
            [MarshalAs(UnmanagedType.Bool)] public bool fixedSizeSamples;
            [MarshalAs(UnmanagedType.Bool)] public bool temporalCompression;
            public int sampleSize;
            public Guid formatType;
            public IntPtr unk;
            public int formatSize;
            public IntPtr formatPtr;
        }

        [ComImport]
        [Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphBuilder
        {
            [PreserveSig] int AddFilter(IBaseFilter pFilter, [MarshalAs(UnmanagedType.LPWStr)] string pName);
            [PreserveSig] int RemoveFilter(IBaseFilter pFilter);
            [PreserveSig] int EnumFilters(out IntPtr ppEnum);
            [PreserveSig] int FindFilterByName([MarshalAs(UnmanagedType.LPWStr)] string pName, out IBaseFilter ppFilter);
            [PreserveSig] int ConnectDirect(IPin ppinOut, IPin ppinIn, IntPtr pmt);
            [PreserveSig] int Reconnect(IPin ppin);
            [PreserveSig] int Disconnect(IPin ppin);
            [PreserveSig] int SetDefaultSyncSource();
            [PreserveSig] int Connect(IPin ppinOut, IPin ppinIn);
            [PreserveSig] int Render(IPin ppinOut);
            [PreserveSig] int RenderFile([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.LPWStr)] string? playlist);
            [PreserveSig] int AddSourceFilter([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.LPWStr)] string filterName, out IBaseFilter ppFilter);
            [PreserveSig] int SetLogFile(IntPtr hFile);
            [PreserveSig] int Abort();
            [PreserveSig] int ShouldOperationContinue();
        }

        [ComImport]
        [Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770")]
        [InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IMediaControl
        {
            void Run();
            void Pause();
            void Stop();
            void GetState(int msTimeout, out int pfs);
            void RenderFile(string strFilename);
            void AddSourceFilter(string strFilename, [MarshalAs(UnmanagedType.IDispatch)] out object ppUnk);
            void get_FilterCollection([MarshalAs(UnmanagedType.IDispatch)] out object ppUnk);
            void get_RegFilterCollection([MarshalAs(UnmanagedType.IDispatch)] out object ppUnk);
            void StopWhenReady();
        }

        [ComImport]
        [Guid("56a86895-0ad4-11ce-b03a-0020af0ba770")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IBaseFilter
        {
            [PreserveSig] int GetClassID(out Guid pClassID);
            [PreserveSig] int Stop();
            [PreserveSig] int Pause();
            [PreserveSig] int Run(long tStart);
            [PreserveSig] int GetState(int dwMilliSecsTimeout, out int filtState);
            [PreserveSig] int SetSyncSource(IntPtr pClock);
            [PreserveSig] int GetSyncSource(out IntPtr pClock);
            [PreserveSig] int EnumPins(out IEnumPins ppEnum);
            [PreserveSig] int FindPin([MarshalAs(UnmanagedType.LPWStr)] string Id, out IPin ppPin);
            [PreserveSig] int QueryFilterInfo(IntPtr pInfo);
            [PreserveSig] int JoinFilterGraph(IntPtr pGraph, [MarshalAs(UnmanagedType.LPWStr)] string? pName);
            [PreserveSig] int QueryVendorInfo([MarshalAs(UnmanagedType.LPWStr)] out string pVendorInfo);
        }

        [ComImport]
        [Guid("56a86892-0ad4-11ce-b03a-0020af0ba770")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumPins
        {
            [PreserveSig] int Next(int cPins, out IPin ppPin, IntPtr pcFetched);
            [PreserveSig] int Skip(int cPins);
            [PreserveSig] int Reset();
            [PreserveSig] int Clone(out IEnumPins ppEnum);
        }

        [ComImport]
        [Guid("56a86891-0ad4-11ce-b03a-0020af0ba770")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPin
        {
            [PreserveSig] int Connect(IPin receivePin, IntPtr pmt);
            [PreserveSig] int ReceiveConnection(IPin connector, IntPtr pmt);
            [PreserveSig] int Disconnect();
            [PreserveSig] int ConnectedTo(out IPin pin);
            [PreserveSig] int ConnectionMediaType(IntPtr pmt);
            [PreserveSig] int QueryPinInfo(IntPtr pInfo);
            [PreserveSig] int QueryDirection(out int pinDir);
            [PreserveSig] int QueryId(out IntPtr id);
            [PreserveSig] int QueryAccept(IntPtr pmt);
            [PreserveSig] int EnumMediaTypes(out IntPtr ppEnum);
            [PreserveSig] int QueryInternalConnections(IntPtr ppPins, ref int nPin);
            [PreserveSig] int EndOfStream();
            [PreserveSig] int BeginFlush();
            [PreserveSig] int EndFlush();
            [PreserveSig] int NewSegment(long tStart, long tStop, double dRate);
        }

        [ComImport]
        [Guid("C6E13340-30AC-11d0-A18C-00A0C9118956")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAMStreamConfig
        {
            [PreserveSig] int SetFormat(ref AMMediaType pmt);
            [PreserveSig] int GetFormat(out IntPtr ppmt);
            [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
            [PreserveSig] int GetStreamCaps(int index, out IntPtr pmt, IntPtr pSCC);
        }

        [ComImport]
        [Guid("6B652FFF-11FE-4fce-92AD-0266B5D7C78F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISampleGrabber
        {
            [PreserveSig] int SetOneShot(int oneShot);
            [PreserveSig] int SetMediaType(ref AMMediaType pmt);
            [PreserveSig] int GetConnectedMediaType(ref AMMediaType pmt);
            [PreserveSig] int SetBufferSamples(int bufferThem);
            [PreserveSig] int GetCurrentBuffer(ref int pBufferSize, IntPtr pBuffer);
            [PreserveSig] int GetCurrentSample(out IntPtr ppSample);
            [PreserveSig] int SetCallback(ISampleGrabberCB? callback, int whichMethodToCallback);
        }

        [ComImport]
        [Guid("0579154A-2B1F-486c-9F0A-97044DC6C7B8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISampleGrabberCB
        {
            [PreserveSig] int SampleCB(double sampleTime, IntPtr sample);
            [PreserveSig] int BufferCB(double sampleTime, IntPtr buffer, int bufferLen);
        }
    }
}
