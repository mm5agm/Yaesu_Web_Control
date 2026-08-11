using System.Text;
using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Video;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/video")]
    public class VideoController : ControllerBase
    {
        private static readonly byte[] NewLine = Encoding.ASCII.GetBytes("\r\n");
        private const string Boundary = "frame";

        private readonly ISettingsService _settings;
        private readonly VideoCaptureService _capture;
        private readonly VideoSessionManager _sessions;
        private readonly ILogger<VideoController> _logger;

        public VideoController(
            ISettingsService settings,
            VideoCaptureService capture,
            VideoSessionManager sessions,
            ILogger<VideoController> logger)
        {
            _settings = settings;
            _capture = capture;
            _sessions = sessions;
            _logger = logger;
        }

        [HttpGet("devices")]
        public IActionResult ListDevices()
        {
            try
            {
                var devices = VideoDeviceEnumerator.ListDevices()
                    .Select(d => new { key = d.Key, label = d.Label, index = d.Index });
                var notes = OperatingSystem.IsLinux()
                    ? "Linux: devices from /sys/class/video4linux. Map /dev/video* into Docker and add the video group."
                    : "Select a USB webcam or HDMI capture dongle. Indexes can shift when devices are replugged.";
                return Ok(new { devices, notes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate video devices");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            var s = await _settings.GetSettingsAsync();
            return Ok(new
            {
                enabled = s.VideoDisplayEnabled,
                deviceKey = s.VideoCaptureDeviceKey ?? "",
                status = _capture.Status,
                error = _capture.LastError,
                width = _capture.FrameWidth,
                height = _capture.FrameHeight,
                fps = Math.Round(_capture.MeasuredFps, 1),
                viewers = _sessions.ViewerCount,
                maxWidth = s.VideoMaxWidth,
                targetFps = s.VideoTargetFps,
                jpegQuality = s.VideoJpegQuality
            });
        }

        public sealed class DeviceRequest
        {
            public string? Key { get; set; }
        }

        /// <summary>
        /// Persist the capture device key and reopen the stream if viewers are connected.
        /// </summary>
        [HttpPost("device")]
        public async Task<IActionResult> SetDevice([FromBody] DeviceRequest? body)
        {
            var key = (body?.Key ?? "").Trim();
            if (!string.IsNullOrEmpty(key) && !VideoDeviceKey.TryParseIndex(key, out _))
                return BadRequest(new { error = "Invalid device key." });

            var s = await _settings.GetSettingsAsync();
            if (!s.VideoDisplayEnabled)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Radio Display is disabled in Settings." });

            s.VideoCaptureDeviceKey = key;
            await _settings.SaveSettingsAsync(s);
            _capture.RequestRestart();
            _logger.LogInformation("Radio Display device set to {Key}", string.IsNullOrEmpty(key) ? "(none)" : key);
            return Ok(new { deviceKey = s.VideoCaptureDeviceKey, status = _capture.Status });
        }

        public sealed class FpsRequest
        {
            public int? Fps { get; set; }
        }

        /// <summary>Persist encode FPS (15 / 30 / 40 / 60). Applied on the next frame cycle.</summary>
        [HttpPost("fps")]
        public async Task<IActionResult> SetFps([FromBody] FpsRequest? body)
        {
            var s = await _settings.GetSettingsAsync();
            if (!s.VideoDisplayEnabled)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Radio Display is disabled in Settings." });

            s.VideoTargetFps = VideoFpsOptions.Normalize(body?.Fps ?? s.VideoTargetFps);
            await _settings.SaveSettingsAsync(s);
            return Ok(new { targetFps = s.VideoTargetFps });
        }

        /// <summary>
        /// MJPEG multipart stream. Opens capture on first viewer; releases on disconnect.
        /// </summary>
        [HttpGet("stream")]
        public async Task Stream(CancellationToken cancellationToken)
        {
            var settings = await _settings.GetSettingsAsync();
            if (!settings.VideoDisplayEnabled)
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                await Response.WriteAsync("Radio Display is disabled in Settings.", cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.VideoCaptureDeviceKey))
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                await Response.WriteAsync("No video capture device selected in Settings.", cancellationToken);
                return;
            }

            var viewerId = Guid.NewGuid().ToString("N");
            try
            {
                await _capture.AcquireViewerAsync(viewerId, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                await Response.WriteAsync(ex.Message, cancellationToken);
                return;
            }

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = $"multipart/x-mixed-replace; boundary={Boundary}";
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            Response.Headers.Pragma = "no-cache";
            Response.Headers.Connection = "keep-alive";

            // Disable response buffering so frames flush promptly.
            var feature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            feature?.DisableBuffering();

            long lastSeq = 0;
            try
            {
                await Response.StartAsync(cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await _capture.WaitForFrameAsync(lastSeq, cancellationToken);
                    if (frame is null)
                    {
                        // Keep the connection alive while reconnecting.
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }

                    var (jpeg, seq) = frame.Value;
                    lastSeq = seq;

                    var header =
                        $"--{Boundary}\r\n" +
                        "Content-Type: image/jpeg\r\n" +
                        $"Content-Length: {jpeg.Length}\r\n\r\n";
                    var headerBytes = Encoding.ASCII.GetBytes(header);

                    await Response.Body.WriteAsync(headerBytes, cancellationToken);
                    await Response.Body.WriteAsync(jpeg, cancellationToken);
                    await Response.Body.WriteAsync(NewLine, cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // client disconnected
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Radio Display MJPEG stream ended");
            }
            finally
            {
                _capture.ReleaseViewer(viewerId);
            }
        }
    }
}
