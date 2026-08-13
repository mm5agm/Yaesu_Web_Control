using System.Diagnostics;
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

                try
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
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
            Volatile.Write(ref _measuredFps, 0);
            _logger.LogInformation("Radio Display capture stopped");
        }

        private void RunCaptureLoop(CancellationToken ct)
        {
            SetStatus("connecting");
            _lastError = null;

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
                    cap = OpenCapture(index);
                    if (cap is null || !cap.IsOpened())
                    {
                        MarkDisconnected($"Could not open capture device index {index}.");
                        _logger.LogWarning("Radio Display: failed to open device index {Index}", index);
                        SleepOrCancel(2000, ct);
                        continue;
                    }

                    Volatile.Write(ref _openDeviceIndex, index);
                    Volatile.Write(ref _deviceOpenFlag, 1);

                    // Best-effort preferred size near radio-panel resolution.
                    cap.Set(VideoCaptureProperties.FrameWidth, maxWidth > 0 ? maxWidth : 800);
                    cap.Set(VideoCaptureProperties.FrameHeight, 600);
                    cap.Set(VideoCaptureProperties.Fps, targetFps);

                    SetStatus("streaming");
                    _lastError = null;
                    _logger.LogInformation(
                        "Radio Display streaming device index {Index} (maxW={MaxW}, fps={Fps}, q={Q})",
                        index, maxWidth, targetFps, jpegQuality);

                    using var frame = new Mat();
                    using var resized = new Mat();
                    var encodeParams = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality) };
                    var fpsWindow = Stopwatch.StartNew();
                    var streamStarted = Stopwatch.StartNew();
                    var framesInWindow = 0;
                    var emptyStreak = 0;
                    var slowReadStreak = 0;
                    var fpsCollapseStreak = 0;

                    while (!ct.IsCancellationRequested)
                    {
                        if (settingsAge.ElapsedMilliseconds >= SettingsRefreshMs)
                        {
                            settings = ReadSettings();
                            maxWidth = settings.VideoMaxWidth < 0 ? 0 : Math.Clamp(settings.VideoMaxWidth, 0, 1920);
                            targetFps = VideoFpsOptions.Normalize(settings.VideoTargetFps);
                            jpegQuality = Math.Clamp(settings.VideoJpegQuality, 40, 85);
                            frameInterval = TimeSpan.FromSeconds(1.0 / targetFps);
                            encodeParams = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality) };
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
                        if (_sessions.ViewerCount == 0)
                        {
                            var idleRemain = frameInterval - tick.Elapsed;
                            if (idleRemain > TimeSpan.Zero)
                                SleepOrCancel(idleRemain, ct);
                            continue;
                        }

                        Mat source = frame;
                        if (maxWidth > 0 && frame.Width > maxWidth)
                        {
                            var scale = (double)maxWidth / frame.Width;
                            var newH = Math.Max(1, (int)Math.Round(frame.Height * scale));
                            Cv2.Resize(frame, resized, new OpenCvSharp.Size(maxWidth, newH), 0, 0, InterpolationFlags.Area);
                            source = resized;
                        }

                        byte[]? jpegBytes = null;
                        try
                        {
                            jpegBytes = source.ImEncode(".jpg", encodeParams);
                        }
                        catch
                        {
                            SleepOrCancel(50, ct);
                            continue;
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
                            _frameWidth = source.Width;
                            _frameHeight = source.Height;
                        }

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

                        var remaining = frameInterval - tick.Elapsed;
                        if (remaining > TimeSpan.Zero)
                            SleepOrCancel(remaining, ct);
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

        private static VideoCapture? OpenCapture(int index)
        {
            // Prefer platform-native backends when available.
            VideoCaptureAPIs[] backends;
            if (OperatingSystem.IsWindows())
                backends = new[] { VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF, VideoCaptureAPIs.ANY };
            else if (OperatingSystem.IsLinux())
                backends = new[] { VideoCaptureAPIs.V4L2, VideoCaptureAPIs.ANY };
            else if (OperatingSystem.IsMacOS())
                backends = new[] { VideoCaptureAPIs.AVFOUNDATION, VideoCaptureAPIs.ANY };
            else
                backends = new[] { VideoCaptureAPIs.ANY };

            foreach (var api in backends)
            {
                try
                {
                    var cap = new VideoCapture(index, api);
                    if (cap.IsOpened())
                        return cap;
                    cap.Dispose();
                }
                catch
                {
                    // try next backend
                }
            }

            return null;
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
        /// Frame pacing must not use 50 ms <see cref="Thread.Sleep(int)"/>
        /// chunks: on Windows the timer quantum is ~15.6 ms, so Sleep(50)
        /// often overshoots a 15 fps slot (~67 ms) or a 30 fps slot (~33 ms)
        /// and the measured rate falls to ~13 / ~20.
        /// </summary>
        private static void SleepOrCancel(TimeSpan delay, CancellationToken ct)
        {
            if (delay <= TimeSpan.Zero)
                return;

            var sw = Stopwatch.StartNew();
            var spin = new SpinWait();
            while (!ct.IsCancellationRequested)
            {
                var left = delay - sw.Elapsed;
                if (left <= TimeSpan.Zero)
                    return;

                if (left > TimeSpan.FromMilliseconds(2))
                    Thread.Sleep(1);
                else
                    spin.SpinOnce();
            }
        }

        public void Dispose()
        {
            CancelIdleRelease();
            StopLoopAndWait();
        }
    }
}
