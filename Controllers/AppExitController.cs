using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// POST api/app/exit: close YWC from the browser, as the tray's Exit does.
    /// The Exit page asks first. Refused in Docker, where the container's own
    /// restart rule would only start it again: use docker compose down there.
    /// </summary>
    [ApiController]
    [Route("api/app")]
    public class AppExitController : ControllerBase
    {
        private readonly AppExitService _exit;

        public AppExitController(AppExitService exit) => _exit = exit;

        [HttpPost("exit")]
        public IActionResult Exit()
        {
            if (HostRuntime.IsContainer)
                return Conflict(new { error = "YWC is running in Docker. Stop it with docker compose down." });
            _exit.RequestExit(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return Ok(new { exiting = true });
        }
    }
}
