using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.RegularExpressions;
using Yaesu_Web_Control.Models.Alexa;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Controllers;

// Webhook endpoint for Amazon Alexa voice-control intents.
// See VOICE_CONTROL.md for the full setup. This controller handles the
// JSON request format documented at:
//   https://developer.amazon.com/en-US/docs/alexa/custom-skills/request-and-response-json-reference.html
//
// The endpoint is dormant by default — controlled by Settings.AlexaEnabled.
// A request received while AlexaEnabled=false returns 404, so a fresh YWC
// install that hasn't gone through VOICE_CONTROL.md setup is not exposing
// a voice-controlled rig surface.
//
// Signature verification (Phase 2.5) is required before exposing the endpoint
// to the public Cloudflare tunnel. Until that lands, the
// AlexaSkipSignatureVerification setting bypasses the check for local testing
// via curl. Both flags MUST be flipped before going live with a real Echo.
[ApiController]
[Route("api/alexa")]
public class AlexaController : ControllerBase
{
    private readonly RadioStateService _radioState;
    private readonly ICatClient _catClient;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<AlexaController> _logger;

    public AlexaController(
        RadioStateService radioState,
        ICatClient catClient,
        ISettingsService settingsService,
        ILogger<AlexaController> logger)
    {
        _radioState      = radioState;
        _catClient       = catClient;
        _settingsService = settingsService;
        _logger          = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Handle([FromBody] AlexaRequest request)
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.AlexaEnabled)
        {
            _logger.LogWarning("Alexa request received but Settings.AlexaEnabled=false. Returning 404.");
            return NotFound();
        }

        // TODO Phase 2.5: verify the Amazon SHA-256 signature using the
        // Signature and SignatureCertChainUrl headers. Until then, only
        // accept requests when AlexaSkipSignatureVerification=true so
        // a misconfigured public install can't accept fake intents.
        if (!settings.AlexaSkipSignatureVerification)
        {
            _logger.LogWarning(
                "Alexa request received but signature verification is not yet implemented. " +
                "Set AlexaSkipSignatureVerification=true only for local development testing.");
            return StatusCode(503, AlexaResponse.Speak(
                "Voice control signature verification is not yet configured. Please check the setup guide."));
        }

        try
        {
            return request.Request.Type switch
            {
                "LaunchRequest" => Ok(AlexaResponse.Speak(
                    "Yaesu Web Control is ready. You can ask me to go to a band, set a frequency, change mode, or get the rig status.",
                    endSession: false)),

                "IntentRequest" => Ok(await DispatchIntent(request.Request.Intent ?? new())),

                "SessionEndedRequest" => Ok(AlexaResponse.Speak("")),

                _ => HandleUnknownType(request.Request.Type)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alexa request handler error");
            return Ok(AlexaResponse.Speak("Sorry, something went wrong handling that request."));
        }
    }

    private IActionResult HandleUnknownType(string type)
    {
        _logger.LogWarning("Alexa request: unknown type {Type}", type);
        return Ok(AlexaResponse.Speak("Sorry, I didn't understand that request type."));
    }

    private async Task<AlexaResponse> DispatchIntent(AlexaIntent intent)
    {
        _logger.LogInformation("Alexa intent: {Name}", intent.Name);

        return intent.Name switch
        {
            "SetBandIntent"      => await HandleSetBand(intent),
            "SetFrequencyIntent" => await HandleSetFrequency(intent),
            "SetModeIntent"      => await HandleSetMode(intent),
            "RigStatusIntent"    => HandleRigStatus(),

            // Amazon's built-in intents — handle gracefully even if the Skill
            // definition includes them by default.
            "AMAZON.HelpIntent"   => AlexaResponse.Speak(
                "You can say: go to forty metres, set frequency to fourteen point zero seven four megahertz, " +
                "set mode to USB, or rig status. What would you like?",
                endSession: false),
            "AMAZON.StopIntent" or "AMAZON.CancelIntent" => AlexaResponse.Speak("Goodbye."),

            _ => AlexaResponse.Speak($"Sorry, I don't know how to handle the {intent.Name} intent.")
        };
    }

    // ── SetBandIntent ───────────────────────────────────────────────────────
    // Slot: "band" — Amazon returns the spoken band as a string.
    // Examples: "40 metres", "40m", "forty metres", "20", "20 m".
    // We extract digits and map to YWC's band string "Xm" / "Xcm".
    private static readonly HashSet<string> SupportedBands = new(StringComparer.OrdinalIgnoreCase)
    {
        "160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m", "4m"
    };

    private async Task<AlexaResponse> HandleSetBand(AlexaIntent intent)
    {
        var raw = intent.Slots.GetValueOrDefault("band")?.Value ?? "";

        // Pull the first integer from the spoken value. "40 metres", "40m",
        // "forty metres" (Amazon usually normalises to digits), all give us "40".
        var match = Regex.Match(raw, @"\d+");
        if (!match.Success)
        {
            return AlexaResponse.Speak($"Sorry, I didn't catch which band. You said {raw}.");
        }

        var band = $"{match.Value}m";
        if (!SupportedBands.Contains(band))
        {
            return AlexaResponse.Speak($"Sorry, {band} isn't one of the bands I support.");
        }

        // YWC's BandFreqs in CatController has the canonical band-to-default-freq
        // mapping. Replicating the relevant ones here for the Alexa handler so
        // we don't take a dependency on CatController's internal table.
        long bandFreq = band switch
        {
            "160m" => 1840000,  "80m"  => 3700000,  "60m"  => 5357000,
            "40m"  => 7100000,  "30m"  => 10136000, "20m"  => 14074000,
            "17m"  => 18110000, "15m"  => 21074000, "12m"  => 24915000,
            "10m"  => 28074000, "6m"   => 50313000, "4m"   => 70100000,
            _ => 14074000
        };

        var command = $"FA{bandFreq:D9};";
        await _catClient.SendCommandAsync(command, "Alexa", CancellationToken.None);
        _radioState.SetBand("A", band);
        _radioState.FrequencyA = bandFreq;

        _logger.LogInformation("Alexa SetBandIntent: tuned VFO A to {Band} ({Freq} Hz)", band, bandFreq);
        return AlexaResponse.Speak($"Tuned to {band.TrimEnd('m')} metres.");
    }

