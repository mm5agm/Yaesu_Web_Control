using Microsoft.AspNetCore.Mvc;
using RadioWebControl.Core.Services.TxAlert;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Controllers;

/// <summary>
/// The high SWR / high ALC alert, for core js/txalert/tx-alert.js on every
/// page (Discussion #257). Read-only.
/// </summary>
[ApiController]
[Route("api/txalert")]
public class TxAlertController : ControllerBase
{
    private readonly TxAlertService _alerts;

    public TxAlertController(TxAlertService alerts) => _alerts = alerts;

    [HttpGet]
    public IActionResult Get()
    {
        var s = _alerts.Snapshot;
        Response.Headers.CacheControl = "no-store";
        return Ok(new { enabled = s.Enabled, run = _alerts.Run, swr = Dto(s.Swr), alc = Dto(s.Alc) });
    }

    private static object? Dto(TxAlertEvent? e) => e is null ? null : new
    {
        id = e.Id,
        active = e.Active,
        peak = Math.Round(e.Peak, 2),
        limit = e.Limit,
        at = e.At.ToString("o"),
        mode = e.Mode,
    };
}
