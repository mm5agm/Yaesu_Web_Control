using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yaesu_Web_Control.Models.Alexa;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Alexa;

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
    private readonly AmazonSignatureVerifier _signatureVerifier;
    private readonly ILogger<AlexaController> _logger;

    // Reused across requests to avoid per-call options allocation. Case
    // insensitivity matters because Amazon sends camelCase property names
    // ("request", "intent") and our C# models use PascalCase.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AlexaController(
        RadioStateService radioState,
        ICatClient catClient,
        ISettingsService settingsService,
        AmazonSignatureVerifier signatureVerifier,
        ILogger<AlexaController> logger)
    {
        _radioState        = radioState;
        _catClient         = catClient;
        _settingsService   = settingsService;
        _signatureVerifier = signatureVerifier;
        _logger            = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Handle()
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.AlexaEnabled)
        {
            _logger.LogWarning("Alexa request received but Settings.AlexaEnabled=false. Returning 404.");
            return NotFound();
        }

        // We need the RAW request body bytes for signature verification,
        // BEFORE model binding deserializes it. So we read the body ourselves
        // here instead of using [FromBody] on the parameter.
        string rawBody;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync();
        }

        // ── Signature verification ──────────────────────────────────────────
        if (settings.AlexaSkipSignatureVerification)
        {
            // Dev-mode bypass. The settings class makes the safety implication
            // explicit; we log loudly on every request so anyone tailing
            // production logs notices if this is left on by accident.
            _logger.LogWarning(
                "Alexa request: SIGNATURE VERIFICATION BYPASSED (AlexaSkipSignatureVerification=true). " +
                "This must NEVER be set in a production install — anyone discovering the public tunnel URL " +
                "could send arbitrary intent requests and drive the radio.");
        }
        else
        {
            // Amazon signs every request with BOTH "Signature" (RSA-SHA1, legacy)
            // and "Signature-256" (RSA-SHA256, modern) for backwards compatibility.
            // The verifier hashes with SHA-256, so we must read the matching
            // Signature-256 header — reading the legacy "Signature" header would
            // give us a SHA-1 signature that never matches a SHA-256 hash.
            var sigHeader    = Request.Headers["Signature-256"].ToString();
            var certChainUrl = Request.Headers["SignatureCertChainUrl"].ToString();
            var verification = await _signatureVerifier.VerifyAsync(rawBody, sigHeader, certChainUrl);
            if (!verification.IsValid)
            {
                // Log the precise reason for our own diagnosis, but don't
                // echo it back to the caller — a fake-request attacker can't
                // binary-search their way to a valid signature this way.
                _logger.LogWarning("Alexa signature verification failed: {Reason}", verification.FailReason);
                return BadRequest();
            }
        }

        // ── Deserialize after signature OK ──────────────────────────────────
        AlexaRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AlexaRequest>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Alexa request body is not valid JSON");
            return BadRequest();
        }

        if (request?.Request == null)
        {
            _logger.LogWarning("Alexa request body did not contain a 'request' object");
            return BadRequest();
        }

        // ── Replay protection: reject stale timestamps ──────────────────────
        // Amazon documents a 150-second window. Outside that, treat the
        // request as a replay attempt and reject. The timestamp comes from
        // the signed body, so an attacker who replays an old request can't
        // forge a fresh timestamp without breaking the signature.
        // Skipped in dev-mode bypass since manual curl tests use a fixed
        // timestamp that drifts out of the window immediately.
        if (!settings.AlexaSkipSignatureVerification)
        {
            var ageSeconds = (DateTime.UtcNow - request.Request.Timestamp.ToUniversalTime()).TotalSeconds;
            if (Math.Abs(ageSeconds) > 150)
            {
                _logger.LogWarning("Alexa request rejected: timestamp {Timestamp:O} is {Age:0}s away from now (limit 150s)",
                    request.Request.Timestamp, ageSeconds);
                return BadRequest();
            }
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
