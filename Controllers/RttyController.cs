using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services.Rtty;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// The RTTY tuning scope's HTTP face. Polled, like the CW phasor: the
    /// browser asks for the latest sweep each time it redraws, so a dropped
    /// request costs one frame and there is nothing to reconnect.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class RttyController : ControllerBase
    {
        private readonly RttyTunerService _tuner;
        private readonly RttyReaderService _reader;
        private readonly ILogger<RttyController> _logger;

        public RttyController(RttyTunerService tuner,
                              RttyReaderService reader,
                              ILogger<RttyController> logger)
        {
            _tuner = tuner;
            _reader = reader;
            _logger = logger;
        }

        public sealed class StartRequest
        {
            public double MarkHz  { get; set; } = RttyTunerService.DefaultMarkHz;
            public int    ShiftHz { get; set; } = RttyTunerService.DefaultShiftHz;
            public bool   Reverse { get; set; }
            public double Baud    { get; set; } = RttyTunerService.DefaultBaud;
        }

        /// <summary>
        /// Which window is asking: each open tuner holds the audio under its
        /// own id, so one closing does not stop the others. A page that sends
        /// none shares one lease, as every page did before.
        /// </summary>
        private static string? ClientId(string? client)
            => string.IsNullOrEmpty(client) || client.Length > 64 ? null : client;

        /// <summary>Start the scope, or move its filters if it is already running.</summary>
        [HttpPost("tuner/start")]
        public async Task<IActionResult> Start([FromBody] StartRequest? req, [FromQuery] string? client = null)
        {
            var id = ClientId(client);
            req ??= new StartRequest();
            try
            {
                var error = await _tuner.StartAsync(req.MarkHz, req.ShiftHz, req.Reverse, req.Baud, id);
                if (error != null) return BadRequest(new { error });
                return Ok(_tuner.Frame(0, id));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the RTTY tuner");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// This window's dialog has closed. The audio is let go a couple of
        /// seconds later, once no other window has the tuner open.
        /// </summary>
        [HttpPost("tuner/stop")]
        public IActionResult Stop([FromQuery] string? client = null)
        {
            _tuner.RequestStop(ClientId(client));
            return Ok();
        }

        /// <summary>
        /// The latest <paramref name="points"/> points of the figure, at
        /// 24,000 a second: 500 is about 21 ms, one sweep of the scope.
        /// </summary>
        [HttpGet("tuner")]
        public IActionResult Tuner([FromQuery] int points = 500, [FromQuery] string? client = null)
            => Ok(_tuner.Frame(Math.Clamp(points, 0, 4800), ClientId(client)));

        // ---- The reader --------------------------------------------------
        //
        // Polled like the CW reader and for the same reason: RTTY arrives at
        // six characters a second, so asking twice a second reads as live and
        // costs nothing. Each poll sends back the cursor from the previous
        // reply and gets only what is new.
        //
        // The reader takes mark, shift, reverse and speed from the tuner, so
        // none of them appear here. That is the point of the one panel - the
        // operator tunes the cross and the decoder is already listening in the
        // right place.
        //
        // There is no client id on these, unlike the tuner's: the tuner draws
        // a figure per window and so holds the audio per window, while the
        // reader decodes one stream of text that every window reads the same
        // copy of.

        public sealed class ReaderStartRequest
        {
            /// <summary>
            /// Which figures table to print above the shift: "Ita2" or
            /// "UsTty". Omitted keeps what the operator last chose.
            /// </summary>
            public string? Figures { get; set; }

            /// <summary>Unshift on space. Omitted keeps the current setting.</summary>
            public bool? Usos { get; set; }
        }

        /// <summary>Start decoding, or re-apply the alphabet to a running decoder.</summary>
        [HttpPost("reader/start")]
        public async Task<IActionResult> ReaderStart([FromBody] ReaderStartRequest? req = null)
        {
            RadioWebControl.Core.Services.Rtty.RttyFigureSet? set = null;
            if (!string.IsNullOrWhiteSpace(req?.Figures))
            {
                if (!Enum.TryParse<RadioWebControl.Core.Services.Rtty.RttyFigureSet>(
                        req!.Figures, ignoreCase: true, out var parsed))
                    return BadRequest(new { error = $"Unknown figures table \"{req.Figures}\"." });
                set = parsed;
            }

            try
            {
                await _reader.StartAsync(set, req?.Usos, HttpContext.RequestAborted);
                return Ok(_reader.Snapshot(0));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the RTTY reader");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Stop decoding and let the audio go. The text stays, so the operator
        /// can read the bulletin they just stopped.
        /// </summary>
        [HttpPost("reader/stop")]
        public async Task<IActionResult> ReaderStop()
        {
            try
            {
                await _reader.StopAsync(HttpContext.RequestAborted);
                return Ok(_reader.Snapshot(long.MaxValue));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop the RTTY reader");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Throw the text away, leaving the decoder running.</summary>
        [HttpPost("reader/clear")]
        public IActionResult ReaderClear()
        {
            _reader.ClearText();
            // The cursor the caller should carry on from, so the next poll asks
            // for what comes after the clear rather than replaying the buffer.
            return Ok(_reader.Snapshot(long.MaxValue));
        }

        /// <summary>
        /// What has been decoded since <paramref name="since"/>, plus
        /// everything the panel's status line needs.
        /// </summary>
        [HttpGet("reader")]
        public IActionResult Reader([FromQuery] long since = 0)
            => Ok(_reader.Snapshot(Math.Max(0, since)));
    }
}
