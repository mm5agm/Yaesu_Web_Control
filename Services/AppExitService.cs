using Microsoft.AspNetCore.SignalR;
using Yaesu_Web_Control.Hubs;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Closes YWC from a web page, the same way the tray's Exit does: tell the
    /// browser tabs first, so they show "has stopped", then stop the host. The
    /// normal shutdown path still runs, so the radio gets TX0; and the COM port
    /// is closed, free for another program such as HRD (issue #332, Mike).
    /// </summary>
    public class AppExitService
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly ILogger<AppExitService> _logger;
        private int _exiting;

        public AppExitService(IHostApplicationLifetime lifetime, IHubContext<RadioHub> hubContext, ILogger<AppExitService> logger)
        {
            _lifetime = lifetime;
            _hubContext = hubContext;
            _logger = logger;
        }

        /// <summary>
        /// Starts the shutdown and returns at once, so the request that asked
        /// for it still gets its answer. A second call while exiting does nothing.
        /// </summary>
        public void RequestExit(string who)
        {
            if (Interlocked.Exchange(ref _exiting, 1) == 1) return;
            _logger.LogInformation("[WebExit] Exit requested from {Who}", who);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _hubContext.Clients.All
                        .SendAsync("RadioStateUpdate", new { property = "ServerShutdown", value = true })
                        .WaitAsync(TimeSpan.FromMilliseconds(300));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[WebExit] ServerShutdown broadcast failed; stopping anyway");
                }
                // Time for the answer and the broadcast to reach the browsers
                // before Kestrel closes the connections.
                await Task.Delay(500);
                _logger.LogInformation("[WebExit] Calling StopApplication()");
                _lifetime.StopApplication();
            });
        }
    }
}
