using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services.Cw;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// The CW reader's HTTP face: start it, stop it, poll it for text.
    ///
    /// Polling rather than a WebSocket on purpose. Decoded CW arrives a few
    /// characters at a time at a handful of characters a second, so a poll
    /// every half second is both cheap and quick enough to read as live, and
    /// it needs none of the reconnection handling a socket would. The audio
    /// bridge has a socket because audio cannot tolerate that latency; text
    /// can.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class CwController : ControllerBase
    {
        private readonly CwReaderService _reader;
        private readonly CwQsoLogService _log;
        private readonly ILogger<CwController> _logger;

        public CwController(CwReaderService reader,
                            CwQsoLogService log,
                            ILogger<CwController> logger)
        {
            _reader = reader;
            _log = log;
            _logger = logger;
        }

        [HttpPost("start")]
        public async Task<IActionResult> Start(CancellationToken ct)
        {
            try
            {
                await _reader.StartAsync(ct);
                return Ok(_reader.Snapshot(0));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the CW reader");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("stop")]
        public async Task<IActionResult> Stop(CancellationToken ct)
        {
            try
            {
                await _reader.StopAsync(ct);
                return Ok(_reader.Snapshot(long.MaxValue));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop the CW reader");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("clear")]
        public IActionResult Clear()
        {
            _reader.ClearText();
            return Ok(_reader.Snapshot(long.MaxValue));
        }

        /// <summary>
        /// Status plus whatever has been decoded since the caller's cursor.
        /// Pass the Cursor from the previous reply; 0 asks for everything the
        /// reader still holds.
        /// </summary>
        [HttpGet("poll")]
        public IActionResult Poll([FromQuery] long since = 0)
            => Ok(_reader.Snapshot(since));

        /// <summary>
        /// Points for the phasor tuning aid since the caller's cursor.
        ///
        /// Separate from poll because it is only wanted while the aid is
        /// visible, and it carries roughly two hundred points a second when it
        /// is. Pass the Cursor from the previous reply; 0 asks for whatever the
        /// ring still holds.
        /// </summary>
        [HttpGet("phasor")]
        public IActionResult Phasor([FromQuery] long since = 0)
            => Ok(_reader.Phasor(since));

        /// <summary>
        /// The passband spectrum for the tuning display.
        ///
        /// No cursor, unlike phasor: this is a picture of the moment rather
        /// than a stream, so a dropped poll costs nothing and the caller never
        /// has to catch up.
        /// </summary>
        [HttpGet("spectrum")]
        public IActionResult Spectrum()
            => Ok(_reader.Spectrum());

        /// <summary>
        /// A draft log entry built from the recent copy and the radio's current
        /// state, for the operator to correct before saving.
        ///
        /// Every field comes back as a ranked list with the reason it was
        /// suggested, not as an answer. The decoder has been measured
        /// reporting full confidence on junk, so a form silently pre-filled
        /// from it would be worse than an empty one - the operator would have
        /// no reason to look at it twice.
        /// </summary>
        [HttpGet("qso/suggest")]
        public async Task<IActionResult> SuggestQso()
        {
            try
            {
                return Ok(await _log.SuggestAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build a QSO suggestion");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Appends a confirmed QSO to the ADIF log.</summary>
        [HttpPost("qso")]
        public async Task<IActionResult> SaveQso([FromBody] CwQsoSave qso, CancellationToken ct)
        {
            if (qso is null || string.IsNullOrWhiteSpace(qso.Callsign))
                return BadRequest(new { error = "A QSO needs a callsign." });

            try
            {
                return Ok(await _log.SaveAsync(qso, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log a QSO");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Where the log and this session's transcript are on disk.</summary>
        [HttpGet("files")]
        public IActionResult Files() => Ok(new
        {
            log = CwQsoLogService.LogPath,
            logExists = System.IO.File.Exists(CwQsoLogService.LogPath),
            transcript = _reader.TranscriptPath,
        });
    }
}
