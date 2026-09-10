using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Controllers;

/// <summary>
/// Drives the radio's OWN spectrum scope over CAT (the SS command).
///
/// This is not YWC's SDR spectrum panel — that is separate hardware fed by an
/// RSP1 on the rear-panel IF output, and nothing here touches it. What these
/// endpoints change is what the operator sees on the radio's front-panel TFT,
/// which is useful when operating remotely, and useful again when that TFT is
/// being captured to the browser as video. See
/// docs/design/scope-control-via-cat.md for why this beats drawing clickable
/// overlays on top of the captured picture.
///
/// Deliberately a separate controller rather than more endpoints bolted onto
/// CatController (which is already 3000 lines): this feature is new, is gated
/// per model, and may not survive contact with users. Keeping it in one file
/// means it can be judged — or deleted — on its own.
///
/// Every write is followed by a read-back, and the read-back is what the API
/// returns. The radio, not our optimism, is the source of truth for what the
/// scope is now showing; that matters here more than usual because the operator
/// may be sitting in front of the radio changing the same settings by hand.
/// </summary>
[ApiController]
[Route("api/scope")]
public class ScopeController : ControllerBase
{
    private readonly ICatClient _catClient;
    private readonly ISettingsService _settingsService;
    private readonly RadioStateService _radioState;
    private readonly ILogger<ScopeController> _logger;

    // SS frames are short and the radio answers them promptly, but they share
    // the port with the meter poll. One at a time, same as CatController.
    private static readonly SemaphoreSlim _gate = new(1, 1);

    // How long to let the radio redraw before reading a setting back after a
    // write. Measured on an FTdx101MP: without it, roughly one read in three
    // following a mode change goes unanswered.
    private const int SettleAfterWriteMs = 150;

    // Gap before a retried read. Deliberately shorter than the settle delay —
    // by this point the radio has already had the settle window.
    private const int RetryDelayMs = 80;

    public ScopeController(
        ICatClient catClient,
        ISettingsService settingsService,
        RadioStateService radioState,
        ILogger<ScopeController> logger)
    {
        _catClient       = catClient;
        _settingsService = settingsService;
        _radioState      = radioState;
        _logger          = logger;
    }

