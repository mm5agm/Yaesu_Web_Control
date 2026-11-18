using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Audio;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// Click-to-centre for the radio audio waterfall: the page says which tone
    /// was clicked and where it should be heard, and this moves the dial.
    ///
    /// <para>Receive only. It never keys the radio, and it refuses while the
    /// radio is transmitting. Moving the VFO moves the transmit frequency with
    /// it (unless the operator is working split), which is right for search and
    /// pounce and wrong while running, and the manual says so.</para>
    /// </summary>
    [ApiController]
    [Route("api/audio-waterfall")]
    public class AudioWaterfallController : ControllerBase
    {
        private readonly ICatClient _cat;
        private readonly RadioStateService _state;
        private readonly ILogger<AudioWaterfallController> _logger;

        public AudioWaterfallController(ICatClient cat, RadioStateService state, ILogger<AudioWaterfallController> logger)
        {
            _cat = cat;
            _state = state;
            _logger = logger;
        }

        public sealed record CentreRequest(double FromHz, double? ToHz);

        public sealed record CentreResult(bool Moved, string Message, double? TargetHz = null,
                                          long? OffsetHz = null, long? FrequencyHz = null, string? Vfo = null);

        /// <summary>
        /// Moves the dial so the tone at FromHz is heard at ToHz. In CW the
        /// target is always the CW pitch, whatever the page sent.
        /// </summary>
        [HttpPost("centre")]
        public async Task<ActionResult<CentreResult>> Centre([FromBody] CentreRequest request, CancellationToken ct)
        {
            if (!_cat.IsConnected) return Ok(new CentreResult(false, "The radio is not connected."));
            if (_state.IsTransmitting) return Ok(new CentreResult(false, "Not while the radio is transmitting."));

            // Single-receiver radios like the FTdx10 hear whichever VFO is in
            // use; the dual-receiver ones send MAIN (VFO A) to the USB audio.
            bool useB = _state.IsSingleReceiver && _state.ActiveVfo == 1;
            string mode = (useB ? _state.ModeB : _state.ModeA) ?? "";
            string vfo = useB ? "B" : "A";

            if (AudioClickCentre.ToneRisesWithDial(mode) is not { } rises)
            {
                _logger.LogInformation("Audio waterfall: click at {From:0} Hz refused, VFO {Vfo} is in {Mode}", request.FromHz, vfo, mode);
                return Ok(new CentreResult(false,
                    $"Click-to-centre works in SSB, CW, RTTY and DATA-L/U, not {(mode.Length == 0 ? "this mode" : mode)}."));
            }

            double target = AudioClickCentre.IsCw(mode)
                ? AudioClickCentre.CwPitchHz(_state.CwPitch)
                : request.ToHz ?? 0;

            if (AudioClickCentre.DialMove(request.FromHz, target, rises) is not { } offset)
                return Ok(new CentreResult(false, "That tone is outside the waterfall.", target));
            if (Math.Abs(offset) < AudioClickCentre.MinMoveHz)
                return Ok(new CentreResult(false, $"Already centred on {target:0} Hz.", target, 0));

            try
            {
                // Read, do not assume: the cached figure may be a poll behind,
                // and this writes back an absolute frequency.
                long from = useB ? await _cat.QueryFrequencyBAsync("AudioCentre", ct)
                                 : await _cat.QueryFrequencyAAsync("AudioCentre", ct);
                if (from <= 0) return Ok(new CentreResult(false, "The radio did not say its frequency.", target));

                long to = from + offset;
                if (!RadioCapabilities.IsTunableFrequency(_state.RadioModel, to))
                    return Ok(new CentreResult(false, "That would tune outside the radio's range.", target));

                _state.MarkUserFrequencyWrite();   // hold off the FA/FB backstop poll
                bool ok = useB ? await _cat.SetFrequencyBAsync(to) : await _cat.SetFrequencyAAsync(to);
                if (!ok) return Ok(new CentreResult(false, "The radio did not take the new frequency.", target));
                if (useB) _state.FrequencyB = to; else _state.FrequencyA = to;

                _logger.LogInformation(
                    "Audio waterfall: {From:F0} Hz to {Target:F0} Hz in {Mode} - moved VFO {Vfo} {Offset:+#;-#;0} Hz, {Was} to {Now}",
                    request.FromHz, target, mode, vfo, offset, from, to);
                return Ok(new CentreResult(true,
                    $"Moved VFO {vfo} {offset:+#;-#;0} Hz to put it on {target:0} Hz.", target, offset, to, vfo));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio waterfall: could not move the VFO");
                return Ok(new CentreResult(false, "Could not move the VFO.", target));
            }
        }
    }
}
