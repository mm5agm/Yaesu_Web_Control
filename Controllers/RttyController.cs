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

        /// <summary>Start the scope, or move its filters if it is already running.</summary>
        [HttpPost("tuner/start")]
        public async Task<IActionResult> Start([FromBody] StartRequest? req)
        {
            req ??= new StartRequest();
            try
            {
                var error = await _tuner.StartAsync(req.MarkHz, req.ShiftHz, req.Reverse);
                if (error != null) return BadRequest(new { error });
                return Ok(_tuner.Frame(0));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the RTTY tuner");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>The dialog has closed. The audio is let go a couple of seconds later.</summary>
        [HttpPost("tuner/stop")]
        public IActionResult Stop()
        {
            _tuner.RequestStop();
            return Ok();
        }

        /// <summary>
        /// The latest <paramref name="points"/> points of the figure, at
        /// 24,000 a second: 500 is about 21 ms, one sweep of the scope.
        /// </summary>
        [HttpGet("tuner")]
        public IActionResult Tuner([FromQuery] int points = 500)
            => Ok(_tuner.Frame(Math.Clamp(points, 0, 4800)));
    }
}
