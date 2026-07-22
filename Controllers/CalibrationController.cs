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

    [HttpGet("file")]
    public IActionResult GetCalibrationFile()
    {
        return Ok(new
        {
            calibration = _service.Current,
            saveTargetPath = _service.GetSavePath(),
            mode = _service.IsDevelopmentMode ? "development" : "user"
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
    // of scripts/merge-calibration.py. Gated to the development build — a normal
    // installed YWC is Production, so users never see or reach this.
    public class ImportDefaultRequest { public string? Text { get; set; } }

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
            var result = _service.ImportEmailedCalibrationIntoDefault(request?.Text);
            _log.LogInformation("[cal-import] result: ok={Ok} changed={Changed} model={Model} updated=[{Updated}] msg={Msg}",
                result.Ok, result.Changed, result.Model, string.Join(",", result.Updated), result.Message);
            return result.Ok ? Ok(result) : BadRequest(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[cal-import] threw");
            return BadRequest(new CalibrationImportResult { Ok = false, Message = "Server error: " + ex.Message });
        }
    }
}
