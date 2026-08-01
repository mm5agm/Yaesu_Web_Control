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

        // All currently open SignalR connections
        private static readonly ConcurrentDictionary<string, byte> _connections = new();

        // Connections that have sent at least one heartbeat (i.e. the main page tab)
        private static readonly ConcurrentDictionary<string, DateTime> _heartbeats = new();

        // Grace-period shutdown: starts when all heartbeating clients disconnect,
        // cancelled if any client reconnects within the window.
        private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(30);
        private static CancellationTokenSource? _shutdownCts;
        private static readonly object _shutdownLock = new();

        public RadioHub(ILogger<RadioHub> logger, IHostApplicationLifetime lifetime, RadioStateService radioState)
        {
            _logger   = logger;
            _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            _radioState = radioState;
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
            // disconnects and no other heartbeating clients remain.
            if (wasHeartbeating && _heartbeats.IsEmpty)
            {
                _logger.LogInformation("All browser tabs closed. Shutting down in {s}s if none reconnect.",
                    ShutdownGrace.TotalSeconds);
                ScheduleShutdown();
            }
        }

        // Called by the main page every 5 seconds
        public Task Heartbeat()
        {
            _heartbeats[Context.ConnectionId] = DateTime.UtcNow;
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
