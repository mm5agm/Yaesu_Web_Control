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

        // Every currently open SignalR connection, and the whole of what the
        // host means by "a browser is watching". Presence used to mean a
        // connection that had called Heartbeat(), which only the pages using
        // _Layout do -- site.js is what starts that timer. Remote Audio and
        // Radio Display set Layout = null, so neither heartbeated, and a
        // listener sitting on one of them with no other tab open had the host
        // exit underneath them 30s later. Counting the connection itself asks
        // nothing of a new page beyond connecting to this hub, which any page
        // wanting live state does anyway.
        private static readonly ConcurrentDictionary<string, byte> _connections = new();

        // Grace-period shutdown: starts when the last connection drops,
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
            CancelShutdown("browser connected");

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

            // A navigation or a closed tab arrives with no exception. A
            // connection the server gave up on -- one that sent nothing
            // within ClientTimeoutInterval -- arrives with one. On 2026-09-19
            // that distinction was the whole question and the log could not
            // answer it: an idle About page was dropped after 24 minutes and
            // the host exited 30 seconds later, with nothing recorded to say
            // which end let go first. Logged now so the next one explains
            // itself.
            if (exception is not null)
            {
                _logger.LogWarning(exception,
                    "Browser connection {ConnectionId} ended with an error rather than a clean close.",
                    Context.ConnectionId);
            }

            await base.OnDisconnectedAsync(exception);

            // Only trigger the shutdown countdown when the last connection of
            // any kind has gone — and only when the AutoShutdownWhenNoBrowsers
            // setting is enabled (default true).
            if (_connections.IsEmpty)
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

                // This fires on every page change too: the page being left
                // drops its connection a moment before the new page opens its
                // own, and the cancel below follows within a second. Worded so
                // a log reader does not take the routine case for a fault
                // (issue #143 -- it was read as the app deciding to quit).
                _logger.LogInformation(
                    "Last live browser connection dropped (page change or tab closed). Shutting down in {s}s unless one reconnects.",
                    ShutdownGrace.TotalSeconds);
                ScheduleShutdown();
            }
        }

        // Called by site.js every 5 seconds (and once immediately on connect).
        // Presence is the connection itself now, so this no longer decides
        // whether the host lives — but it is kept, and kept cancelling,
        // because a page that connects before the previous tab's
        // OnDisconnectedAsync runs would otherwise miss the cancel in
        // OnConnectedAsync and let that disconnect arm the timer.
        public Task Heartbeat()
        {
            CancelShutdown("browser heartbeat");
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
                    if (!t.IsCanceled && _connections.IsEmpty)
                    {
                        _logger.LogInformation("No clients reconnected — stopping application.");
                        _lifetime.StopApplication();
                    }
                }, TaskScheduler.Default);
            }
        }

        // Logged when there was a countdown to cancel, so the log can tell
        // "timer cancelled by a reconnect" from "timer fired and the process
        // went" -- without this a navigation and a shutdown started the same
        // way and only one of them wrote a second line.
        private void CancelShutdown(string reason)
        {
            lock (_shutdownLock)
            {
                if (_shutdownCts is not null)
                {
                    _shutdownCts.Cancel();
                    _shutdownCts.Dispose();
                    _shutdownCts = null;
                    _logger.LogInformation("Shutdown countdown cancelled ({Reason}).", reason);
                }
            }
        }

        public async Task SendInitializationStatus(string status)
        {
            await Clients.All.SendAsync("InitializationStatus", status);
        }
    }
}
