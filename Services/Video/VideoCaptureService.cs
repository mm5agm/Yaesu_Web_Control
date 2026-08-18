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
        private static int ReopenSettleMs => OperatingSystem.IsMacOS() ? 1500 : 500;

        private const int StopWaitMs = 8000;
        private const int SettingsRefreshMs = 500;

        /// <summary>
        /// DirectShow/MJPEG Read can keep returning empty (or block ~1 s) after
        /// unplug. Give up on wall-clock, not a retry counter × wait.
        /// </summary>
        private const int EmptyFrameGiveUpMs = 5000;

        /// <summary>
        /// Bumped when a capture loop is cancelled/orphaned so a stuck worker
        /// exits instead of racing a newly started loop (device switch on macOS).
        /// </summary>
        private int _captureEpoch;

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
        private bool _dshowMjpegUnavailable;
        private bool _dshowPreferNativeMjpeg;
        private bool _linuxMjpegUnavailable;
        /// <summary>
        /// Set by <see cref="MarkDisconnected"/>. The outer loop must exit so
        /// we do not reopen <c>index:N</c> after Windows hands that slot to
        /// another camera. Cleared only by operator Start or device change.
        /// </summary>
        private readonly VideoDisconnectHalt _disconnectHalt = new();
        private VideoCapture? _liveCapture;
        private TaskCompletionSource<bool> _framePulse = NewFramePulse();

        public VideoCaptureService(
            ISettingsService settings,
            VideoSessionManager sessions,
            ILogger<VideoCaptureService> logger,
            VideoDisconnectHalt? disconnectHalt = null)
        {
            _settings = settings;
            _sessions = sessions;
            _logger = logger;
            _disconnectHalt = disconnectHalt ?? new VideoDisconnectHalt();
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

        /// <summary>True while capture is halted after a disconnect until operator recovery.</summary>
        public bool IsHaltedAfterDisconnect => _disconnectHalt.IsActive;

        /// <summary>
        /// Clear the post-disconnect halt so the operator can start capture again.
        /// </summary>
        public void ClearDisconnectHalt() => _disconnectHalt.Clear();

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

            if (_disconnectHalt.IsActive)
                throw new InvalidOperationException(
                    "Capture device is disconnected. Refresh the device list, then press Start.");

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
        /// On macOS the running loop applies the new device itself — a hard
        /// stop would Release the AVFoundation session from another thread
        /// while Read is on the UI thread (SIGSEGV in CaptureDelegate).
        /// </summary>
        public void RequestRestart()
        {
            CancelIdleRelease();
#if !WINDOWS
            if (OperatingSystem.IsMacOS())
            {
                bool start;
                lock (_lifecycleLock)
                {
                    if (_loopTask is { IsCompleted: false })
                    {
                        _logger.LogInformation(
                            "Radio Display: device change will be applied by the running capture loop");
                        return;
                    }

                    start = _sessions.ViewerCount > 0;
                }

                if (start)
                    EnsureLoopStarted();
                return;
            }
#endif
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
            if (_disconnectHalt.IsActive)
                return;

            lock (_lifecycleLock)
            {
                if (_loopTask is { IsCompleted: false })
                    return;

                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                var epoch = Volatile.Read(ref _captureEpoch);
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(() =>
                {
                    try
                    {
                        RunCaptureLoop(token, epoch);
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
                // Invalidate any loop that may be orphaned after a stuck Read so
                // it stops encoding and cannot race a replacement loop.
                Interlocked.Increment(ref _captureEpoch);
                running = _loopTask;
            }

            // Let the in-flight UI-thread Read finish, then the loop's finally
            // releases on the UI thread. Force-closing under that Read is what
            // SIGSEGVs CaptureDelegate on device switch.
            var finished = running == null;
            if (running != null)
            {
                try { finished = running.Wait(StopWaitMs); }
                catch (AggregateException) { finished = true; }
            }

            if (!finished)
            {
                _logger.LogWarning(
                    "Radio Display capture loop did not stop within {Ms} ms; forcing release",
                    StopWaitMs);
                ForceCloseLiveCapture();
                try { running?.Wait(1000); } catch { /* ignore */ }
            }

            lock (_lifecycleLock)
            {
                if (!finished && _loopTask is { IsCompleted: false })
                {
                    _logger.LogWarning(
                        "Radio Display capture loop did not stop within {Ms} ms; orphaning it so a new graph can open",
                        StopWaitMs);
                }

                _loopCts = null;
                _loopTask = null;
            }

            if (ReopenSettleMs > 0 && !_disconnectHalt.IsActive)
                Thread.Sleep(ReopenSettleMs);

            if (!_disconnectHalt.IsActive)
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
            _dshowMjpegUnavailable = false;
            _linuxMjpegUnavailable = false;
            _logger.LogInformation("Radio Display capture stopped");
        }

        private void ForceCloseLiveCapture()
        {
            var cap = Interlocked.Exchange(ref _liveCapture, null);
            if (cap is null)
                return;
#if !WINDOWS
            if (OperatingSystem.IsMacOS())
            {
                try { MacAvFoundationCapture.ForceUnblockAndRelease(cap); }
                catch
                {
                    try { cap.Release(); } catch { /* ignore */ }
                    try { cap.Dispose(); } catch { /* ignore */ }
                }
                return;
            }
#endif
            try { cap.Release(); } catch { /* ignore */ }
            try { cap.Dispose(); } catch { /* ignore */ }
        }

        private void RunCaptureLoop(CancellationToken ct, int epoch)
        {
            _mfUnavailable = false;
            _dshowMjpegUnavailable = false;
            _linuxMjpegUnavailable = false;
            SetStatus("connecting");
            if (!_disconnectHalt.IsActive)
                _lastError = null;
            BeginPreciseTimer();
            try
            {
                RunCaptureLoopCore(ct, epoch);
            }
            finally
            {
                EndPreciseTimer();
            }
        }

        private void RunCaptureLoopCore(CancellationToken ct, int epoch)
        {
            // Stay running across a brief zero-viewer gap (pop-out / Hide).
            // StopLoopAndWait cancels after IdleReleaseDelayMs.
            while (!ct.IsCancellationRequested && Volatile.Read(ref _captureEpoch) == epoch)
            {
                var settings = ReadSettings();
                if (!settings.VideoDisplayEnabled ||
                    string.IsNullOrWhiteSpace(settings.VideoCaptureDeviceKey) ||
                    !VideoDeviceKey.TryResolveOpenIndex(settings.VideoCaptureDeviceKey, out var index))
                {
                    SetStatus("unconfigured");
                    _lastError = "No capture device configured.";
                    SleepOrCancel(1000, ct);
                    continue;
                }

                // New open (including a user device-key change) must re-probe
                // MJPEG; the latches only skip fall-through inside this attempt.
                _mfUnavailable = false;
                _dshowMjpegUnavailable = false;
                _linuxMjpegUnavailable = false;

                var maxWidth = settings.VideoMaxWidth < 0 ? 0 : Math.Clamp(settings.VideoMaxWidth, 0, 1920);
                var rates = VideoDeviceFpsCaps.PeekRates(settings.VideoCaptureDeviceKey);
                var targetFps = VideoFpsOptions.Normalize(settings.VideoTargetFps, rates);
                var jpegQuality = VideoJpegQualityOptions.Normalize(settings.VideoJpegQuality);
                var frameInterval = TimeSpan.FromSeconds(1.0 / targetFps);
                var settingsAge = Stopwatch.StartNew();

                VideoCapture? cap = null;
                try
                {
                    if (OperatingSystem.IsLinux() && !_linuxMjpegUnavailable &&
                        RunLinuxMjpegSession(index, maxWidth, targetFps, ct))
                    {
                        if (ct.IsCancellationRequested || ShouldHaltAfterDisconnect())
                            break;
                        SetStatus("connecting");
                        SleepOrCancel(1000, ct);
                        continue;
                    }

                    if (OperatingSystem.IsWindows() && !_dshowMjpegUnavailable &&
                        RunDshowMjpegSession(index, maxWidth, targetFps, ct))
                    {
                        if (ct.IsCancellationRequested || ShouldHaltAfterDisconnect())
                            break;
                        SetStatus("connecting");
                        SleepOrCancel(1000, ct);
                        continue;
                    }

                    if (OperatingSystem.IsWindows() && !_mfUnavailable && RunMfSession(index, maxWidth, targetFps, ct))
                    {
                        if (ct.IsCancellationRequested || ShouldHaltAfterDisconnect())
                            break;
                        SetStatus("connecting");
                        SleepOrCancel(1000, ct);
                        continue;
                    }

                    cap = OpenCaptureForHost(index, maxWidth, targetFps, out var jpegPassthrough, out var openError);
                    if (cap is null || !cap.IsOpened())
                    {
                        var detail = OperatingSystem.IsLinux()
                            ? LinuxV4l2Devices.DescribeOpenFailure(index, openError)
                            : string.IsNullOrWhiteSpace(openError)
                                ? $"Could not open capture device index {index}."
                                : $"Could not open capture device index {index}. {openError}";
                        MarkDisconnected(detail);
                        _logger.LogWarning("Radio Display: failed to open device index {Index}: {Detail}", index, detail);
                        break;
                    }

                    Interlocked.Exchange(ref _liveCapture, cap);
                    Volatile.Write(ref _openDeviceIndex, index);
                    Volatile.Write(ref _deviceOpenFlag, 1);

                    SetStatus("streaming");
                    _lastError = null;
                    _logger.LogInformation(
                        "Radio Display streaming device index {Index} (maxW={MaxW}, fps={Fps}, q={Q}, passthrough={Passthrough})",
                        index, maxWidth, targetFps, jpegQuality, jpegPassthrough);

                    using var frame = new Mat();
                    using var resized = new Mat();
#if !WINDOWS
                    using var macEncodeSrc = new Mat();
#endif
                    var encodeParams = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality) };
                    var fpsWindow = Stopwatch.StartNew();
                    var streamStarted = Stopwatch.StartNew();
                    var framesInWindow = 0;
                    var emptySince = new Stopwatch();
                    var slowReadStreak = 0;
                    var fpsCollapseStreak = 0;
                    var loggedFormat = false;
                    Task<(byte[]? jpeg, int w, int h)>? macEncodeInflight = null;

                    void PublishJpeg(byte[] jpegBytes, int outW, int outH)
                    {
                        long publishedSeq;
                        lock (_frameLock)
                        {
                            _latestJpeg = jpegBytes;
                            publishedSeq = ++_frameSeq;
                            _frameWidth = outW;
                            _frameHeight = outH;
                        }

                        PulseFrame();

                        if (publishedSeq == 1)
                        {
                            _logger.LogInformation(
                                "Radio Display first JPEG published ({Bytes} bytes, {W}x{H})",
                                jpegBytes.Length, outW, outH);
                        }

                        framesInWindow++;
                        if (fpsWindow.ElapsedMilliseconds < 1000)
                            return;

                        var measured = framesInWindow * 1000.0 / fpsWindow.ElapsedMilliseconds;
                        Volatile.Write(ref _measuredFps, measured);
                        framesInWindow = 0;
                        fpsWindow.Restart();

                        var floor = Math.Max(2.0, targetFps * 0.25);
                        // FPS collapse after warmup: treat as unplug/disconnect, release
                        // capture, and halt — operator must refresh and press Start.
                        if (streamStarted.ElapsedMilliseconds >= FpsWarmupMs && measured < floor)
                        {
                            fpsCollapseStreak++;
                            if (fpsCollapseStreak >= FpsCollapseWindows)
                            {
                                MarkDisconnected("Capture device stalled (frame rate collapsed).");
                                _logger.LogWarning(
                                    "Radio Display: measured {Fps:0.0} fps vs target {Target} — treating as unplug",
                                    measured, targetFps);
                            }
                        }
                        else
                        {
                            fpsCollapseStreak = 0;
                        }
                    }

                    void DrainMacEncode()
                    {
                        if (macEncodeInflight is null)
                            return;

                        (byte[]? jpeg, int w, int h) result;
                        try
                        {
                            result = macEncodeInflight.GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Radio Display: macOS JPEG encode failed");
                            macEncodeInflight = null;
                            return;
                        }

                        macEncodeInflight = null;
                        if (result.jpeg is { Length: > 0 })
                            PublishJpeg(result.jpeg, result.w, result.h);
                    }

                    try
                    {
                        while (!ct.IsCancellationRequested && Volatile.Read(ref _captureEpoch) == epoch)
                        {
                        if (settingsAge.ElapsedMilliseconds >= SettingsRefreshMs)
                        {
                            settings = ReadSettings();
                            maxWidth = settings.VideoMaxWidth < 0 ? 0 : Math.Clamp(settings.VideoMaxWidth, 0, 1920);
                            var newFps = VideoFpsOptions.Normalize(settings.VideoTargetFps, VideoDeviceFpsCaps.PeekRates(settings.VideoCaptureDeviceKey));
                            jpegQuality = VideoJpegQualityOptions.Normalize(settings.VideoJpegQuality);
                            encodeParams = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality) };
                            if (newFps != targetFps)
                            {
                                targetFps = newFps;
                                frameInterval = TimeSpan.FromSeconds(1.0 / targetFps);
#if !WINDOWS
                                if (OperatingSystem.IsMacOS())
                                {
                                    var fpsWant = targetFps;
                                    var idx = index;
                                    string? avDetail = null;
                                    MacAvFoundationCapture.OnUiThread(() =>
                                    {
                                        MacAvFoundationDevices.TrySetFrameRate(
                                            idx, UniqueIdFromDeviceKey(settings.VideoCaptureDeviceKey),
                                            fpsWant, maxWidth, out avDetail);
                                    });
                                    if (!string.IsNullOrWhiteSpace(avDetail))
                                        _logger.LogInformation("Radio Display {Detail}", avDetail);
                                    loggedFormat = false;
                                }
#endif
                            }

                            settingsAge.Restart();
                        }

                        if (!settings.VideoDisplayEnabled ||
                            !VideoDeviceKey.TryResolveOpenIndex(settings.VideoCaptureDeviceKey, out var newIndex) ||
                            newIndex != index)
                        {
                            _logger.LogInformation(
                                "Radio Display switching capture device {From} → {To}",
                                index,
                                settings.VideoDisplayEnabled &&
                                VideoDeviceKey.TryResolveOpenIndex(settings.VideoCaptureDeviceKey, out var logged)
                                    ? logged
                                    : -1);
                            break; // exit inner loop to reopen after intentional device change
                        }

                        var tick = Stopwatch.StartNew();
                        bool readOk;
#if !WINDOWS
                        if (OperatingSystem.IsMacOS())
                            readOk = MacAvFoundationCapture.ReadOnUiThread(cap, frame);
                        else
                            readOk = cap.Read(frame);
#else
                        readOk = cap.Read(frame);
#endif
                        var readMs = tick.ElapsedMilliseconds;
                        if (!readOk || frame.Empty())
                        {
                            DrainMacEncode();
                            if (!emptySince.IsRunning)
                                emptySince.Start();
                            if (emptySince.ElapsedMilliseconds < 50 ||
                                emptySince.ElapsedMilliseconds % 1000 < 40)
                            {
                                _logger.LogDebug(
                                    "Radio Display: empty/failed Read for {Ms} ms readMs={ReadMs}",
                                    emptySince.ElapsedMilliseconds, readMs);
                            }
                            if (emptySince.ElapsedMilliseconds >= EmptyFrameGiveUpMs)
                            {
                                MarkDisconnected("Capture device stopped delivering frames.");
                                _logger.LogWarning(
                                    "Radio Display: no frames for {Ms} ms — treating as unplug",
                                    emptySince.ElapsedMilliseconds);
                                break;
                            }

                            SleepOrCancel(20, ct);
                            continue;
                        }

                        emptySince.Reset();

                        // JPEG bitstream Mats are 1×nbytes (width = compressed
                        // size, varies every frame). Picture size is in the SOF.
                        var reportW = frame.Width;
                        var reportH = frame.Height;
                        if (LooksLikeJpegMat(frame) &&
                            VideoJpegSof.TryReadSize(frame.Data, (int)(frame.Total() * frame.ElemSize()), out var sofW, out var sofH))
                        {
                            reportW = sofW;
                            reportH = sofH;
                        }
                        else
                        {
                            reportW = frame.Width > 1 && frame.Width <= 1920
                                ? frame.Width
                                : (int)cap.Get(VideoCaptureProperties.FrameWidth);
                            reportH = frame.Height > 1 && frame.Height <= 1200
                                ? frame.Height
                                : (int)cap.Get(VideoCaptureProperties.FrameHeight);
                            if (reportW <= 1 || reportW > 1920) reportW = frame.Width;
                            if (reportH <= 1 || reportH > 1200) reportH = frame.Height;
                        }

                        // Surface size even before the first JPEG so the UI is
                        // not stuck at 0×0 / 0 fps while encode catches up.
                        lock (_frameLock)
                        {
                            if (_frameWidth == 0 && reportW > 0)
                            {
                                _frameWidth = reportW;
                                _frameHeight = reportH;
                            }
                        }

                        if (!loggedFormat)
                        {
                            loggedFormat = true;
                            LogNegotiatedFormat(
                                reportW,
                                reportH,
                                cap.Get(VideoCaptureProperties.FourCC),
                                cap.Get(VideoCaptureProperties.Fps),
                                targetFps,
                                maxWidth,
                                jpegPassthrough);
                        }

                        // Unplug: DirectShow keeps succeeding with stale frames
                        // but Read blocks (~1 fps). Treat as disconnect, release
                        // capture, and halt — no automatic reopen; operator must
                        // refresh the device list and press Start.
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
                        // Drain first so a macOS Skia encode overlaps the Read
                        // we just finished instead of running after it.
                        DrainMacEncode();
                        if (fpsCollapseStreak >= FpsCollapseWindows)
                            break;

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
                                // Prefer already-compressed MJPEG payloads when present
                                // (PreferMjpegFourCc on macOS / some UVC dongles).
                                if (TryCopyJpeg(frame, out jpegBytes))
                                {
                                    outW = reportW;
                                    outH = reportH;
                                }
#if !WINDOWS
                                else if (OperatingSystem.IsMacOS())
                                {
                                    // OpenCvSharp ImEncode can native-crash (libjpeg
                                    // longjmp) on AVFoundation frames — use Skia.
                                    // Copy off `frame` and encode on the threadpool
                                    // so the next UI-thread Read can run in parallel
                                    // (Read blocks the AppKit thread for a full
                                    // device interval; doing JPEG after it is why
                                    // 30 fps became ~20).
                                    var encodeMaxW = maxWidth;
                                    var encodeQ = jpegQuality;
                                    frame.CopyTo(macEncodeSrc);
                                    macEncodeInflight = Task.Run(() =>
                                    {
                                        if (MacJpegEncoder.TryEncode(
                                                macEncodeSrc, encodeMaxW, encodeQ,
                                                out var encoded, out var ew, out var eh,
                                                out var encodeSkip))
                                            return (encoded, ew, eh);

                                        if (encodeSkip != null)
                                            _logger.LogWarning("Radio Display: skip JPEG encode — {Reason}", encodeSkip);
                                        return ((byte[]?)null, 0, 0);
                                    });
                                }
#endif
                                else if (!TryEncodeFrameJpeg(frame, resized, maxWidth, encodeParams, out jpegBytes, out outW, out outH, out var encodeSkipCv))
                                {
                                    if (encodeSkipCv != null)
                                        _logger.LogWarning("Radio Display: skip JPEG encode — {Reason}", encodeSkipCv);
                                    SleepOrCancel(50, ct);
                                    continue;
                                }
                            }

                            if (jpegBytes is { Length: > 0 })
                                PublishJpeg(jpegBytes, outW, outH);
                            else if (macEncodeInflight is null)
                            {
                                SleepOrCancel(50, ct);
                                continue;
                            }
                        }

                        if (fpsCollapseStreak >= FpsCollapseWindows)
                            break;

                        // Pace to the panel target even when the pin's minimum
                        // is faster (15 fps on a 20–60 pin used to publish at
                        // 20 because Read already took 50 ms and the 0.6
                        // threshold skipped the leftover wait). Sub-quantum
                        // leftovers spin in SleepOrCancel, so this does not
                        // recreate the Windows 19.8 fps overshoot.
                        var wait = frameInterval - tick.Elapsed;
                        if (wait > TimeSpan.Zero)
                            SleepOrCancel(wait, ct);
                    }
                    }
                    finally
                    {
                        DrainMacEncode();
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
                    break;
                }
                finally
                {
                    Volatile.Write(ref _deviceOpenFlag, 0);
                    Volatile.Write(ref _openDeviceIndex, -1);
                    // StopLoopAndWait may already have released this instance to
                    // unblock a stuck Read — only dispose if we still own it.
                    var owned = Interlocked.CompareExchange(ref _liveCapture, null, cap);
                    if (owned is not null)
                    {
#if !WINDOWS
                        if (OperatingSystem.IsMacOS())
                        {
                            // Read has returned; safe to serialize Release on the UI thread.
                            try { MacAvFoundationCapture.ReleaseOnUiThread(owned); }
                            catch
                            {
                                try { owned.Release(); } catch { /* ignore */ }
                                try { owned.Dispose(); } catch { /* ignore */ }
                            }
                        }
                        else
#endif
                        {
                            try { owned.Release(); } catch { /* ignore */ }
                            try { owned.Dispose(); } catch { /* ignore */ }
                        }
                    }
                }

                if (ct.IsCancellationRequested || ShouldHaltAfterDisconnect())
                    break;

                SetStatus("connecting");
                SleepOrCancel(ReopenSettleMs > 0 ? ReopenSettleMs : 1000, ct);
            }

            if (_sessions.ViewerCount == 0 && !ShouldHaltAfterDisconnect())
                SetStatus("idle");
        }

        /// <summary>
        /// Run the Media Foundation MJPEG session on its own MTA thread and
        /// wait for it. The capture thread is STA (DirectShow graphs need that);
        /// the MF source reader misbehaves in a single-threaded apartment.
        /// Returns false when this device has no ranked MJPEG pin, so the caller
        /// falls back to the OpenCV decode-and-encode path.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private bool RunMfSession(int index, int maxWidth, int targetFps, CancellationToken ct)
        {
            var opened = false;
            var noUsableMjpegPin = false;
            var thread = new Thread(() =>
            {
                WindowsMfMjpegSession? mf = null;
                try
                {
                    mf = WindowsMfMjpegSession.TryOpen(
                        index, targetFps, maxWidth, _logger, out var noMjpeg);
                    noUsableMjpegPin = noMjpeg;
                    if (mf is null)
                        return;

                    opened = true;
                    Volatile.Write(ref _openDeviceIndex, index);
                    Volatile.Write(ref _deviceOpenFlag, 1);
                    RunJpegPassthroughLoop(mf, index, maxWidth, targetFps, "Media Foundation", ct);
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

            if (!opened && noUsableMjpegPin)
                _mfUnavailable = true;

            return opened;
        }

        /// <summary>
        /// DirectShow MJPEG pin (the OBS path) on this STA capture thread.
        /// Returns false when the dongle has no usable MJPEG type or the graph
        /// failed to connect without a decoder, so the caller can fall back.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private bool RunDshowMjpegSession(int index, int maxWidth, int targetFps, CancellationToken ct)
        {
            WindowsDshowMjpegSession? ds = null;
            var opened = false;
            try
            {
                ds = WindowsDshowMjpegSession.TryOpen(
                    index, targetFps, maxWidth, _logger, out var noUsableMjpegPin,
                    _dshowPreferNativeMjpeg);
                if (ds is null)
                {
                    if (noUsableMjpegPin)
                        _dshowMjpegUnavailable = true;
                    return false;
                }

                opened = true;
                Volatile.Write(ref _openDeviceIndex, index);
                Volatile.Write(ref _deviceOpenFlag, 1);
                RunJpegPassthroughLoop(ds, index, maxWidth, targetFps, "DirectShow", ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Radio Display DirectShow MJPEG session failed");
                return opened;
            }
            finally
            {
                Volatile.Write(ref _deviceOpenFlag, 0);
                Volatile.Write(ref _openDeviceIndex, -1);
                ds?.Dispose();
            }
        }

        [SupportedOSPlatform("linux")]
        private bool RunLinuxMjpegSession(int index, int maxWidth, int targetFps, CancellationToken ct)
        {
            LinuxV4l2MjpegSession? session = null;
            var opened = false;
            try
            {
                session = LinuxV4l2MjpegSession.TryOpen(
                    index, targetFps, maxWidth, _logger, out var noUsableMjpegPin);
                if (session is null)
                {
                    if (noUsableMjpegPin)
                        _linuxMjpegUnavailable = true;
                    return false;
                }

                opened = true;
                Volatile.Write(ref _openDeviceIndex, index);
                Volatile.Write(ref _deviceOpenFlag, 1);
                RunJpegPassthroughLoop(session, index, maxWidth, targetFps, "V4L2", ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Radio Display V4L2 MJPEG session failed");
                return opened;
            }
            finally
            {
                Volatile.Write(ref _deviceOpenFlag, 0);
                Volatile.Write(ref _openDeviceIndex, -1);
                session?.Dispose();
            }
        }

        private void RunJpegPassthroughLoop(
            IJpegCaptureSession session, int index, int maxWidth, int targetFps, string via, CancellationToken ct)
        {
            SetStatus("streaming");
            _lastError = null;

            var settings = ReadSettings();
            var jpegQuality = VideoJpegQualityOptions.Normalize(settings.VideoJpegQuality);
            var frameInterval = TimeSpan.FromSeconds(1.0 / targetFps);
            var settingsAge = Stopwatch.StartNew();
            var fpsWindow = Stopwatch.StartNew();
            var streamStarted = Stopwatch.StartNew();
            var framesInWindow = 0;
            var emptySince = new Stopwatch();
            var fpsCollapseStreak = 0;
            var loggedFormat = false;
            using var resizedScratch = new Mat();

            while (!ct.IsCancellationRequested)
            {
                if (settingsAge.ElapsedMilliseconds >= SettingsRefreshMs)
                {
                    settings = ReadSettings();
                    maxWidth = settings.VideoMaxWidth < 0 ? 0 : Math.Clamp(settings.VideoMaxWidth, 0, 1920);
                    jpegQuality = VideoJpegQualityOptions.Normalize(settings.VideoJpegQuality);
                    var newFps = VideoFpsOptions.Normalize(
                        settings.VideoTargetFps, VideoDeviceFpsCaps.PeekRates(settings.VideoCaptureDeviceKey));
                    if (newFps != targetFps)
                    {
                        targetFps = newFps;
                        frameInterval = TimeSpan.FromSeconds(1.0 / targetFps);
                        if (session.TrySetFrameRate(targetFps))
                            loggedFormat = false;
                    }

                    settingsAge.Restart();
                }

                if (!settings.VideoDisplayEnabled ||
                    !VideoDeviceKey.TryResolveOpenIndex(settings.VideoCaptureDeviceKey, out var newIndex) ||
                    newIndex != index)
                {
                    break;
                }

                // The device is packing several pictures into one sample, which
                // renders as a tiled repeat. Nothing downstream can undo that —
                // the merge is inside a single JPEG scan — so drop the session and
                // reopen on the device's native mode. Checked before the read: the
                // session stops publishing merged samples once it is sure, so
                // waiting for a frame here would stall until the unplug watchdog.
                if (session.DeviceMergesFrames && !_dshowPreferNativeMjpeg)
                {
                    _dshowPreferNativeMjpeg = true;
                    _logger.LogWarning(
                        "Radio Display: {Via} device is merging frames at {W}x{H} — reopening on its " +
                        "native mode and scaling here instead",
                        via, session.Width, session.Height);
                    break;
                }

                var tick = Stopwatch.StartNew();
                if (!session.TryReadJpeg(out var jpegBytes) || jpegBytes is null || jpegBytes.Length == 0)
                {
                    if (!emptySince.IsRunning)
                        emptySince.Start();
                    if (emptySince.ElapsedMilliseconds >= EmptyFrameGiveUpMs)
                    {
                        MarkDisconnected("Capture device stopped delivering frames.");
                        _logger.LogWarning(
                            "Radio Display: {Via} no JPEG for {Ms} ms — treating as unplug",
                            via, emptySince.ElapsedMilliseconds);
                        break;
                    }

                    SleepOrCancel(20, ct);
                    continue;
                }

                emptySince.Reset();
                if (!loggedFormat)
                {
                    loggedFormat = true;
                    var mode = maxWidth > 0 && session.Width > maxWidth ? "passthrough+scale" : "passthrough";
                    _logger.LogInformation(
                        "Radio Display negotiated {W}x{H} MJPG device={DevFps:0.#}fps (encode {Target} fps, maxW={MaxW}, mode={Mode}, via={Via})",
                        session.Width,
                        session.Height,
                        session.DeviceFps,
                        targetFps,
                        maxWidth,
                        mode,
                        via);
                }

                if (tick.ElapsedMilliseconds >= SlowReadMs)
                {
                    // Slow JPEG read: stall after unplug — disconnect, release, halt.
                    MarkDisconnected("Capture device stalled (no timely frames).");
                    _logger.LogWarning(
                        "Radio Display: {Via} JPEG read took {Ms} ms — treating as unplug",
                        via, tick.ElapsedMilliseconds);
                    break;
                }

                if (_sessions.ViewerCount > 0)
                {
                    var outW = session.Width;
                    var outH = session.Height;
                    if (TryDownscalePublishedJpeg(
                            jpegBytes, session.Width, session.Height, maxWidth, jpegQuality, resizedScratch,
                            out var sized, out outW, out outH) &&
                        sized is { Length: > 0 })
                    {
                        jpegBytes = sized;
                    }

                    long publishedSeq;
                    lock (_frameLock)
                    {
                        _latestJpeg = jpegBytes;
                        publishedSeq = ++_frameSeq;
                        _frameWidth = outW;
                        _frameHeight = outH;
                    }

                    PulseFrame();

                    if (publishedSeq == 1)
                    {
                        _logger.LogInformation(
                            "Radio Display first JPEG published ({Bytes} bytes, {W}x{H})",
                            jpegBytes.Length, outW, outH);
                    }

                    framesInWindow++;
                    if (fpsWindow.ElapsedMilliseconds >= 1000)
                    {
                        var measured = framesInWindow * 1000.0 / fpsWindow.ElapsedMilliseconds;
                        Volatile.Write(ref _measuredFps, measured);
                        framesInWindow = 0;
                        fpsWindow.Restart();

                        var floor = Math.Max(2.0, targetFps * 0.25);
                        // FPS collapse after warmup: treat as unplug/disconnect, release
                        // capture, and halt — operator must refresh and press Start.
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

                var wait = frameInterval - tick.Elapsed;
                if (wait > TimeSpan.Zero)
                    SleepOrCancel(wait, ct);
            }
        }

        /// <summary>
        /// Apply panel JPEG quality (and optional max-width scale) to a
        /// captured JPEG. MJPEG passthrough copies the dongle bitstream as-is,
        /// so Low/Medium must re-encode or the quality toggle does nothing.
        /// Max at native size keeps the hardware JPEG (sharpest, cheapest).
        /// </summary>
        private static bool TryDownscalePublishedJpeg(
            byte[] jpegIn,
            int srcW,
            int srcH,
            int maxWidth,
            int jpegQuality,
            Mat resizedScratch,
            out byte[]? jpegOut,
            out int outW,
            out int outH)
        {
            jpegOut = jpegIn;
            outW = srcW;
            outH = srcH;
            var needScale = maxWidth > 0 && srcW > maxWidth;
            var needRecompress = jpegQuality < VideoJpegQualityOptions.Max;
            if (!needScale && !needRecompress)
                return true;

            using var decoded = Cv2.ImDecode(jpegIn, ImreadModes.Color);
            if (decoded.Empty() || decoded.Width < 2 || decoded.Height < 2)
                return true;

            Mat source = decoded;
            if (needScale)
            {
                var newH = Math.Max(1, (int)Math.Round(decoded.Height * ((double)maxWidth / decoded.Width)));
                Cv2.Resize(decoded, resizedScratch, new OpenCvSharp.Size(maxWidth, newH), 0, 0, InterpolationFlags.Linear);
                source = resizedScratch;
                outW = maxWidth;
                outH = newH;
            }
            else
            {
                outW = decoded.Width;
                outH = decoded.Height;
            }

            var encodeParams = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality) };
            var encoded = source.ImEncode(".jpg", encodeParams);
            if (encoded is null || encoded.Length < 100)
            {
                jpegOut = jpegIn;
                outW = srcW;
                outH = srcH;
                return true;
            }

            jpegOut = encoded;
            return true;
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

        /// <summary>
        /// Stable 4:3 ≥800 request for the OpenCV YUY2 fallback so 15 / 30 / 60
        /// share size. USB2 may still cap uncompressed 800×600@30 at ~20 fps;
        /// that is logged, not silently dropped to 640×480. MJPEG passthrough
        /// is the path that can hold 30/60.
        /// </summary>
        private static (int Width, int Height) PreferredCaptureSize(int maxWidth) =>
            VideoCapturePinRank.PreferredUncompressedSize(maxWidth);

        /// <summary>
        /// On macOS, open the AVFoundation device on Avalonia's UI thread so
        /// AppKit/TCC complete. Reads also stay on that thread.
        /// </summary>
        private VideoCapture? OpenCaptureForHost(
            int index,
            int maxWidth,
            int targetFps,
            out bool jpegPassthrough,
            out string? failureDetail)
        {
#if !WINDOWS
            if (OperatingSystem.IsMacOS())
            {
                bool jpeg = false;
                string? detail = null;
                VideoCapture? opened = null;
                try
                {
                    string? avDetail = null;
                    opened = MacAvFoundationCapture.OpenOnUiThread(() =>
                    {
                        var cap = OpenCapture(index, maxWidth, targetFps, out jpeg, out detail);
                        if (cap is not null)
                        {
                            MacAvFoundationDevices.TrySetFrameRate(index, null, targetFps, maxWidth, out var setDetail);
                            avDetail = setDetail;
                        }

                        return cap;
                    });
                    if (!string.IsNullOrWhiteSpace(avDetail))
                        _logger.LogInformation("Radio Display {Detail}", avDetail);
                }
                catch (Exception ex)
                {
                    detail = FlattenException(ex);
                    _logger.LogWarning(ex, "Radio Display: macOS UI-thread open failed");
                }

                jpegPassthrough = jpeg;
                failureDetail = detail;
                return opened;
            }
#endif
            return OpenCapture(index, maxWidth, targetFps, out jpegPassthrough, out failureDetail);
        }

        private static VideoCapture? OpenCapture(
            int index,
            int maxWidth,
            int targetFps,
            out bool jpegPassthrough,
            out string? failureDetail)
        {
            jpegPassthrough = false;
            failureDetail = null;
            VideoCaptureAPIs[] backends;
            if (OperatingSystem.IsWindows())
                backends = [VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF, VideoCaptureAPIs.ANY];
            else if (OperatingSystem.IsLinux())
                backends = [VideoCaptureAPIs.V4L2, VideoCaptureAPIs.ANY];
            else if (OperatingSystem.IsMacOS())
                backends = [VideoCaptureAPIs.AVFOUNDATION, VideoCaptureAPIs.ANY];
            else
                backends = [VideoCaptureAPIs.ANY];

            string? lastError = null;

            // macOS: prefer the device's native mode (often MJPEG). Forcing the
            // Windows USB2 YUY2 panel size opens Macrosilicon dongles in a mode
            // that reports IsOpened() but delivers black / stalled frames.
            if (OperatingSystem.IsMacOS())
            {
                foreach (var api in backends)
                {
                    var cap = TryOpen(index, api, paramsArray: null, out lastError);
                    if (cap is not null)
                    {
                        ApplyMacCaptureHints(cap);
                        return cap;
                    }

                    if (api == VideoCaptureAPIs.AVFOUNDATION)
                    {
                        cap = TryOpenPath($"{index}:none", api, out lastError);
                        if (cap is not null)
                        {
                            ApplyMacCaptureHints(cap);
                            return cap;
                        }
                    }
                }
            }

            // OpenCV DirectShow always decodes to BGR — MJPEG passthrough is
            // handled by WindowsMfMjpegSession. Ask for MJPEG 800×600 first so a
            // failed MF open does not land on YUY2 640×480@30 (USB2 rejects
            // uncompressed 800×600@30 and the driver drops the size).
            // Linux V4L2 must pass FourCC at open too: post-open Set(FourCC)
            // is ignored after VIDIOC_S_FMT, and YUYV 800×600 is USB2-capped
            // at 15 fps. CONVERT_RGB=0 keeps the JPEG bitstream for passthrough.
            var (wantW, wantH) = PreferredCaptureSize(maxWidth);
            if (OperatingSystem.IsLinux())
            {
                var pick = VideoCapturePinRank.PickMjpegCaptureMeetingFps(
                    LinuxV4l2Devices.TryQueryPins(index), targetFps, maxWidth);
                if (pick is { Width: >= 2, Height: >= 2 } p)
                {
                    wantW = p.Width;
                    wantH = p.Height;
                }
            }

            var mjpeg = VideoWriter.FourCC('M', 'J', 'P', 'G');
            int[] bgrParams;
            if (OperatingSystem.IsWindows())
            {
                bgrParams =
                [
                    (int)VideoCaptureProperties.FourCC, mjpeg,
                    (int)VideoCaptureProperties.FrameWidth, wantW,
                    (int)VideoCaptureProperties.FrameHeight, wantH,
                    (int)VideoCaptureProperties.BufferSize, 3
                ];
            }
            else if (OperatingSystem.IsLinux())
            {
                bgrParams =
                [
                    (int)VideoCaptureProperties.FourCC, mjpeg,
                    (int)VideoCaptureProperties.ConvertRgb, 0,
                    (int)VideoCaptureProperties.FrameWidth, wantW,
                    (int)VideoCaptureProperties.FrameHeight, wantH,
                    (int)VideoCaptureProperties.BufferSize, 3
                ];
            }
            else
            {
                bgrParams =
                [
                    (int)VideoCaptureProperties.FrameWidth, wantW,
                    (int)VideoCaptureProperties.FrameHeight, wantH,
                    (int)VideoCaptureProperties.Fps, targetFps,
                    (int)VideoCaptureProperties.BufferSize, 3
                ];
            }

            foreach (var api in backends)
            {
                if (OperatingSystem.IsLinux())
                {
                    var linuxBgrParams = new[]
                    {
                        (int)VideoCaptureProperties.FourCC, mjpeg,
                        (int)VideoCaptureProperties.ConvertRgb, 0,
                        (int)VideoCaptureProperties.FrameWidth, wantW,
                        (int)VideoCaptureProperties.FrameHeight, wantH,
                        (int)VideoCaptureProperties.BufferSize, 3
                    };

                    var opened = FinishLinuxOpen(
                        TryOpenPath(LinuxV4l2Devices.DevicePath(index), api, bgrParams, out lastError),
                        index, wantW, wantH, maxWidth, targetFps, ref jpegPassthrough);
                    if (opened is not null)
                        return opened;

                    opened = FinishLinuxOpen(
                        TryOpen(index, api, bgrParams, out lastError),
                        index, wantW, wantH, maxWidth, targetFps, ref jpegPassthrough);
                    if (opened is not null)
                        return opened;

                    opened = FinishLinuxOpen(
                        TryOpenPath(LinuxV4l2Devices.DevicePath(index), api, linuxBgrParams, out lastError),
                        index, wantW, wantH, maxWidth, targetFps, ref jpegPassthrough,
                        requestJpegPassthrough: false);
                    if (opened is not null)
                        return opened;

                    opened = FinishLinuxOpen(
                        TryOpen(index, api, linuxBgrParams, out lastError),
                        index, wantW, wantH, maxWidth, targetFps, ref jpegPassthrough,
                        requestJpegPassthrough: false);
                    if (opened is not null)
                        return opened;

                    opened = FinishLinuxOpen(
                        TryOpen(index, api, paramsArray: null, out lastError),
                        index, wantW, wantH, maxWidth, targetFps, ref jpegPassthrough,
                        requestJpegPassthrough: false);
                    if (opened is not null)
                        return opened;

                    continue;
                }

                var cap = TryOpen(index, api, bgrParams, out lastError);
                if (cap is not null)
                {
                    ApplyPreferredCaptureFormat(cap, maxWidth, targetFps);
                    return cap;
                }

                cap = TryOpen(index, api, paramsArray: null, out lastError);
                if (cap is not null)
                {
                    ApplyPreferredCaptureFormat(cap, maxWidth, targetFps);
                    return cap;
                }

                // macOS AVFoundation also accepts "N:none" (video only, no audio).
                if (OperatingSystem.IsMacOS() && api == VideoCaptureAPIs.AVFOUNDATION)
                {
                    cap = TryOpenPath($"{index}:none", api, out lastError);
                    if (cap is not null)
                    {
                        ApplyPreferredCaptureFormat(cap, maxWidth, targetFps);
                        return cap;
                    }
                }
            }

            failureDetail = OperatingSystem.IsMacOS()
                ? DescribeOpenFailure(lastError)
                : lastError;
            return null;
        }

        private static void PreferMjpegFourCc(VideoCapture cap)
        {
            try
            {
                cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
            }
            catch
            {
                // Device may ignore; native mode still preferred over forced YUY2.
            }
        }

        /// <summary>
        /// Linux: lock MJPEG + raw JPEG Mats when the dongle actually delivers
        /// SOI. CONVERT_RGB=0 after a YUYV negotiate would break ImEncode, so
        /// a non-JPEG probe either restores BGR conversion or discards the open.
        /// </summary>
        private static VideoCapture? FinishLinuxOpen(
            VideoCapture? cap,
            int index,
            int wantW,
            int wantH,
            int maxWidth,
            int targetFps,
            ref bool jpegPassthrough,
            bool requestJpegPassthrough = true)
        {
            if (cap is null)
                return null;

            ApplyPreferredCaptureFormat(cap, maxWidth, targetFps, wantW, wantH, setFps: false);
            if (OperatingSystem.IsLinux())
            {
                if (!LinuxV4l2Devices.TrySetMjpegFormat(index, wantW, wantH, targetFps)
                    && targetFps >= 30
                    && LinuxV4l2Devices.TrySetMjpegFormat(index, 640, 480, targetFps))
                {
                    wantW = 640;
                    wantH = 480;
                    ApplyPreferredCaptureFormat(cap, maxWidth, targetFps, wantW, wantH, setFps: false);
                }
            }

            if (!requestJpegPassthrough)
            {
                jpegPassthrough = false;
                try { cap.Set(VideoCaptureProperties.ConvertRgb, 1); } catch { /* ignore */ }
                return cap;
            }

            try { cap.Set(VideoCaptureProperties.ConvertRgb, 0); } catch { /* ignore */ }

            using var probe = new Mat();
            var readOk = false;
            try { readOk = cap.Read(probe); }
            catch { /* treat as failed probe */ }

            if (readOk && !probe.Empty() && LooksLikeJpegMat(probe))
            {
                jpegPassthrough = true;
                return cap;
            }

            try { cap.Set(VideoCaptureProperties.ConvertRgb, 1); } catch { /* ignore */ }

            if (readOk && !probe.Empty())
            {
                jpegPassthrough = false;
                return cap;
            }

            try { cap.Release(); } catch { /* ignore */ }
            try { cap.Dispose(); } catch { /* ignore */ }
            return null;
        }

        private static bool LooksLikeJpegMat(Mat frame)
        {
            if (frame.Empty())
                return false;

            var len = (int)(frame.Total() * frame.ElemSize());
            if (len < 100)
                return false;

            try
            {
                if (Marshal.ReadByte(frame.Data) != 0xFF || Marshal.ReadByte(frame.Data, 1) != 0xD8)
                    return false;
            }
            catch
            {
                return false;
            }

            // Decoded BGR/YUV images are WxH; MJPEG bitstreams are 1-row (or
            // 1-col) 8U buffers whose long side is the compressed length.
            if (frame.Height <= 2 || frame.Width <= 2)
                return true;
            if (frame.Channels() is 3 or 4 && frame.Width <= 1920 && frame.Height <= 1200)
                return false;
            return frame.Width > 1920 || frame.Height > 1200;
        }

        /// <summary>
        /// Ask AVFoundation for MJPEG. Do not force a panel size here —
        /// Macrosilicon dongles then report IsOpened() but deliver black /
        /// stalled frames. Frame rate is applied after open via
        /// <c>MacAvFoundationDevices.TrySetFrameRate</c> (OpenCV's FPS
        /// property is ignored by the AVFoundation backend).
        /// </summary>
        private static void ApplyMacCaptureHints(VideoCapture cap)
        {
            PreferMjpegFourCc(cap);
            try { cap.Set(VideoCaptureProperties.BufferSize, 2); } catch { /* ignore */ }
        }

        private static string? UniqueIdFromDeviceKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key) ||
                !key.StartsWith("uid:", StringComparison.OrdinalIgnoreCase))
                return null;

            var uid = key["uid:".Length..].Trim();
            return uid.Length > 0 ? uid : null;
        }

        private static string DescribeOpenFailure(string? lastError)
        {
            if (string.IsNullOrWhiteSpace(lastError))
            {
                return OperatingSystem.IsMacOS()
                    ? "OpenCV returned IsOpened=false (device busy, index changed, or still releasing after a prior session). Retry in a few seconds; confirm Camera is allowed for Yaesu Web Control in System Settings."
                    : "OpenCV could not open the device.";
            }

            if (lastError.Contains("libavif", StringComparison.OrdinalIgnoreCase) ||
                lastError.Contains("OpenCvSharpExtern", StringComparison.OrdinalIgnoreCase) ||
                lastError.Contains("DllNotFound", StringComparison.OrdinalIgnoreCase))
            {
                if (OperatingSystem.IsMacOS() &&
                    RuntimeInformation.OSArchitecture == Architecture.X64)
                {
                    return "OpenCvSharp native library failed to load (missing Homebrew libavif on Intel Mac). " +
                           "Run: brew install libavif — then restart Yaesu Web Control.";
                }

                return "OpenCvSharp native library failed to load: " + TruncateForUi(lastError, 180);
            }

            if (lastError.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
                lastError.Contains("denied", StringComparison.OrdinalIgnoreCase))
            {
                return "Camera access denied. System Settings → Privacy & Security → Camera → enable Yaesu Web Control, then relaunch.";
            }

            return TruncateForUi(lastError, 180);
        }

        /// <summary>
        /// Build a continuous 8UC3 BGR Mat and JPEG-encode it. OpenCvSharp's
        /// libjpeg path can SIGSEGV (longjmp to nowhere) on bad/non-continuous
        /// frames — seen on macOS AVFoundation after UI-thread Read. Validate
        /// and copy before ImEncode; never encode in-place capture buffers.
        /// </summary>
        private static bool TryEncodeFrameJpeg(
            Mat frame,
            Mat resizedScratch,
            int maxWidth,
            ImageEncodingParam[] encodeParams,
            out byte[]? jpegBytes,
            out int outW,
            out int outH,
            out string? skipReason)
        {
            jpegBytes = null;
            outW = 0;
            outH = 0;
            skipReason = null;

            if (frame.Empty() || frame.Width < 2 || frame.Height < 2)
            {
                skipReason = "empty or tiny frame";
                return false;
            }

            var ch = frame.Channels();
            if (ch is not (1 or 3 or 4))
            {
                skipReason = $"unsupported channel count {ch}";
                return false;
            }

            // Deep-copy off the capture buffer (AVFoundation may recycle it).
            using var owned = frame.Clone();
            if (owned.Empty() || !owned.IsContinuous())
            {
                // Clone should be continuous; if not, force one more copy.
                using var cont = owned.Clone();
                return EncodePrepared(cont, resizedScratch, maxWidth, encodeParams, out jpegBytes, out outW, out outH, out skipReason);
            }

            return EncodePrepared(owned, resizedScratch, maxWidth, encodeParams, out jpegBytes, out outW, out outH, out skipReason);
        }

        private static bool EncodePrepared(
            Mat owned,
            Mat resizedScratch,
            int maxWidth,
            ImageEncodingParam[] encodeParams,
            out byte[]? jpegBytes,
            out int outW,
            out int outH,
            out string? skipReason)
        {
            jpegBytes = null;
            outW = 0;
            outH = 0;
            skipReason = null;

            Mat bgr;
            Mat? converted = null;
            try
            {
                var ch = owned.Channels();
                if (ch == 4)
                {
                    converted = new Mat();
                    Cv2.CvtColor(owned, converted, ColorConversionCodes.BGRA2BGR);
                    bgr = converted;
                }
                else if (ch == 1)
                {
                    converted = new Mat();
                    Cv2.CvtColor(owned, converted, ColorConversionCodes.GRAY2BGR);
                    bgr = converted;
                }
                else
                {
                    bgr = owned;
                }

                Mat source = bgr;
                if (maxWidth > 0 && bgr.Width > maxWidth)
                {
                    var scale = (double)maxWidth / bgr.Width;
                    var newH = Math.Max(1, (int)Math.Round(bgr.Height * scale));
                    if (newH < 1 || maxWidth < 2)
                    {
                        skipReason = "invalid resize target";
                        return false;
                    }

                    Cv2.Resize(bgr, resizedScratch, new OpenCvSharp.Size(maxWidth, newH), 0, 0, InterpolationFlags.Area);
                    source = resizedScratch;
                }

                if (source.Empty() || source.Channels() != 3 || source.Type() != MatType.CV_8UC3)
                {
                    skipReason = $"bad encode source type={source.Type()} ch={source.Channels()}";
                    return false;
                }

                // ImEncode can native-crash on bad input; keep the smallest
                // surface area possible and avoid encoding the live capture Mat.
                using var continuous = source.IsContinuous() ? null : source.Clone();
                var toEncode = continuous ?? source;
                jpegBytes = toEncode.ImEncode(".jpg", encodeParams);
                if (jpegBytes is null || jpegBytes.Length < 100)
                {
                    skipReason = "ImEncode returned empty buffer";
                    jpegBytes = null;
                    return false;
                }

                // Sanity: JPEG SOI
                if (jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8)
                {
                    skipReason = "ImEncode output missing JPEG SOI";
                    jpegBytes = null;
                    return false;
                }

                outW = source.Width;
                outH = source.Height;
                return true;
            }
            catch (Exception ex)
            {
                skipReason = ex.GetType().Name + ": " + ex.Message;
                jpegBytes = null;
                return false;
            }
            finally
            {
                converted?.Dispose();
            }
        }

        private static string TruncateForUi(string text, int max)
        {
            var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "…";
        }

        /// <summary>
        /// MJPEG 800×600 when the pin exists (USB2-safe). FourCC before size so
        /// DirectShow does not pick YUY2 640×480@30. FPS last — requesting
        /// 800×600 YUY2@30 is what made the driver drop to 640.
        /// </summary>
        private static void ApplyPreferredCaptureFormat(
            VideoCapture cap, int maxWidth, int targetFps, int? width = null, int? height = null,
            bool setFps = true)
        {
            var (wantW, wantH) = width is >= 2 && height is >= 2
                ? (width.Value, height.Value)
                : PreferredCaptureSize(maxWidth);
            PreferMjpegFourCc(cap);
            try { cap.Set(VideoCaptureProperties.FrameWidth, wantW); } catch { /* ignore */ }
            try { cap.Set(VideoCaptureProperties.FrameHeight, wantH); } catch { /* ignore */ }
            if (setFps)
            {
                try { cap.Set(VideoCaptureProperties.Fps, targetFps); } catch { /* ignore */ }
            }

            try { cap.Set(VideoCaptureProperties.BufferSize, 3); } catch { /* not all backends */ }
        }

        private static VideoCapture? TryOpen(
            int index,
            VideoCaptureAPIs api,
            int[]? paramsArray,
            out string? error)
        {
            error = null;
            try
            {
                var cap = paramsArray is null
                    ? new VideoCapture(index, api)
                    : new VideoCapture(index, api, paramsArray);
                if (cap.IsOpened())
                    return cap;
                cap.Dispose();
            }
            catch (Exception ex)
            {
                error = FlattenException(ex);
            }

            return null;
        }

        private static VideoCapture? TryOpenPath(string path, VideoCaptureAPIs api, out string? error) =>
            TryOpenPath(path, api, paramsArray: null, out error);

        private static VideoCapture? TryOpenPath(string path, VideoCaptureAPIs api, int[]? paramsArray, out string? error)
        {
            error = null;
            try
            {
                var cap = paramsArray is null
                    ? new VideoCapture(path, api)
                    : new VideoCapture(path, api, paramsArray);
                if (cap.IsOpened())
                    return cap;
                cap.Dispose();
            }
            catch (Exception ex)
            {
                error = FlattenException(ex);
            }

            return null;
        }

        private static string FlattenException(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e is not null; e = e.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message))
                    parts.Add(e.Message);
            }

            return string.Join(" → ", parts);
        }

        private static bool TryCopyJpeg(Mat frame, out byte[]? jpeg)
        {
            jpeg = null;
            if (!LooksLikeJpegMat(frame))
                return false;

            var len = (int)(frame.Total() * frame.ElemSize());

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

        private void LogNegotiatedFormat(
            int width,
            int height,
            double fourCcValue,
            double deviceFps,
            int targetFps,
            int maxWidth,
            bool jpegPassthrough)
        {
            var fourCc = FormatFourCc(fourCcValue);
            var mode = jpegPassthrough ? "passthrough" : "encode";
            var uncompressedMbps = width * height * Yuy2BytesPerPixel * targetFps / 1e6;
            var usb2BudgetMbps = Usb2UvcBytesPerSecond / 1e6;

            // USB2 comparison only makes sense for known uncompressed packed
            // formats. MJPEG / unknown FourCC on a Pi at 640×480@60 can sustain
            // 60 fps on the wire while the YUY2 estimate (36.9 MB/s) looks over
            // the ~22 MB/s budget — a false alarm.
            if (LooksUncompressedFourCc(fourCc) && uncompressedMbps > usb2BudgetMbps)
            {
                _logger.LogWarning(
                    "Radio Display negotiated {W}x{H} {FourCc} device={DevFps:0.#}fps " +
                    "(encode {Target} fps, maxW={MaxW}, mode={Mode}, uncompressed {Mbps:0.#} MB/s vs ~{Budget:0} MB/s USB2)",
                    width, height, fourCc, deviceFps, targetFps, maxWidth, mode,
                    uncompressedMbps, usb2BudgetMbps);
                return;
            }

            _logger.LogInformation(
                "Radio Display negotiated {W}x{H} {FourCc} device={DevFps:0.#}fps " +
                "(encode {Target} fps, maxW={MaxW}, mode={Mode})",
                width, height, fourCc, deviceFps, targetFps, maxWidth, mode);
        }

        /// <summary>
        /// Packed YUV/RGB FourCCs whose USB payload is ~2–3 bytes/pixel.
        /// Anything else (MJPG, unknown, hex dump) is treated as compressed or
        /// untrusted so we do not warn on USB2 budget.
        /// </summary>
        private static bool LooksUncompressedFourCc(string fourCc)
        {
            return fourCc.Equals("YUY2", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("YUYV", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("UYVY", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("YVYU", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("RGB3", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("BGR3", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("RGB4", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("BGR4", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("I420", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("YV12", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("NV12", StringComparison.OrdinalIgnoreCase)
                || fourCc.Equals("GREY", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatFourCc(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return "unknown";

            // CAP_PROP_FOURCC is a u32 packed as double. Cast via long so
            // codes with the high bit set are not truncated by (int).
            var rounded = Math.Round(value);
            if (rounded > uint.MaxValue)
                return "unknown";

            var c = unchecked((uint)rounded);
            Span<char> chars = stackalloc char[4];
            chars[0] = (char)(c & 0xFF);
            chars[1] = (char)((c >> 8) & 0xFF);
            chars[2] = (char)((c >> 16) & 0xFF);
            chars[3] = (char)((c >> 24) & 0xFF);
            for (var i = 0; i < 4; i++)
            {
                // FourCC is letters, digits, or space (e.g. "Y16 "). Punctuation
                // from a driver that does not return a real FourCC (seen as
                // "}?6?" on V4L2/AVFoundation) is not useful in the log.
                if (!IsFourCcChar(chars[i]))
                    return $"0x{c:X8}";
            }

            return new string(chars);
        }

        private static bool IsFourCcChar(char ch) =>
            ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or ' ';

        private void SetStatus(string status) => Volatile.Write(ref _status!, status);

        private bool ShouldHaltAfterDisconnect() => _disconnectHalt.IsActive;

        private void MarkDisconnected(string error)
        {
            _disconnectHalt.Set();
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
