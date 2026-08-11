using System.Diagnostics;
using OpenCvSharp;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Single shared capture/encode loop. Opens the UVC device only while
    /// viewers are attached; publishes the latest JPEG for MJPEG fan-out.
    /// </summary>
    public sealed class VideoCaptureService : IDisposable
    {
        private readonly ISettingsService _settings;
        private readonly VideoSessionManager _sessions;
        private readonly ILogger<VideoCaptureService> _logger;
        private readonly object _frameLock = new();
        private readonly object _lifecycleLock = new();

        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private byte[]? _latestJpeg;
        private long _frameSeq;
        private int _frameWidth;
        private int _frameHeight;
        private double _measuredFps;
        private string _status = "idle";
        private string? _lastError;

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
            EnsureLoopStarted();
            return viewerId;
        }

        public void ReleaseViewer(string viewerId)
        {
            if (_sessions.Release(viewerId, out var remaining) && remaining == 0)
                StopLoop();
        }

        /// <summary>
        /// Cancel and reopen the capture loop (e.g. after the device key changes).
        /// No-op when no viewers are attached.
        /// </summary>
        public void RequestRestart()
        {
            lock (_lifecycleLock)
            {
                var hadLoop = _loopTask is { IsCompleted: false };
                try { _loopCts?.Cancel(); } catch { /* ignore */ }
                _loopCts = null;
                _loopTask = null;

                lock (_frameLock)
                {
                    _latestJpeg = null;
                    _frameSeq = 0;
                    _frameWidth = 0;
                    _frameHeight = 0;
                }

                if (hadLoop && _sessions.ViewerCount > 0)
                {
                    SetStatus("connecting");
                    _loopCts = new CancellationTokenSource();
                    var token = _loopCts.Token;
                    _loopTask = Task.Run(() => CaptureLoopAsync(token), CancellationToken.None);
                    _logger.LogInformation("Radio Display capture restart requested");
                }
            }
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
                _loopTask = Task.Run(() => CaptureLoopAsync(token), CancellationToken.None);
            }
        }

        private void StopLoop()
        {
            lock (_lifecycleLock)
            {
                try { _loopCts?.Cancel(); } catch { /* ignore */ }
                _loopCts = null;
                _loopTask = null;
            }

            SetStatus("idle");
            lock (_frameLock)
            {
                _latestJpeg = null;
                _frameSeq = 0;
                _frameWidth = 0;
                _frameHeight = 0;
            }
            _logger.LogInformation("Radio Display capture stopped (no viewers)");
        }

        private async Task CaptureLoopAsync(CancellationToken ct)
        {
            SetStatus("connecting");
            _lastError = null;

            while (!ct.IsCancellationRequested && _sessions.ViewerCount > 0)
            {
                var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
                if (!settings.VideoDisplayEnabled ||
                    string.IsNullOrWhiteSpace(settings.VideoCaptureDeviceKey) ||
                    !VideoDeviceKey.TryParseIndex(settings.VideoCaptureDeviceKey, out var index))
                {
                    SetStatus("unconfigured");
                    _lastError = "No capture device configured.";
                    await DelayOrCancel(1000, ct).ConfigureAwait(false);
                    continue;
                }

                var maxWidth = settings.VideoMaxWidth < 0 ? 0 : Math.Clamp(settings.VideoMaxWidth, 0, 1920);
                var targetFps = VideoFpsOptions.Normalize(settings.VideoTargetFps);
                var jpegQuality = Math.Clamp(settings.VideoJpegQuality, 40, 85);
                var frameInterval = TimeSpan.FromSeconds(1.0 / targetFps);

                VideoCapture? cap = null;
                try
                {
                    cap = OpenCapture(index);
                    if (cap is null || !cap.IsOpened())
                    {
                        SetStatus("disconnected");
                        _lastError = $"Could not open capture device index {index}.";
                        _logger.LogWarning("Radio Display: failed to open device index {Index}", index);
                        await DelayOrCancel(2000, ct).ConfigureAwait(false);
                        continue;
                    }

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
                    var framesInWindow = 0;

                    while (!ct.IsCancellationRequested && _sessions.ViewerCount > 0)
                    {
                        // Hot-reload quality knobs without reopening the device.
                        settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
                        maxWidth = settings.VideoMaxWidth < 0 ? 0 : Math.Clamp(settings.VideoMaxWidth, 0, 1920);
                        targetFps = VideoFpsOptions.Normalize(settings.VideoTargetFps);
                        jpegQuality = Math.Clamp(settings.VideoJpegQuality, 40, 85);
                        frameInterval = TimeSpan.FromSeconds(1.0 / targetFps);
                        encodeParams = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality) };

                        if (!settings.VideoDisplayEnabled ||
                            !VideoDeviceKey.TryParseIndex(settings.VideoCaptureDeviceKey, out var newIndex) ||
                            newIndex != index)
                        {
                            break; // reopen with new device / disabled
                        }

                        var tick = Stopwatch.StartNew();
                        if (!cap.Read(frame) || frame.Empty())
                        {
                            SetStatus("disconnected");
                            _lastError = "Capture device stopped delivering frames.";
                            break;
                        }

                        Mat source = frame;
                        if (maxWidth > 0 && frame.Width > maxWidth)
                        {
                            var scale = (double)maxWidth / frame.Width;
                            var newH = Math.Max(1, (int)Math.Round(frame.Height * scale));
                            Cv2.Resize(frame, resized, new Size(maxWidth, newH), 0, 0, InterpolationFlags.Area);
                            source = resized;
                        }

                        byte[]? jpegBytes = null;
                        try
                        {
                            jpegBytes = source.ImEncode(".jpg", encodeParams);
                        }
                        catch
                        {
                            await DelayOrCancel(50, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (jpegBytes is null || jpegBytes.Length == 0)
                        {
                            await DelayOrCancel(50, ct).ConfigureAwait(false);
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
                            Volatile.Write(ref _measuredFps, framesInWindow * 1000.0 / fpsWindow.ElapsedMilliseconds);
                            framesInWindow = 0;
                            fpsWindow.Restart();
                        }

                        var remaining = frameInterval - tick.Elapsed;
                        if (remaining > TimeSpan.Zero)
                            await DelayOrCancel(remaining, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetStatus("disconnected");
                    _lastError = ex.Message;
                    _logger.LogWarning(ex, "Radio Display capture error");
                    await DelayOrCancel(2000, ct).ConfigureAwait(false);
                }
                finally
                {
                    try { cap?.Release(); } catch { /* ignore */ }
                    try { cap?.Dispose(); } catch { /* ignore */ }
                }

                if (ct.IsCancellationRequested || _sessions.ViewerCount == 0)
                    break;

                SetStatus("connecting");
                await DelayOrCancel(1000, ct).ConfigureAwait(false);
            }

            if (_sessions.ViewerCount == 0)
                SetStatus("idle");
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

        private static async Task DelayOrCancel(TimeSpan delay, CancellationToken ct)
        {
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* swallow */ }
        }

        private static Task DelayOrCancel(int ms, CancellationToken ct) =>
            DelayOrCancel(TimeSpan.FromMilliseconds(ms), ct);

        public void Dispose()
        {
            StopLoop();
        }
    }
}
