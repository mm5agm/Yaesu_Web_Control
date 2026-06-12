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

    public CalibrationController(ICalibrationService service, IHubContext<RadioHub> hub)
    {
        _service = service;
        _hub     = hub;
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
}
