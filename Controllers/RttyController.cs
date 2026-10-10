using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
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
        private readonly RttyAutoService _auto;
        private readonly RttyRadioToneService _tones;
        private readonly RadioStateService _state;
        private readonly ILogger<RttyController> _logger;

        public RttyController(RttyTunerService tuner,
                              RttyReaderService reader,
                              RttyAutoService auto,
                              RttyRadioToneService tones,
                              RadioStateService state,
                              ILogger<RttyController> logger)
        {
            _tuner = tuner;
            _reader = reader;
            _auto = auto;
            _tones = tones;
            _state = state;
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

        // ---- Auto, and the radio's own menu ------------------------------

        /// <summary>
        /// A long recording of the receive audio to a WAV, for examining a signal
        /// after the event instead of during it. Changes nothing on the radio.
        /// </summary>
        [HttpPost("capture")]
        public async Task<IActionResult> Capture(double seconds = 60, string? name = null)
        {
            try
            {
                var (path, error) = await _auto.RecordAsync(
                    seconds, name, HttpContext.RequestAborted);

                return error != null
                    ? Ok(new { ok = false, reason = error })
                    : Ok(new { ok = true, path });
            }
            catch (OperationCanceledException)
            {
                return Ok(new { ok = false, reason = "Cancelled." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RTTY capture failed");
                return Ok(new { ok = false, reason = ex.Message });
            }
        }

        /// <summary>
        /// Listen for a few seconds and work out what is being sent: the tones, the
        /// shift, which way round they are and the speed.
        ///
        /// <para>Always 200, like <c>radio-tones</c> and for the same reason - the
        /// shared tuner uses the shape of the reply to decide whether to show the
        /// Auto button at all, so a 404 has to mean "this app cannot do it" and
        /// nothing else. <c>ok: false</c> with a reason means it could not hear a
        /// signal, which is a message for the operator rather than a missing
        /// feature.</para>
        ///
        /// <para>It takes about four seconds to answer, and it holds the request
        /// open for that long rather than returning a job to poll. The browser has
        /// one button disabled meanwhile and nothing else to do.</para>
        /// </summary>
        ///
        /// <param name="body">
        /// Optionally where the tuner's mark filter is, so that a confident answer
        /// can move the dial to bring the signal onto it. Optional because the
        /// mark is the dialog's own setting and the dialog is the authority on it -
        /// the server's copy is only as fresh as the last start - and because a
        /// request that does not say must leave the radio alone rather than centre
        /// on a default the operator may have changed.
        /// </param>
        [HttpPost("auto")]
        public async Task<IActionResult> Auto([FromBody] AutoRequest? body = null)
        {
            try
            {
                return Ok(await _auto.AnalyseAsync(body?.MarkHz, HttpContext.RequestAborted));
            }
            catch (OperationCanceledException)
            {
                // The operator closed the dialog or the tab while it was listening.
                return Ok(RttyAutoResult.Failed("Cancelled."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RTTY Auto failed");
                return Ok(RttyAutoResult.Failed(ex.Message));
            }
        }

        /// <summary>
        /// The radio's own RTTY mark and shift, for the tuner's "From radio"
        /// button. Always 200 with an <c>ok</c> flag rather than a 404 on a
        /// miss, because the shared tuner uses the shape of this reply to
        /// decide whether to show the button at all: a 404 means "this app
        /// cannot do it, hide the button", and ok:false means "it can, but not
        /// right now" - which is a message, not a missing feature.
        ///
        /// <para>This reads the same two EX items as <c>GET /api/cat/rtty</c>,
        /// which has been here since click-to-tune in RTTY and is used by the
        /// main page. It is not folded into that one: this path and this reply
        /// shape are the shared tuner's, identical in both apps, and that one's
        /// are the Yaesu CAT API's. Both go through
        /// <see cref="RttyRadioToneService"/>, so there is one reader of the
        /// menu even though there are two routes onto it.</para>
        /// </summary>
        [HttpGet("radio-tones")]
        public async Task<IActionResult> RadioTones()
        {
            try
            {
                var t = await _tones.ReadAsync(HttpContext.RequestAborted);
                if (t is not { } tones)
                    return Ok(new { ok = false, reason = "The radio did not answer." });

                // FSK only. In an AFSK mode the tones are the operator's
                // software's and the radio's menu is not describing them, so
                // say so rather than handing over numbers that do not apply.
                var mode = _state.ModeA ?? "";
                bool fsk = mode.StartsWith("RTTY", StringComparison.OrdinalIgnoreCase);

                return Ok(new
                {
                    ok       = true,
                    markHz   = tones.MarkHz,
                    shiftHz  = tones.ShiftHz,
                    mode,
                    fsk,
                    note     = fsk
                        ? null
                        : $"These are the radio's FSK settings; it is in {mode}, "
                          + "where your software makes the tones."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read the radio's RTTY tone settings");
                return Ok(new { ok = false, reason = ex.Message });
            }
        }

        public sealed record RadioTonesRequest(int MarkHz, int ShiftHz);

        /// <summary>
        /// Push the tuner's Mark and Shift into the radio's own RTTY menu - the
        /// MARK FREQUENCY and SHIFT FREQUENCY items, whose EX addresses differ
        /// per model and live in <see cref="RttyToneMap"/> - so the two agree
        /// without a trip to the front panel. The counterpart of the GET above
        /// and of the tuner's "From radio" button.
        ///
        /// FSK only, for the same reason the GET says so: in an AFSK mode those
        /// menu items describe a transmitter the operator is not using, and
        /// writing them would change how the radio transmits on the strength of
        /// a receive-side tuning aid.
        ///
        /// Always 200. The body says what the radio took: a null markHz or
        /// shiftHz means this radio has no rung for that value - the menu items
        /// are one-byte indexes into a short fixed list, and 450 Hz, perfectly
        /// ordinary on the air and offered by the tuner, simply is not on it.
        /// </summary>
        [HttpPost("radio-tones")]
        public async Task<IActionResult> SetRadioTones([FromBody] RadioTonesRequest req)
        {
            var mode = _state.ModeA ?? "";
            if (!mode.StartsWith("RTTY", StringComparison.OrdinalIgnoreCase))
                return Ok(new
                {
                    ok     = false,
                    mode,
                    fsk    = false,
                    reason = $"The radio is in {mode}, where your software makes the tones, "
                             + "so its RTTY menu was left alone."
                });

            try
            {
                var w = await _tones.WriteAsync(req.MarkHz, req.ShiftHz,
                                                HttpContext.RequestAborted);
                return Ok(new
                {
                    ok      = w.MarkHz != null || w.ShiftHz != null,
                    mode,
                    fsk     = true,
                    markHz  = w.MarkHz,
                    shiftHz = w.ShiftHz,
                    reason  = w.MarkHz == null && w.ShiftHz == null
                        ? "The radio has no setting for those tones."
                        : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write the radio's RTTY tone settings");
                return Ok(new { ok = false, reason = ex.Message });
            }
        }
    }

    /// <summary>
    /// What the browser sends with an Auto request. A record with one optional
    /// field rather than a query parameter, so that the shared tuner can send it
    /// to an app that does not read it yet without the request failing.
    /// </summary>
    /// <param name="MarkHz">The tuner's mark, in audio Hz.</param>
    public sealed record AutoRequest(double? MarkHz);
}