    // ── SetFrequencyIntent ──────────────────────────────────────────────────
    // Slot: "frequencyMHz" — a number like "14.074" or "7.1".
    // We parse as a double, convert to Hz, clamp to the FTdx101MP range.
    private async Task<AlexaResponse> HandleSetFrequency(AlexaIntent intent)
    {
        var raw = intent.Slots.GetValueOrDefault("frequencyMHz")?.Value ?? "";

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
        {
            return AlexaResponse.Speak($"Sorry, I didn't understand the frequency {raw}.");
        }

        // Clamp to FTdx101MP's HF coverage (30 kHz to 75 MHz).
        long hz = (long)Math.Round(mhz * 1_000_000);
        if (hz < 30_000 || hz > 75_000_000)
        {
            return AlexaResponse.Speak(
                $"Sorry, {mhz} megahertz is outside the radio's supported range.");
        }

        var command = $"FA{hz:D9};";
        await _catClient.SendCommandAsync(command, "Alexa", CancellationToken.None);
        _radioState.FrequencyA = hz;

        _logger.LogInformation("Alexa SetFrequencyIntent: tuned VFO A to {Hz} Hz ({Mhz} MHz)", hz, mhz);
        return AlexaResponse.Speak($"Tuned to {mhz:0.000} megahertz.");
    }

    // ── SetModeIntent ───────────────────────────────────────────────────────
    // Slot: "mode" — common ham mode names. Maps to Yaesu CAT MD codes per
    // the FTdx101MP manual.
    private static readonly Dictionary<string, (string CatCode, string SpeechName)> ModeMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "LSB",     ("1", "lower sideband") },
            { "USB",     ("2", "upper sideband") },
            { "CW",      ("3", "CW") },
            { "CWU",     ("3", "CW") },
            { "FM",      ("4", "FM") },
            { "AM",      ("5", "AM") },
            { "RTTY",    ("6", "RTTY") },
            { "CWL",     ("7", "CW lower") },
            { "DATA",    ("8", "data") },
            { "DATAL",   ("8", "data lower") },
            { "FT8",     ("8", "data") },   // FT8 runs in DATA-L mode by convention
            { "RTTYU",   ("9", "RTTY upper") },
            { "DATAFM",  ("A", "data FM") },
            { "FMN",     ("B", "FM narrow") },
            { "DATAU",   ("C", "data upper") },
            { "AMN",     ("D", "AM narrow") },
            { "PSK",     ("E", "PSK") },
        };

    private async Task<AlexaResponse> HandleSetMode(AlexaIntent intent)
    {
        var raw = intent.Slots.GetValueOrDefault("mode")?.Value ?? "";
        // Strip spaces — Alexa might say "upper sideband" but the Skill will
        // typically normalise to "USB". Defensive handling for both.
        var normalised = Regex.Replace(raw, @"\s+", "").ToUpperInvariant();

        // Handle a few spoken-out variants that Amazon might pass through verbatim.
        normalised = normalised switch
        {
            "UPPERSIDEBAND" => "USB",
            "LOWERSIDEBAND" => "LSB",
            "AMPLITUDEMODULATION" => "AM",
            "FREQUENCYMODULATION" => "FM",
            _ => normalised
        };

        if (!ModeMap.TryGetValue(normalised, out var modeInfo))
        {
            return AlexaResponse.Speak($"Sorry, I don't know the mode {raw}.");
        }

        var command = $"MD0{modeInfo.CatCode};";
        await _catClient.SendCommandAsync(command, "Alexa", CancellationToken.None);
        _radioState.ModeA = normalised;

        _logger.LogInformation("Alexa SetModeIntent: VFO A mode -> {Mode}", normalised);
        return AlexaResponse.Speak($"Mode set to {modeInfo.SpeechName}.");
    }

    // ── RigStatusIntent ─────────────────────────────────────────────────────
    // Reads RadioStateService for current frequency, mode, band, and S-meter
    // value. Composes a single spoken summary.
    private AlexaResponse HandleRigStatus()
    {
        var freqHz = _radioState.FrequencyA;
        var mhz    = freqHz / 1_000_000.0;
        var band   = _radioState.BandA ?? "unknown";
        var mode   = _radioState.ModeA ?? "unknown";
        var sRaw   = _radioState.SMeterA ?? 0;

        // Convert raw 0-255 S-meter to an S-unit label using a simplified
        // version of the calibration-tables.js SMETER_LABELS mapping.
        // Good enough for spoken status — not a precision figure.
        string sLabel = sRaw switch
        {
            <   4 => "S zero",
            <  30 => "S one",
            <  65 => "S three",
            <  95 => "S five",
            < 131 => "S seven",
            < 171 => "S nine",
            < 208 => "S nine plus twenty",
            < 240 => "S nine plus forty",
            _     => "S nine plus sixty"
        };

        var speech = $"VFO A is on {mhz:0.000} megahertz, the {band} band, mode {mode}. Signal {sLabel}.";
        _logger.LogInformation("Alexa RigStatusIntent: {Speech}", speech);
        return AlexaResponse.Speak(speech);
    }
}
