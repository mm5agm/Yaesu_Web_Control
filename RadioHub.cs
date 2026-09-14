using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Hubs
{
    public class RadioHub : Hub
    {
        private readonly ILogger<RadioHub> _logger;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly RadioStateService _radioState;
        private readonly ISettingsService _settings;

        // All currently open SignalR connections
        private static readonly ConcurrentDictionary<string, byte> _connections = new();

        // Connections that have sent at least one heartbeat (i.e. the main page tab)
        private static readonly ConcurrentDictionary<string, DateTime> _heartbeats = new();

        // Grace-period shutdown: starts when all heartbeating clients disconnect,
        // cancelled if any client reconnects within the window.
        private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(30);
        private static CancellationTokenSource? _shutdownCts;
        private static readonly object _shutdownLock = new();

        public RadioHub(
            ILogger<RadioHub> logger,
            IHostApplicationLifetime lifetime,
            RadioStateService radioState,
            ISettingsService settings)
        {
            _logger   = logger;
            _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            _radioState = radioState;
            _settings = settings;
        }

        public override async Task OnConnectedAsync()
        {
            _connections.TryAdd(Context.ConnectionId, 0);
            CancelShutdown();

            // Replay the full state snapshot to this client only. Regular
            // broadcasts fire on change, so without this a browser that
            // connects after startup (second tab, another computer) keeps the
            // frontend JS defaults for everything not server-rendered in the
            // Razor page — most visibly ActiveVfo/TxVfo/SplitMode, which made
            // VFO A always appear active on late-joining clients.
            foreach (var (property, value) in _radioState.GetClientStateSnapshot())
            {
                await Clients.Caller.SendAsync("RadioStateUpdate", new { property, value });
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _connections.TryRemove(Context.ConnectionId, out _);
            bool wasHeartbeating = _heartbeats.TryRemove(Context.ConnectionId, out _);

            await base.OnDisconnectedAsync(exception);

            // Only trigger shutdown countdown when a heartbeating client (main page tab)
            // disconnects and no other heartbeating clients remain — and only when the
            // AutoShutdownWhenNoBrowsers setting is enabled (default true).
            if (wasHeartbeating && _heartbeats.IsEmpty)
            {
                var settings = await _settings.GetSettingsAsync();
                // Containers must stay up as a headless CAT controller even if
                // the user left AutoShutdown enabled in a copied settings file.
                if (!settings.AutoShutdownWhenNoBrowsers || HostRuntime.IsContainer)
                {
                    _logger.LogInformation(
                        "All browser tabs closed — auto-shutdown disabled; host keeps running.");
                    return;
                }

                _logger.LogInformation("All browser tabs closed. Shutting down in {s}s if none reconnect.",
                    ShutdownGrace.TotalSeconds);
                ScheduleShutdown();
            }
        }

        // Called by the main page every 5 seconds (and once immediately on connect).
        public Task Heartbeat()
        {
            _heartbeats[Context.ConnectionId] = DateTime.UtcNow;
            // OnConnectedAsync already cancels, but a page that connects before
            // the previous tab's OnDisconnectedAsync can miss that cancel —
            // the disconnect then schedules shutdown because this connection
            // has not heartbeated yet. Count a heartbeat as "a browser is here".
            CancelShutdown();
            return Task.CompletedTask;
        }

        // ── Shutdown helpers ──────────────────────────────────────────────────

        private void ScheduleShutdown()
        {
            lock (_shutdownLock)
            {
                _shutdownCts?.Cancel();
                _shutdownCts?.Dispose();
                _shutdownCts = new CancellationTokenSource();
                var token = _shutdownCts.Token;

                Task.Delay(ShutdownGrace, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled && _heartbeats.IsEmpty)
                    {
                        _logger.LogInformation("No clients reconnected — stopping application.");
                        _lifetime.StopApplication();
                    }
                }, TaskScheduler.Default);
            }
        }

        private static void CancelShutdown()
        {
            lock (_shutdownLock)
            {
                if (_shutdownCts is not null)
                {
                    _shutdownCts.Cancel();
                    _shutdownCts.Dispose();
                    _shutdownCts = null;
                }
            }
        }

        public async Task SendInitializationStatus(string status)
        {
            await Clients.All.SendAsync("InitializationStatus", status);
        }
    }
}
