using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Models.Calibration;

[ApiController]
[Route("api/calibration")]
public class CalibrationController : ControllerBase
{
    private readonly ICalibrationService _service;
    private readonly IHubContext<RadioHub> _hub;
    private readonly ILogger<CalibrationController> _log;

    public CalibrationController(ICalibrationService service, IHubContext<RadioHub> hub, ILogger<CalibrationController> log)
    {
        _service = service;
        _hub     = hub;
        _log     = log;
    }

    // Broadcast CalibrationUpdated to every connected client so all open
    // browser tabs reload their in-memory calibration tables. Without this
    // the calibration save would only refresh on a full page reload —
    // confusing because the user's saved values appeared to be ignored.
    // (#29 follow-up — Jacek SP3L, 2026-06-11.)
    private async Task BroadcastCalibrationUpdated()
    {
        try
        {
            await _hub.Clients.All.SendAsync(
                "RadioStateUpdate",
                new { property = "CalibrationUpdated", value = DateTime.UtcNow.ToString("O") });
        }
        catch { /* best-effort */ }
    }

    [HttpGet("all")]
    public IActionResult GetAll()
    {
        var all = _service.GetAllCalibrationTables();
        return Ok(all);
    }

    // Re-reads the calibration file from disk before answering, so this really
    // is "from file" — both on page load and behind the page's "Reload From
    // File" button. It used to return the in-memory copy loaded at startup,
    // which meant an edit made by another YWC instance, a hand edit, or the dev
    // import was invisible until a restart, and "Reload From File" quietly
    // re-showed what was already on screen.
    [HttpGet("file")]
    public IActionResult GetCalibrationFile()
    {
        var reloaded = _service.Reload();
        return Ok(new
        {
            calibration = _service.Current,
            saveTargetPath = _service.GetSavePath(),
            mode = _service.IsDevelopmentMode ? "development" : "user",
            // false = the file couldn't be read; the values below are the last
            // good ones from memory, so the page can say so rather than imply
            // it is showing what is on disk.
            reloaded
        });
    }

    [HttpPost("file")]
    public async Task<IActionResult> SaveCalibrationFile([FromBody] CalibrationFile file)
    {
        if (file == null)
        {
            return BadRequest(new { error = "Calibration file payload is required." });
        }

        _service.Save(file);
        _log.LogInformation("[cal-save] wrote {Count} meters to {Path}",
            file.Meters?.Count ?? 0, _service.GetSavePath());
        await BroadcastCalibrationUpdated();
        return Ok(new
        {
            ok = true,
            saveTargetPath = _service.GetSavePath(),
            mode = _service.IsDevelopmentMode ? "development" : "user"
        });
    }

    /// <summary>
    /// Reset the user's calibration file to the model-specific defaults
    /// shipped with the app. Used when the user has changed radio model in
    /// Settings (the calibration table that came with the previous model is
    /// no longer right for the new one) or when they want to wipe their own
    /// tweaks and start over.
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> ResetCalibration()
    {
        _service.ResetToDefault();
        await BroadcastCalibrationUpdated();
        return Ok(new
        {
            ok = true,
            calibration = _service.Current,
            saveTargetPath = _service.GetSavePath(),
        });
    }

    // Developer-only: fold a user-emailed calibration (pasted / read from the
    // clipboard) into the SHIPPED default file for its radio, editing only the
    // values that changed. Exposed just so Colin can do it with a button instead
    // of a hand edit. Gated to the development build — a normal installed YWC is
    // Production, so users never see or reach this.
    //
    // `From` is a CALLSIGN and nothing else — it is written into
    // calibration-contributions/<Model>.json, which is committed to git. No
    // email addresses, real names or locations. See
    // docs/design/calibration-contributions-port-from-iwc.md.
    public class ImportDefaultRequest
    {
        public string? Text { get; set; }
        public string? From { get; set; }
        public string? Note { get; set; }
    }

    // Developer-only: promote THIS instance's saved calibration into the shipped
    // default for the configured radio, with no clipboard in the loop.
    [HttpPost("import-default/current")]
    public IActionResult ImportDefaultFromCurrent([FromBody] ImportDefaultRequest? request = null)
    {
        if (!_service.IsDevelopmentMode)
        {
            _log.LogWarning("[cal-import] rejected: not development mode");
            return NotFound();
        }

        try
        {
            var result = _service.ImportCurrentCalibrationIntoDefault(
                new ContributionMeta(request?.From, request?.Note));
            _log.LogInformation("[cal-import/current] result: ok={Ok} changed={Changed} model={Model} updated=[{Updated}] msg={Msg}",
                result.Ok, result.Changed, result.Model, string.Join(",", result.Updated), result.Message);
            _log.LogInformation("[cal-import/current] incoming values: {Incoming}", result.IncomingSummary);
            return result.Ok ? Ok(result) : BadRequest(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[cal-import/current] threw");
            return BadRequest(new CalibrationImportResult { Ok = false, Message = "Server error: " + ex.Message });
        }
    }

    [HttpPost("import-default")]
    public IActionResult ImportDefault([FromBody] ImportDefaultRequest? request)
    {
        _log.LogInformation("[cal-import] request received: dev={Dev}, textLen={Len}",
            _service.IsDevelopmentMode, request?.Text?.Length ?? 0);

        if (!_service.IsDevelopmentMode)
        {
            _log.LogWarning("[cal-import] rejected: not development mode");
            return NotFound();   // hidden entirely outside the dev build
        }

        try
        {
            var result = _service.ImportEmailedCalibrationIntoDefault(
                request?.Text, new ContributionMeta(request?.From, request?.Note));
            _log.LogInformation("[cal-import] result: ok={Ok} changed={Changed} model={Model} updated=[{Updated}] msg={Msg}",
                result.Ok, result.Changed, result.Model, string.Join(",", result.Updated), result.Message);
            // Compare this against the [cal] saved/reloaded line: if they differ,
            // the clipboard was stale, not the import.
            _log.LogInformation("[cal-import] incoming values: {Incoming}", result.IncomingSummary);
            return result.Ok ? Ok(result) : BadRequest(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[cal-import] threw");
            return BadRequest(new CalibrationImportResult { Ok = false, Message = "Server error: " + ex.Message });
        }
    }

    // Developer-only: re-derive the shipped default from the contributions
    // already on disk, adding nothing. This is the undo for a bad contribution
    // — mark it "excluded": true in calibration-contributions/<Model>.json, POST
    // here, and the shipped numbers fall back to the median of what remains.
    [HttpPost("contributions/recompute")]
    public IActionResult RecomputeFromContributions([FromBody] RecomputeRequest? request = null)
    {
        if (!_service.IsDevelopmentMode)
        {
            _log.LogWarning("[cal-recompute] rejected: not development mode");
            return NotFound();
        }

        try
        {
            var result = _service.RecomputeDefaultFromContributions(request?.Model);
            _log.LogInformation("[cal-recompute] result: ok={Ok} changed={Changed} model={Model} " +
                                "contributors={N} updated=[{Updated}] refused=[{Refused}] msg={Msg}",
                result.Ok, result.Changed, result.Model, result.Contributors,
                string.Join(",", result.Updated), string.Join("; ", result.Refused), result.Message);
            return result.Ok ? Ok(result) : BadRequest(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[cal-recompute] threw");
            return BadRequest(new CalibrationImportResult { Ok = false, Message = "Server error: " + ex.Message });
        }
    }

    // Null/blank Model means "the radio currently selected in Settings".
    public class RecomputeRequest { public string? Model { get; set; } }
}