    // ── Endpoints ────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/scope/main | /api/scope/sub
    /// Reads the scope settings the UI exposes, straight from the radio.
    /// </summary>
    [HttpGet("{band}")]
    public async Task<IActionResult> GetState(string band)
    {
        if (!TryBand(band, out var p1, out var bandError))
            return BadRequest(new { error = bandError });

        if (!Supported(out var model))
            return NotSupported(model);

        if (!await _gate.WaitAsync(2000))
            return StatusCode(503, new { error = "Radio busy" });
        try
        {
            await EnsureConnectedAsync();
            // Logged at Information alongside the writes. A read changes nothing
            // on the radio, so it is invisible from the operating position — and
            // when someone reports "pressing the buttons does nothing", the first
            // question is whether the request arrived at all. Without this line a
            // scope panel whose JS never wired up and one that is working
            // normally produce byte-identical logs.
            _logger.LogInformation("Scope {Band} read", band);
            return Ok(await ReadStateAsync(p1, model));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading scope state for {Band}", band);
            return StatusCode(500, new { error = "Failed to read scope state" });
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// POST /api/scope/{main|sub}/{setting}   body: { "value": "..." }
    ///
    /// setting is one of: span, mode, hold, marker, peak, speed, level, affft.
    /// Returns the re-read state, so the caller can see what the radio actually
    /// did rather than what it was asked to do.
    ///
    /// Route token is {setting}, NOT {action}: "action" is a reserved ASP.NET
    /// Core routing token and an attribute route using it silently fails to
    /// register, 404ing every request. That already cost a debugging session
    /// once on the QMB endpoints — see CatController.Qmb.
    /// </summary>
    [HttpPost("{band}/{setting}")]
    public async Task<IActionResult> Set(string band, string setting, [FromBody] ScopeSetRequest? request)
    {
        if (!TryBand(band, out var p1, out var bandError))
            return BadRequest(new { error = bandError });

        if (!Supported(out var model))
            return NotSupported(model);

        var value = request?.Value?.Trim() ?? "";
        if (value.Length == 0)
            return BadRequest(new { error = "value is required" });

        string frame;
        switch (setting.ToLowerInvariant())
        {
            case "span":
                if (!TryDigit(value, 0, 9, out var span))
                    return BadRequest(new { error = "span must be 0-9 (0 = 1 kHz … 9 = 1 MHz)" });
                frame = ScopeCommands.Set(p1, ScopeCommands.Span, span);
                break;

            case "mode":
                // The UI sends the composed P3 character rather than the three
                // axes, so the composition rule lives in exactly one place
                // (ScopeCommands.ModeValue, which the browser mirrors). Validate
                // the size against the model here anyway: the FT-710 has no
                // third size, and sending a value its manual prints as "-" is a
                // guess, not a feature.
                if (value.Length != 1 || !IsModeChar(value[0]))
                    return BadRequest(new { error = "mode must be a single character 0-9 or A-B" });
                var (is3dss, _, size) = ScopeCommands.ParseMode(value[0]);
                var sizes = RadioCapabilities.ScopeSizeLabels(model);
                if (!is3dss && size >= sizes.Length)
                    return BadRequest(new
                    {
                        error = $"{model} has {sizes.Length} scope size options ({string.Join(" / ", sizes)}); size index {size} does not exist on this radio"
                    });
                frame = ScopeCommands.Set(p1, ScopeCommands.Mode, char.ToUpperInvariant(value[0]));
                break;

            case "hold":
                if (!RadioCapabilities.SupportsScopeHold(model))
                    return BadRequest(new { error = $"{model} has no scope HOLD — its SS sub-commands stop at 7" });
                if (!TryDigit(value, 0, 1, out var hold))
                    return BadRequest(new { error = "hold must be 0 (off) or 1 (on)" });
                frame = ScopeCommands.Set(p1, ScopeCommands.Hold, hold);
                break;

            case "marker":
                if (!TryDigit(value, 0, 1, out var marker))
                    return BadRequest(new { error = "marker must be 0 (off) or 1 (on)" });
                frame = ScopeCommands.Set(p1, ScopeCommands.Marker, marker);
                break;

            case "peak":
                if (!TryDigit(value, 0, 4, out var peak))
                    return BadRequest(new { error = "peak must be 0-4 (LV1-LV5)" });
                frame = ScopeCommands.Set(p1, ScopeCommands.Peak, peak);
                break;

            case "color":
                // Three axes packed into one SS P2=3 field. Writing any one of
                // them with a single-character Set() would zero the other two.
                if (!ScopeCommands.TryParseColorRequest(value, out var color, out var nbColor, out var nbOn,
                        out var hasColor, out var hasNbColor, out var hasNbOn))
                    return BadRequest(new { error = "color must be 1 character (0-9/A), 3 characters, n0-n6 (NB colour), or o0/o1 (NB on)" });
                if (!await _gate.WaitAsync(2000))
                    return StatusCode(503, new { error = "Radio busy" });
                try
                {
                    await EnsureConnectedAsync();
                    var current = await ReadFieldAsync(p1, ScopeCommands.Color);
                    var (curColor, curNb, curOn) = ScopeCommands.ParseColor(current);
                    if (!hasColor)  color   = curColor;
                    if (!hasNbColor) nbColor = curNb;
                    if (!hasNbOn)    nbOn    = curOn;
                    frame = ScopeCommands.SetColor(p1, color, nbColor, nbOn);
                    await _catClient.SendCommandAsync(frame, "WebUI", CancellationToken.None);
                    _logger.LogInformation("Scope {Band} color -> {Frame}", band, frame);
                    await Task.Delay(SettleAfterWriteMs);
                    return Ok(await ReadStateAsync(p1, model));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error setting scope color on {Band}", band);
                    return StatusCode(500, new { error = "Failed to set scope color" });
                }
                finally { _gate.Release(); }

            case "speed":
                // The FT-710 adds a sixth setting, STOP (5); the others stop at
                // FAST3 (4). Labels are the source of truth so a new radio
                // that documents a different list does not need a second if.
                var maxSpeed = Math.Max(0, RadioCapabilities.ScopeSpeedLabels(model).Length - 1);
                if (!TryDigit(value, 0, maxSpeed, out var speed))
                    return BadRequest(new { error = $"speed must be 0-{maxSpeed} for {model}" });
                frame = ScopeCommands.Set(p1, ScopeCommands.Speed, speed);
                break;

            case "level":
                if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var db))
                    return BadRequest(new { error = "level must be a number of dB, -30.0 to +30.0" });
                frame = ScopeCommands.SetLevel(p1, db);
                break;

            case "affft":
                // Three axes packed into one SS P2=7 field. Writing any one of
                // them with a single-character Set() would zero the other two,
                // so we read the current field, overlay the digits the caller
                // sent, and write the triple back.
                if (!RadioCapabilities.SupportsScopeAfFft(model))
                    return BadRequest(new { error = $"{model} has no AF-FFT / oscilloscope CAT control" });
                if (!ScopeCommands.TryParseAfFftRequest(value, out var fftAtt, out var oscAtt, out var oscTime,
                        out var hasFftAtt, out var hasOscAtt, out var hasOscTime))
                    return BadRequest(new { error = "affft must be 1 digit (FFT ATT), 3 digits, a0-a2 (OSC ATT), or t0-t5 (OSC time)" });
                if (!await _gate.WaitAsync(2000))
                    return StatusCode(503, new { error = "Radio busy" });
                try
                {
                    await EnsureConnectedAsync();
                    var current = await ReadFieldAsync(p1, ScopeCommands.AfFft);
                    var (curFft, curOsc, curTime) = ScopeCommands.ParseAfFft(current);
                    if (!hasFftAtt)  fftAtt  = curFft;
                    if (!hasOscAtt)  oscAtt  = curOsc;
                    if (!hasOscTime) oscTime = curTime;
                    frame = ScopeCommands.SetAfFft(p1, fftAtt, oscAtt, oscTime);
                    await _catClient.SendCommandAsync(frame, "WebUI", CancellationToken.None);
                    _logger.LogInformation("Scope {Band} affft -> {Frame}", band, frame);
                    await Task.Delay(SettleAfterWriteMs);
                    return Ok(await ReadStateAsync(p1, model));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error setting scope affft on {Band}", band);
                    return StatusCode(500, new { error = "Failed to set scope affft" });
                }
                finally { _gate.Release(); }

            default:
                return BadRequest(new
                {
                    error = $"Unknown scope setting '{setting}'. Expected span, mode, hold, marker, peak, color, speed, level or affft."
                });
        }

        if (!await _gate.WaitAsync(2000))
            return StatusCode(503, new { error = "Radio busy" });
        try
        {
            await EnsureConnectedAsync();
            await _catClient.SendCommandAsync(frame, "WebUI", CancellationToken.None);
            _logger.LogInformation("Scope {Band} {Setting} -> {Frame}", band, setting, frame);

            // Let the radio finish redrawing before reading back. A mode change
            // rebuilds the whole scope display, and reads issued into that
            // window are simply not answered — measured at roughly one in three
            // on an FTdx101MP with the meter poll running. ReadValueAsync
            // retries once on top of this, but the delay is what stops most of
            // them happening at all.
            await Task.Delay(SettleAfterWriteMs);

            var state = await ReadStateAsync(p1, model);
            return Ok(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting scope {Setting} on {Band}", setting, band);
            return StatusCode(500, new { error = $"Failed to set scope {setting}" });
        }
        finally { _gate.Release(); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the sub-commands the UI shows. HOLD is skipped entirely on models
    /// without it rather than read and discarded — an unanswered read costs a
    /// timeout on a port shared with the meter poll.
    /// </summary>
    private async Task<object> ReadStateAsync(char p1, string model)
    {
        var span   = await ReadValueAsync(p1, ScopeCommands.Span);
        var mode   = await ReadValueAsync(p1, ScopeCommands.Mode);
        var marker = await ReadValueAsync(p1, ScopeCommands.Marker);
        var peak   = await ReadValueAsync(p1, ScopeCommands.Peak);
        var speed  = await ReadValueAsync(p1, ScopeCommands.Speed);
        var level  = await ReadFieldAsync(p1, ScopeCommands.Level);

        char? color = null, nbColor = null, nbOn = null;
        var colorField = await ReadFieldAsync(p1, ScopeCommands.Color);
        if (colorField is not null)
        {
            var parsedColor = ScopeCommands.ParseColor(colorField);
            color   = parsedColor.Color;
            nbColor = parsedColor.NbColor;
            nbOn    = parsedColor.NbOn;
        }

        char? hold = null;
        if (RadioCapabilities.SupportsScopeHold(model))
            hold = await ReadValueAsync(p1, ScopeCommands.Hold);

        string? fftAtt = null, oscAtt = null, oscTime = null;
        if (RadioCapabilities.SupportsScopeAfFft(model))
        {
            var affft = await ReadFieldAsync(p1, ScopeCommands.AfFft);
            var parsed = ScopeCommands.ParseAfFft(affft);
            if (affft is not null)
            {
                fftAtt  = parsed.FftAtt.ToString();
                oscAtt  = parsed.OscAtt.ToString();
                oscTime = parsed.OscTime.ToString();
            }
        }

        var (is3dss, placement, size) = mode is { } m
            ? ScopeCommands.ParseMode(m)
            : (false, 0, 0);

        return new
        {
            band      = p1 == '1' ? "sub" : "main",
            span      = span?.ToString(),
            mode      = mode?.ToString(),
            is3dss,
            placement,
            size,
            hold      = hold?.ToString(),
            marker    = marker?.ToString(),
            peak      = peak?.ToString(),
            color     = color?.ToString(),
            nbColor   = nbColor?.ToString(),
            nbOn      = nbOn?.ToString(),
            speed     = speed?.ToString(),
            fftAtt,
            oscAtt,
            oscTime,
            // Sent as the raw five-character field ("+05.0") — the browser
            // displays it as-is, so there is no float round-trip to disagree
            // about.
            level,
        };
    }

    private async Task<char?> ReadValueAsync(char p1, char subCommand) =>
        (await ReadFieldAsync(p1, subCommand)) is { Length: > 0 } f ? f[0] : null;

    /// <summary>
    /// Reads one SS sub-command, retrying once if the radio does not answer.
    ///
    /// Unanswered reads are real and expected rather than a fault: the radio
    /// stops answering while it redraws its scope, so the read that follows a
    /// mode change is the one that gets dropped. One retry clears it. Anything
    /// still unanswered after that is reported as null and shown as "unknown"
    /// rather than guessed at — a scope control that displays a stale value as
    /// though it were current is worse than one that admits it does not know.
    /// </summary>
    private async Task<string?> ReadFieldAsync(char p1, char subCommand)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(RetryDelayMs);

            var answer = await _catClient.SendCommandAsync(
                ScopeCommands.Read(p1, subCommand), "WebUI", CancellationToken.None);

            var field = ScopeCommands.ValueField(answer, p1, subCommand);
            if (field is not null) return field;
        }

        _logger.LogDebug("Scope read SS{P1}{P2} unanswered after retry", p1, subCommand);
        return null;
    }

    private async Task EnsureConnectedAsync()
    {
        if (_catClient.IsConnected) return;
        var settings = await _settingsService.GetSettingsAsync();
        await _catClient.ConnectAsync(settings.SerialPort, settings.BaudRate);
    }

    private bool Supported(out string model)
    {
        model = _radioState.RadioModel ?? "";
        return RadioCapabilities.SupportsSpectrumScopeCat(model);
    }

    private IActionResult NotSupported(string model) => BadRequest(new
    {
        error = string.IsNullOrEmpty(model)
            ? "No radio model is configured, so scope control is unavailable."
            : $"{model} cannot have its scope driven over CAT."
    });

    /// <summary>
    /// Maps the route's band segment to SS P1. Accepts A/B as well as main/sub
    /// because the rest of YWC's API speaks in VFO letters; the scope command
    /// speaks in MAIN/SUB, and they are the same two things.
    ///
    /// On single-receiver radios P1 is documented as "0: (Fixed)", so "sub" is
    /// rejected rather than quietly rewritten to MAIN — a UI asking for a SUB
    /// scope on a radio that has one receiver is a bug worth hearing about.
    /// </summary>
    private bool TryBand(string band, out char p1, out string error)
    {
        p1 = '0';
        error = "";
        var b = band.ToLowerInvariant();
        var wantsSub = b is "sub" or "b";
        if (!wantsSub && b is not ("main" or "a"))
        {
            error = "band must be 'main' or 'sub' (or 'a' / 'b')";
            return false;
        }

        if (wantsSub && !RadioCapabilities.HasPerReceiverScopes(_radioState.RadioModel ?? ""))
        {
            error = $"{_radioState.RadioModel} has a single scope; SS P1 is fixed at 0 on this radio";
            return false;
        }

        p1 = ScopeCommands.BandDigit(wantsSub);
        return true;
    }

    private static bool TryDigit(string value, int min, int max, out char digit)
    {
        digit = '0';
        if (value.Length != 1 || value[0] < '0' || value[0] > '9') return false;
        var n = value[0] - '0';
        if (n < min || n > max) return false;
        digit = value[0];
        return true;
    }

    private static bool IsModeChar(char c) =>
        (c >= '0' && c <= '9') || (c is >= 'A' and <= 'B') || (c is >= 'a' and <= 'b');
}

/// <summary>Request body for a scope setting change.</summary>
public sealed class ScopeSetRequest
{
    public string? Value { get; set; }
}
