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
        private readonly ILogger<RttyController> _logger;

        public RttyController(RttyTunerService tuner, ILogger<RttyController> logger)
        {
            _tuner = tuner;
            _logger = logger;
        }

        public sealed class StartRequest
        {
            public double MarkHz  { get; set; } = RttyTunerService.DefaultMarkHz;
            public int    ShiftHz { get; set; } = RttyTunerService.DefaultShiftHz;
            public bool   Reverse { get; set; }
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
                var error = await _tuner.StartAsync(req.MarkHz, req.ShiftHz, req.Reverse, id);
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
    }
}
