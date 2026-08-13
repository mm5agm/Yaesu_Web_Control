using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenCvSharp;
using Yaesu_Web_Control.Models;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Single shared capture/encode loop. Opens the UVC device only while
    /// viewers are attached; publishes the latest JPEG for MJPEG fan-out.
    /// </summary>
    public sealed class VideoCaptureService : IDisposable
    {
        /// <summary>
        /// HDMI capture dongles (DirectShow / Media Foundation) native-crash
        /// the host if a second Open happens while the first graph is still
        /// tearing down. Pop-out / Hide briefly drop the last viewer — keep
        /// the device open across that gap.
        /// </summary>
        private const int IdleReleaseDelayMs = 2000;

        /// <summary>Settle time after Release before the next Open.</summary>
        private const int ReopenSettleMs = 500;

        private const int StopWaitMs = 8000;
        private const int SettingsRefreshMs = 500;
        private const int EmptyFrameGiveUp = 45;

        /// <summary>
        /// Default Windows timer quantum. <see cref="Thread.Sleep(int)"/> of 1 ms
        /// still waits this long unless <c>timeBeginPeriod(1)</c> is active, so
        /// leftover waits at or below this must spin or they land on ~20 fps.
        /// </summary>
        private const int TimerQuantumMs = 16;

        /// <summary>
        /// DirectShow often returns success with a stale non-empty frame after
        /// unplug, so <see cref="Mat.Empty"/> never trips. A Read that blocks
        /// this long is the usual symptom (measured FPS collapses toward 1).
        /// </summary>
        private const int SlowReadMs = 750;
        private const int SlowReadGiveUp = 4;
        private const int FpsCollapseWindows = 3;
        private const int FpsWarmupMs = 2500;

        private readonly ISettingsService _settings;
        private readonly VideoSessionManager _sessions;
        private readonly ILogger<VideoCaptureService> _logger;
        private readonly object _frameLock = new();
        private readonly object _lifecycleLock = new();

        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private CancellationTokenSource? _idleCts;
        private byte[]? _latestJpeg;
        private long _frameSeq;
        private int _frameWidth;
        private int _frameHeight;
        private double _measuredFps;
        private string _status = "idle";
        private string? _lastError;
        private int _openDeviceIndex = -1;
        private int _deviceOpenFlag;
        private bool _mfUnavailable;
        private TaskCompletionSource<bool> _framePulse = NewFramePulse();

        public VideoCaptureService(
            ISettingsService settings,
            VideoSessionManager sessions,
            ILogger<VideoCaptureService> logger)
        {
            _settings = settings;
            _sessions = sessions;
            _logger = logger;
        }

        public string Status => Volatile.Read(ref _status!);
        public string? LastError => _lastError;
        public int FrameWidth { get { lock (_frameLock) return _frameWidth; } }
        public int FrameHeight { get { lock (_frameLock) return _frameHeight; } }
        public double MeasuredFps => Volatile.Read(ref _measuredFps);
        public int ViewerCount => _sessions.ViewerCount;

        /// <summary>True while the native capture graph is open.</summary>
        public bool IsCapturing => Volatile.Read(ref _deviceOpenFlag) != 0;

        /// <summary>Currently opened OpenCV index, or null when idle.</summary>
        public int? OpenDeviceIndex
        {
            get
            {
                var idx = Volatile.Read(ref _openDeviceIndex);
                return idx >= 0 ? idx : null;
            }
        }

        public byte[]? LatestJpeg
        {
            get { lock (_frameLock) return _latestJpeg; }
        }

        public long FrameSeq
        {
            get { lock (_frameLock) return _frameSeq; }
        }

        /// <summary>
        /// Register a viewer and ensure the capture loop is running.
        /// </summary>
        public async Task<string> AcquireViewerAsync(string viewerId, CancellationToken ct)
        {
            var settings = await _settings.GetSettingsAsync();
            if (!settings.VideoDisplayEnabled)
                throw new InvalidOperationException("Radio Display is disabled in Settings.");

            if (string.IsNullOrWhiteSpace(settings.VideoCaptureDeviceKey))
                throw new InvalidOperationException("No video capture device selected in Settings.");

            _sessions.TryAcquire(viewerId, out _);
            CancelIdleRelease();
            EnsureLoopStarted();
            return viewerId;
        }

        public void ReleaseViewer(string viewerId)
        {
            if (_sessions.Release(viewerId, out var remaining) && remaining == 0)
                ScheduleIdleRelease();
        }

        /// <summary>
        /// Cancel and reopen the capture loop (e.g. after the device key changes).
        /// No-op when no viewers are attached.
        /// </summary>
        public void RequestRestart()
        {
            CancelIdleRelease();
            StopLoopAndWait();
            if (_sessions.ViewerCount > 0)
                EnsureLoopStarted();
        }

        /// <summary>
        /// Wait until a newer frame than <paramref name="afterSeq"/> is available.
        /// </summary>
        public async Task<(byte[] jpeg, long seq)?> WaitForFrameAsync(long afterSeq, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                lock (_frameLock)
                {
                    if (_latestJpeg != null && _frameSeq > afterSeq)
                        return (_latestJpeg, _frameSeq);
                }

                if (sw.ElapsedMilliseconds > 5000)
                    return null;

                var pulse = Volatile.Read(ref _framePulse);
                try
                {
                    await pulse.Task.WaitAsync(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Re-check under the lock; the pulse may have been swapped.
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }

            return null;
        }

        private void EnsureLoopStarted()
        {
            lock (_lifecycleLock)
            {
                if (_loopTask is { IsCompleted: false })
                    return;

                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(() =>
                {
                    try
                    {
                        RunCaptureLoop(token);
                        tcs.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Radio Display capture thread failed");
                        tcs.TrySetException(ex);
                    }
                })
                {
                    IsBackground = true,
                    Name = "YWC-RadioDisplay"
                };

                // DirectShow graphs are STA; opening/releasing them on a
                // thread-pool MTA is a common native-crash on HDMI dongles.
                if (OperatingSystem.IsWindows())
                {
                    try { thread.SetApartmentState(ApartmentState.STA); }
                    catch (InvalidOperationException) { /* ignore */ }
                }

                thread.Start();
                _loopTask = tcs.Task;
            }
        }

        private void ScheduleIdleRelease()
        {
            CancelIdleRelease();
            var cts = new CancellationTokenSource();
            _idleCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(IdleReleaseDelayMs, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_sessions.ViewerCount == 0)
                    StopLoopAndWait();
            });
        }

        private void CancelIdleRelease()
        {
            try { _idleCts?.Cancel(); } catch { /* ignore */ }
            _idleCts = null;
        }

        private void StopLoopAndWait()
        {
            Task? running;
            lock (_lifecycleLock)
            {
                try { _loopCts?.Cancel(); } catch { /* ignore */ }
                running = _loopTask;
            }

            var finished = running == null;
            if (running != null)
            {
                try { finished = running.Wait(StopWaitMs); }
                catch (AggregateException) { finished = true; }
            }

            lock (_lifecycleLock)
            {
                if (!finished && _loopTask is { IsCompleted: false })
                {
                    _logger.LogWarning(
                        "Radio Display capture loop did not stop within {Ms} ms; not opening another graph",
                        StopWaitMs);
                    return;
                }

                _loopCts = null;
                _loopTask = null;
            }

            if (ReopenSettleMs > 0)
                Thread.Sleep(ReopenSettleMs);

            SetStatus(_sessions.ViewerCount == 0 ? "idle" : "connecting");
            lock (_frameLock)
            {
                _latestJpeg = null;
                // Keep _frameSeq monotonic so in-flight MJPEG clients are not
                // left waiting for a sequence that was reset to zero.
                _frameWidth = 0;
                _frameHeight = 0;
            }
            PulseFrame();
            Volatile.Write(ref _measuredFps, 0);
            _mfUnavailable = false;
            _logger.LogInformation("Radio Display capture stopped");
        }

        private void RunCaptureLoop(CancellationToken ct)
        {
            SetStatus("connecting");
            _lastError = null;
            BeginPreciseTimer();
            try
            {
                RunCaptureLoopCore(ct);
            }
            finally
            {
                EndPreciseTimer();
            }
        }

        private void RunCaptureLoopCore(CancellationToken ct)
        {
            // Stay running across a brief zero-viewer gap (pop-out / Hide).
            // StopLoopAndWait cancels after IdleReleaseDelayMs.
            while (!ct.IsCancellationRequested)
            {
                var settings = ReadSettings();
                if (!settings.VideoDisplayEnabled ||
                    string.IsNullOrWhiteSpace(settings.VideoCaptureDeviceKey) ||
                    !VideoDeviceKey.TryParseIndex(settings.VideoCaptureDeviceKey, out var index))
                {
                    SetStatus("unconfigured");
                    _lastError = "No capture device configured.";
                    SleepOrCancel(1000, ct);
                    continue;
                }

                var maxWidth = settings.VideoMaxWidth < 0 ? 0 : Math.Clamp(settings.VideoMaxWidth, 0, 1920);
                var targetFps = VideoFpsOptions.Normalize(settings.VideoTargetFps);
                var jpegQuality = Math.Clamp(settings.VideoJpegQuality, 40, 85);
                var frameInterval = TimeSpan.FromSeconds(1.0 / targetFps);
                var settingsAge = Stopwatch.StartNew();

                VideoCapture? cap = null;
                try
                {
                    if (OperatingSystem.IsWindows() && !_mfUnavailable && RunMfSession(index, targetFps, ct))
                    {
                        if (ct.IsCancellationRequested)
                            break;
                        SetStatus("connecting");
                        SleepOrCancel(1000, ct);
                        continue;
                    }

                    cap = OpenCapture(index, maxWidth, targetFps, out var jpegPassthrough);
                    if (cap is null || !cap.IsOpened())
                    {
                        MarkDisconnected($"Could not open capture device index {index}.");
                        _logger.LogWarning("Radio Display: failed to open device index {Index}", index);
                        SleepOrCancel(2000, ct);
                        continue;
                    }

                    Volatile.Write(ref _openDeviceIndex, index);
                    Volatile.Write(ref _deviceOpenFlag, 1);

                    SetStatus("streaming");
                    _lastError = null;
                    _logger.LogInformation(
                        "Radio Display streaming device index {Index} (maxW={MaxW}, fps={Fps}, q={Q}, passthrough={Passthrough})",
                        index, maxWidth, targetFps, jpegQuality, jpegPassthrough);

                    using var frame = new Mat();
                    using var resized = new Mat();
                    var encodeParams = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality) };
                    var fpsWindow = Stopwatch.StartNew();
                    var streamStarted = Stopwatch.StartNew();
                    var framesInWindow = 0;
                    var emptyStreak = 0;
                    var slowReadStreak = 0;
                    var fpsCollapseStreak = 0;
                    var loggedFormat = false;

                    while (!ct.IsCancellationRequested)
                    {
                        if (settingsAge.ElapsedMilliseconds >= SettingsRefreshMs)
                        {
                            settings = ReadSettings();
                            maxWidth = settings.VideoMaxWidth < 0 ? 0 : Math.Clamp(settings.VideoMaxWidth, 0, 1920);
                            var newFps = VideoFpsOptions.Normalize(settings.VideoTargetFps);
                            jpegQuality = Math.Clamp(settings.VideoJpegQuality, 40, 85);
                            encodeParams = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality) };
                            if (newFps != targetFps)
                            {
                                targetFps = newFps;
                                frameInterval = TimeSpan.FromSeconds(1.0 / targetFps);
                            }

                            settingsAge.Restart();
                        }

                        if (!settings.VideoDisplayEnabled ||
                            !VideoDeviceKey.TryParseIndex(settings.VideoCaptureDeviceKey, out var newIndex) ||
                            newIndex != index)
                        {
                            break; // reopen with new device / disabled
                        }

                        var tick = Stopwatch.StartNew();
                        var readOk = cap.Read(frame);
                        var readMs = tick.ElapsedMilliseconds;
                        if (!readOk || frame.Empty())
                        {
                            emptyStreak++;
                            if (emptyStreak >= EmptyFrameGiveUp)
                            {
                                MarkDisconnected("Capture device stopped delivering frames.");
                                break;
                            }

                            SleepOrCancel(20, ct);
                            continue;
                        }

                        emptyStreak = 0;

                        var reportW = (int)cap.Get(VideoCaptureProperties.FrameWidth);
                        var reportH = (int)cap.Get(VideoCaptureProperties.FrameHeight);
                        if (reportW <= 1) reportW = frame.Width;
                        if (reportH <= 1) reportH = frame.Height;

                        if (!loggedFormat)
                        {
                            loggedFormat = true;
                            var uncompressedMbps = reportW * reportH * Yuy2BytesPerPixel * targetFps / 1e6;
                            _logger.LogInformation(
                                "Radio Display negotiated {W}x{H} {FourCc} device={DevFps:0.#}fps " +
                                "(encode {Target} fps, maxW={MaxW}, mode={Mode}, uncompressed {Mbps:0.#} MB/s vs ~{Budget:0} MB/s USB2)",
                                reportW,
                                reportH,
                                FormatFourCc(cap.Get(VideoCaptureProperties.FourCC)),
                                cap.Get(VideoCaptureProperties.Fps),
                                targetFps,
                                maxWidth,
                                jpegPassthrough ? "passthrough" : "encode",
                                uncompressedMbps,
                                Usb2UvcBytesPerSecond / 1e6);
                        }

                        // Unplug: DirectShow keeps succeeding with stale frames
                        // but Read blocks (~1 fps). Break out so we reopen.
                        if (readMs >= SlowReadMs)
                        {
                            slowReadStreak++;
                            if (slowReadStreak >= SlowReadGiveUp)
                            {
                                MarkDisconnected("Capture device stalled (no timely frames).");
                                _logger.LogWarning(
                                    "Radio Display: Read took {Ms} ms for {N} frames — treating as unplug",
                                    readMs, slowReadStreak);
                                break;
                            }
                        }
                        else
                        {
                            slowReadStreak = 0;
                        }

                        // Keep reading so the UVC graph stays alive; skip JPEG
                        // when nobody is watching (idle-release grace window).
                        if (_sessions.ViewerCount > 0)
                        {
                            byte[]? jpegBytes = null;
                            var outW = reportW;
                            var outH = reportH;
                            if (jpegPassthrough)
                            {
                                // Dongle already produced JPEG — do not decode/re-encode.
                                if (!TryCopyJpeg(frame, out jpegBytes))
                                {
                                    SleepOrCancel(20, ct);
                                    continue;
                                }
                            }
                            else
                            {
                                Mat source = frame;
                                if (maxWidth > 0 && frame.Width > maxWidth && frame.Channels() >= 3)
                                {
                                    var scale = (double)maxWidth / frame.Width;
                                    var newH = Math.Max(1, (int)Math.Round(frame.Height * scale));
                                    Cv2.Resize(frame, resized, new OpenCvSharp.Size(maxWidth, newH), 0, 0, InterpolationFlags.Linear);
                                    source = resized;
                                }

                                try
                                {
                                    jpegBytes = source.ImEncode(".jpg", encodeParams);
                                }
                                catch
                                {
                                    SleepOrCancel(50, ct);
                                    continue;
                                }

                                outW = source.Width;
                                outH = source.Height;
                            }

                            if (jpegBytes is null || jpegBytes.Length == 0)
                            {
                                SleepOrCancel(50, ct);
                                continue;
                            }

                            lock (_frameLock)
                            {
                                _latestJpeg = jpegBytes;
                                _frameSeq++;
                                _frameWidth = outW;
                                _frameHeight = outH;
                            }

                            PulseFrame();

                            framesInWindow++;
                            if (fpsWindow.ElapsedMilliseconds >= 1000)
                            {
                                var measured = framesInWindow * 1000.0 / fpsWindow.ElapsedMilliseconds;
                                Volatile.Write(ref _measuredFps, measured);
                                framesInWindow = 0;
                                fpsWindow.Restart();

                                var floor = Math.Max(2.0, targetFps * 0.25);
                                if (streamStarted.ElapsedMilliseconds >= FpsWarmupMs && measured < floor)
                                {
                                    fpsCollapseStreak++;
                                    if (fpsCollapseStreak >= FpsCollapseWindows)
                                    {
                                        MarkDisconnected("Capture device stalled (frame rate collapsed).");
                                        _logger.LogWarning(
                                            "Radio Display: measured {Fps:0.0} fps vs target {Target} — treating as unplug",
                                            measured, targetFps);
                                        break;
                                    }
                                }
                                else
                                {
                                    fpsCollapseStreak = 0;
                                }
                            }
                        }

                        // Blocking Read is already the device clock — leftover
                        // wait on top of it is how 30 fps became 19.8 (one extra
                        // 15.6 ms Windows quantum). Only pace when Read returned early.
                        if (readMs < frameInterval.TotalMilliseconds * 0.6)
                        {
                            var wait = frameInterval - tick.Elapsed;
                            if (wait > TimeSpan.Zero)
                                SleepOrCancel(wait, ct);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    MarkDisconnected(ex.Message);
                    _logger.LogWarning(ex, "Radio Display capture error");
                    SleepOrCancel(2000, ct);
                }
                finally
                {
                    Volatile.Write(ref _deviceOpenFlag, 0);
                    Volatile.Write(ref _openDeviceIndex, -1);
                    try { cap?.Release(); } catch { /* ignore */ }
                    try { cap?.Dispose(); } catch { /* ignore */ }
                }

                if (ct.IsCancellationRequested)
                    break;

                SetStatus("connecting");
                SleepOrCancel(1000, ct);
            }

            if (_sessions.ViewerCount == 0)
                SetStatus("idle");
        }

        /// <summary>
        /// Run the Media Foundation MJPEG session on its own MTA thread and
        /// wait for it. The capture thread is STA (DirectShow graphs need that);
        /// the MF source reader misbehaves in a single-threaded apartment.
        /// Returns false when this device has no MJPEG pin, so the caller falls
        /// back to the OpenCV decode-and-encode path.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private bool RunMfSession(int index, int targetFps, CancellationToken ct)
        {
            var opened = false;
            var thread = new Thread(() =>
            {
                WindowsMfMjpegSession? mf = null;
                try
                {
                    mf = WindowsMfMjpegSession.TryOpen(index, targetFps, _logger);
                    if (mf is null)
                        return;

                    opened = true;
                    Volatile.Write(ref _openDeviceIndex, index);
                    Volatile.Write(ref _deviceOpenFlag, 1);
                    RunMfPassthroughLoop(mf, index, targetFps, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Radio Display Media Foundation session failed");
                }
                finally
                {
                    Volatile.Write(ref _deviceOpenFlag, 0);
                    Volatile.Write(ref _openDeviceIndex, -1);
                    mf?.Dispose();
                }
            })
            {
                IsBackground = true,
                Name = "YWC-RadioDisplay-MF"
            };

            try { thread.SetApartmentState(ApartmentState.MTA); }
            catch (InvalidOperationException) { /* ignore */ }

            thread.Start();
            thread.Join();

            if (!opened)
            {
                // Do not re-probe on every reconnect once we know this host's
                // device has no MJPEG pin — it costs seconds per attempt.
                _mfUnavailable = true;
            }

            return opened;
        }

        [SupportedOSPlatform("windows")]
        private void RunMfPassthroughLoop(WindowsMfMjpegSession mf, int index, int targetFps, CancellationToken ct)
        {
            SetStatus("streaming");
            _lastError = null;

            var settings = ReadSettings();
            var settingsAge = Stopwatch.StartNew();
            var fpsWindow = Stopwatch.StartNew();
            var streamStarted = Stopwatch.StartNew();
            var framesInWindow = 0;
            var emptyStreak = 0;
            var fpsCollapseStreak = 0;
            var loggedFormat = false;

            while (!ct.IsCancellationRequested)
            {
                if (settingsAge.ElapsedMilliseconds >= SettingsRefreshMs)
                {
                    settings = ReadSettings();
                    targetFps = VideoFpsOptions.Normalize(settings.VideoTargetFps);
                    settingsAge.Restart();
                }

                if (!settings.VideoDisplayEnabled ||
                    !VideoDeviceKey.TryParseIndex(settings.VideoCaptureDeviceKey, out var newIndex) ||
                    newIndex != index)
                {
                    break;
                }

                var tick = Stopwatch.StartNew();
                if (!mf.TryReadJpeg(out var jpegBytes) || jpegBytes is null || jpegBytes.Length == 0)
                {
                    emptyStreak++;
                    if (emptyStreak >= EmptyFrameGiveUp)
                    {
                        MarkDisconnected("Capture device stopped delivering frames.");
                        break;
                    }

                    SleepOrCancel(20, ct);
                    continue;
                }

                emptyStreak = 0;
                if (!loggedFormat)
                {
                    loggedFormat = true;
                    _logger.LogInformation(
                        "Radio Display negotiated {W}x{H} MJPG device={DevFps}fps (encode {Target} fps, maxW={MaxW}, mode={Mode})",
                        mf.Width,
                        mf.Height,
                        targetFps,
                        targetFps,
                        0,
                        "passthrough");
                }

                if (tick.ElapsedMilliseconds >= SlowReadMs)
                {
                    MarkDisconnected("Capture device stalled (no timely frames).");
                    _logger.LogWarning(
                        "Radio Display: Media Foundation ReadSample took {Ms} ms — treating as unplug",
                        tick.ElapsedMilliseconds);
                    break;
                }

                if (_sessions.ViewerCount > 0)
                {
                    lock (_frameLock)
                    {
                        _latestJpeg = jpegBytes;
                        _frameSeq++;
                        _frameWidth = mf.Width;
                        _frameHeight = mf.Height;
                    }

                    PulseFrame();

                    framesInWindow++;
                    if (fpsWindow.ElapsedMilliseconds >= 1000)
                    {
                        var measured = framesInWindow * 1000.0 / fpsWindow.ElapsedMilliseconds;
                        Volatile.Write(ref _measuredFps, measured);
                        framesInWindow = 0;
                        fpsWindow.Restart();

                        var floor = Math.Max(2.0, targetFps * 0.25);
                        if (streamStarted.ElapsedMilliseconds >= FpsWarmupMs && measured < floor)
                        {
                            fpsCollapseStreak++;
                            if (fpsCollapseStreak >= FpsCollapseWindows)
                            {
                                MarkDisconnected("Capture device stalled (frame rate collapsed).");
                                _logger.LogWarning(
                                    "Radio Display: measured {Fps:0.0} fps vs target {Target} — treating as unplug",
                                    measured, targetFps);
                                break;
                            }
                        }
                        else
                        {
                            fpsCollapseStreak = 0;
                        }
                    }
                }

                // ReadSample already waits for the next device frame.
            }
        }

        private ApplicationSettings ReadSettings()
        {
            try
            {
                // Memory snapshot only. GetSettingsAsync re-reads the JSON file
                // and must not run on this STA capture thread (sync-over-async
                // plus disk IO stole whole frame slots after 6812e07).
                return _settings.GetCachedSettings();
            }
            catch
            {
                return new ApplicationSettings();
            }
        }

        /// <summary>
        /// Sustained bytes/second a USB 2.0 UVC device can deliver over
        /// high-bandwidth isochronous transfers (3072 B × 8 microframes × 1000),
        /// less protocol overhead.
        /// </summary>
        private const double Usb2UvcBytesPerSecond = 22_000_000;

        /// <summary>Uncompressed YUY2 is 2 bytes per pixel.</summary>
        private const int Yuy2BytesPerPixel = 2;

        private static readonly (int Width, int Height)[] FallbackCaptureSizes =
        [
            (1024, 768),
            (800, 600),
            (640, 480),
            (512, 384),
            (480, 360),
            (320, 240)
        ];

        /// <summary>
        /// Largest 4:3 mode whose uncompressed frames still fit the USB 2.0
        /// budget at <paramref name="targetFps"/>. Without this, 800×600 YUY2 at
        /// 30 fps asks for ~29 MB/s on a ~22 MB/s bus and the dongle delivers
        /// ~20 fps no matter how the host paces its reads. Only the compressed
        /// MJPEG path (WindowsMfMjpegSession) escapes this trade-off.
        /// </summary>
        private static (int Width, int Height) PreferredCaptureSize(int maxWidth, int targetFps)
        {
            var ceiling = maxWidth > 0 ? maxWidth : 800;
            var fps = Math.Max(1, targetFps);
            var pixelBudget = Usb2UvcBytesPerSecond / (fps * Yuy2BytesPerPixel);

            foreach (var (w, h) in FallbackCaptureSizes)
            {
                if (w > ceiling)
                    continue;
                if (w * h <= pixelBudget)
                    return (w, h);
            }

            return (320, 240);
        }

        private static VideoCapture? OpenCapture(int index, int maxWidth, int targetFps, out bool jpegPassthrough)
        {
            jpegPassthrough = false;
            VideoCaptureAPIs[] backends;
            if (OperatingSystem.IsWindows())
                backends = [VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF, VideoCaptureAPIs.ANY];
            else if (OperatingSystem.IsLinux())
                backends = [VideoCaptureAPIs.V4L2, VideoCaptureAPIs.ANY];
            else if (OperatingSystem.IsMacOS())
                backends = [VideoCaptureAPIs.AVFOUNDATION, VideoCaptureAPIs.ANY];
            else
                backends = [VideoCaptureAPIs.ANY];

            // OpenCV DirectShow always decodes to BGR — MJPEG passthrough is
            // handled by WindowsMfMjpegSession. Stay on the USB2-safe YUY2
            // panel size here so a failed MF open does not repeat the 10 fps
            // 720p-BGR regression.
            var (wantW, wantH) = PreferredCaptureSize(maxWidth, targetFps);
            var bgrParams = new[]
            {
                (int)VideoCaptureProperties.FrameWidth, wantW,
                (int)VideoCaptureProperties.FrameHeight, wantH,
                (int)VideoCaptureProperties.Fps, targetFps,
                (int)VideoCaptureProperties.BufferSize, 3
            };
            foreach (var api in backends)
            {
                var cap = TryOpen(index, api, bgrParams);
                if (cap is not null)
                {
                    ApplyPreferredCaptureFormat(cap, maxWidth, targetFps);
                    return cap;
                }

                cap = TryOpen(index, api, paramsArray: null);
                if (cap is not null)
                {
                    ApplyPreferredCaptureFormat(cap, maxWidth, targetFps);
                    return cap;
                }
            }

            return null;
        }

        /// <summary>
        /// Request a size close to the encode width. Used only for the YUY2
        /// fallback path — MJPEG passthrough keeps the native 720p/1080p pin.
        /// </summary>
        private static void ApplyPreferredCaptureFormat(VideoCapture cap, int maxWidth, int targetFps)
        {
            var (wantW, wantH) = PreferredCaptureSize(maxWidth, targetFps);
            try { cap.Set(VideoCaptureProperties.FrameWidth, wantW); } catch { /* ignore */ }
            try { cap.Set(VideoCaptureProperties.FrameHeight, wantH); } catch { /* ignore */ }
            try { cap.Set(VideoCaptureProperties.Fps, targetFps); } catch { /* ignore */ }
            try { cap.Set(VideoCaptureProperties.BufferSize, 3); } catch { /* not all backends */ }
        }

        private static VideoCapture? TryOpen(int index, VideoCaptureAPIs api, int[]? paramsArray)
        {
            try
            {
                var cap = paramsArray is null
                    ? new VideoCapture(index, api)
                    : new VideoCapture(index, api, paramsArray);
                if (cap.IsOpened())
                    return cap;
                cap.Dispose();
            }
            catch
            {
                // try next
            }

            return null;
        }

        private static bool TryCopyJpeg(Mat frame, out byte[]? jpeg)
        {
            jpeg = null;
            if (frame.Empty())
                return false;

            var len = (int)(frame.Total() * frame.ElemSize());
            if (len < 4)
                return false;

            if (Marshal.ReadByte(frame.Data) != 0xFF || Marshal.ReadByte(frame.Data, 1) != 0xD8)
                return false;

            var bytes = new byte[len];
            Marshal.Copy(frame.Data, bytes, 0, len);
            var end = FindJpegEoi(bytes);
            jpeg = end >= 0 && end + 1 < bytes.Length
                ? bytes.AsSpan(0, end + 1).ToArray()
                : bytes;
            return true;
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

        private void PulseFrame()
        {
            var old = Interlocked.Exchange(ref _framePulse, NewFramePulse());
            old.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> NewFramePulse() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static string FormatFourCc(double value)
        {
            var c = unchecked((uint)(int)value);
            Span<char> chars = stackalloc char[4];
            chars[0] = (char)(c & 0xFF);
            chars[1] = (char)((c >> 8) & 0xFF);
            chars[2] = (char)((c >> 16) & 0xFF);
            chars[3] = (char)((c >> 24) & 0xFF);
            for (var i = 0; i < 4; i++)
            {
                if (chars[i] < 32 || chars[i] > 126)
                    chars[i] = '?';
            }

            return new string(chars);
        }

        private void SetStatus(string status) => Volatile.Write(ref _status!, status);

        private void MarkDisconnected(string error)
        {
            _lastError = error;
            Volatile.Write(ref _measuredFps, 0);
            lock (_frameLock)
            {
                _frameWidth = 0;
                _frameHeight = 0;
            }

            SetStatus("disconnected");
        }

        private static void SleepOrCancel(int ms, CancellationToken ct) =>
            SleepOrCancel(TimeSpan.FromMilliseconds(ms), ct);

        /// <summary>
        /// Wait without overshooting a 30 fps slot. <see cref="Thread.Sleep(int)"/>
        /// of 1 ms is still one Windows quantum (~15.6 ms) unless the process
        /// has 1 ms timer resolution — two such sleeps turn a 33 ms frame into
        /// ~50 ms (19.9 fps). Spin for the last quantum instead.
        /// </summary>
        private static void SleepOrCancel(TimeSpan delay, CancellationToken ct)
        {
            if (delay <= TimeSpan.Zero)
                return;

            var sw = Stopwatch.StartNew();
            var spin = new SpinWait();
            var quantum = TimeSpan.FromMilliseconds(TimerQuantumMs);
            while (!ct.IsCancellationRequested)
            {
                var left = delay - sw.Elapsed;
                if (left <= TimeSpan.Zero)
                    return;

                if (left > quantum && left.TotalMilliseconds > 50)
                    Thread.Sleep(1);
                else
                    spin.SpinOnce();
            }
        }

        private static void BeginPreciseTimer()
        {
            if (OperatingSystem.IsWindows())
                WindowsMultimediaTimer.BeginPeriod1();
        }

        private static void EndPreciseTimer()
        {
            if (OperatingSystem.IsWindows())
                WindowsMultimediaTimer.EndPeriod1();
        }

        [SupportedOSPlatform("windows")]
        private static class WindowsMultimediaTimer
        {
            [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
            private static extern uint TimeBeginPeriod(uint period);

            [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
            private static extern uint TimeEndPeriod(uint period);

            public static void BeginPeriod1()
            {
                try { TimeBeginPeriod(1); } catch { /* ignore */ }
            }

            public static void EndPeriod1()
            {
                try { TimeEndPeriod(1); } catch { /* ignore */ }
            }
        }

        public void Dispose()
        {
            CancelIdleRelease();
            StopLoopAndWait();
        }
    }
}
