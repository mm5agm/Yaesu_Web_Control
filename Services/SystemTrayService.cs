using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Yaesu_Web_Control.Hubs;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Adds a small Yaesu Web Control icon to the Windows system tray so the
    /// operator can:
    ///   - tell at a glance that YWC is running, and
    ///   - shut it down cleanly without resorting to Task Manager.
    ///
    /// The web server has no native UI of its own. Without this service the
    /// only ways to know the app is alive are seeing the browser tab work
    /// and noticing the process in Task Manager. The tray icon is the
    /// missing visual anchor.
    ///
    /// Implementation:
    /// - Creates a dedicated STA thread on startup and pumps a WinForms
    ///   message loop on it. The web host runs on its usual thread pool;
    ///   the two don't share anything except IHostApplicationLifetime,
    ///   which we use to stop the app cleanly from the tray menu.
    /// - On shutdown, posts WM_QUIT (via Application.ExitThread) to break
    ///   out of Application.Run.
    /// </summary>
    public class SystemTrayService : IHostedService, IDisposable
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<SystemTrayService> _logger;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly HttpPortInfo _portInfo;
        private Thread? _uiThread;
        private NotifyIcon? _notifyIcon;
        private ApplicationContext? _appContext;
        private SynchronizationContext? _uiSync;

        public SystemTrayService(
            IHostApplicationLifetime lifetime,
            ILogger<SystemTrayService> logger,
            IHubContext<RadioHub> hubContext,
            HttpPortInfo portInfo)
        {
            _lifetime = lifetime;
            _logger = logger;
            _hubContext = hubContext;
            _portInfo = portInfo;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // STA thread is required because NotifyIcon (and the rest of
            // WinForms) is single-threaded apartment.
            _uiThread = new Thread(UiThreadProc)
            {
                IsBackground = true,
                Name = "YWC Tray UI"
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Marshal the shutdown back onto the UI thread via its captured
            // SynchronizationContext. Application.ExitThread() must be called
            // from inside the message loop or it has no effect.
            try
            {
                _uiSync?.Post(_ =>
                {
                    try { if (_notifyIcon != null) _notifyIcon.Visible = false; } catch { }
                    Application.ExitThread();
                }, null);
            }
            catch
            {
                // Post can fail if the loop has already exited — fine.
            }
            // Give the UI thread a moment to wind down, but don't block
            // shutdown indefinitely if it's stuck.
            _uiThread?.Join(TimeSpan.FromSeconds(2));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            try { _notifyIcon?.Dispose(); } catch { }
            _notifyIcon = null;
            _appContext = null;
        }

        private void UiThreadProc()
        {
            try
            {
                // Install a WinForms sync context on this thread BEFORE
                // Application.Run does it implicitly, so we have a reliable
                // handle that StopAsync can Post to from another thread.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                _uiSync = SynchronizationContext.Current;

                Icon icon = LoadTrayIcon();

                var menu = new ContextMenuStrip();
                menu.Items.Add(new ToolStripMenuItem("Open Yaesu Web Control", null, OnOpenBrowser));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("About — version " + AppVersion.Display, null, OnAbout));
                menu.Items.Add(new ToolStripMenuItem("Open user data folder", null, OnOpenAppDataFolder));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("Exit Yaesu Web Control", null, OnExit));

                _notifyIcon = new NotifyIcon
                {
                    Icon = icon,
                    Visible = true,
                    Text = $"Yaesu Web Control v{AppVersion.Display} — running on {_portInfo.RootUrl}",
                    ContextMenuStrip = menu,
                };
                // Double-click the tray icon -> open the browser. Most
                // operators expect this as the natural shortcut.
                _notifyIcon.DoubleClick += OnOpenBrowser;

                _appContext = new ApplicationContext();
                Application.Run(_appContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tray UI thread crashed; tray icon will not be available.");
            }
            finally
            {
                try { if (_notifyIcon != null) _notifyIcon.Visible = false; } catch { }
                try { _notifyIcon?.Dispose(); } catch { }
                _notifyIcon = null;
            }
        }

        private Icon LoadTrayIcon()
        {
            // Prefer the bundled favicon.ico - it's already the app icon.
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico"),
                Path.Combine(AppContext.BaseDirectory, "favicon.ico"),
            };
            foreach (var p in candidates)
            {
                if (File.Exists(p))
                {
                    try { return new Icon(p); } catch { /* try next */ }
                }
            }
            // Fallback to the system default - guaranteed to exist.
            return SystemIcons.Application;
        }

        private void OnOpenBrowser(object? sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_portInfo.RootUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open browser from tray");
            }
        }

        private void OnAbout(object? sender, EventArgs e)
        {
            MessageBox.Show(
                $"Yaesu Web Control v{AppVersion.Display}\n" +
                $"Released {AppVersion.ReleaseDate}\n\n" +
                "Copyright Colin Campbell (MM5AGM)\n" +
                "Released under the GNU General Public License v3.0.\n\n" +
                "For full About details, open the app in your browser " +
                "and click About in the top navigation bar.",
                "About Yaesu Web Control",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void OnOpenAppDataFolder(object? sender, EventArgs e)
        {
            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MM5AGM", "Yaesu Web Control");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open user data folder from tray");
            }
        }

        private void OnExit(object? sender, EventArgs e)
        {
            var ok = MessageBox.Show(
                "Stop Yaesu Web Control?\n\nWSJT-X / Log4OM / JTAlert / GridTracker / Fldigi " +
                "will lose their CAT connection until YWC is restarted.",
                "Exit Yaesu Web Control",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (ok != DialogResult.OK) return;

            _logger.LogInformation("[TrayExit] User confirmed exit");

            // Tell any connected browser tabs we are shutting down so they can
            // replace the stale UI with a "server has stopped" overlay rather
            // than sitting there with a frozen needle. Best-effort — we wait
            // briefly for the broadcast to flush, then proceed to stop the
            // host regardless.
            try
            {
                _hubContext.Clients.All
                    .SendAsync("RadioStateUpdate", new { property = "ServerShutdown", value = true })
                    .Wait(TimeSpan.FromMilliseconds(300));
                _logger.LogInformation("[TrayExit] ServerShutdown broadcast complete");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast ServerShutdown to clients before stopping host.");
            }

            try { if (_notifyIcon != null) _notifyIcon.Visible = false; } catch { }

            // Call StopApplication from a thread-pool worker instead of the
            // WinForms STA thread. Earlier diagnostic logs showed `StopApplication`
            // blocking the STA thread for 5 seconds — a sync-over-async issue
            // where something inside the shutdown path captured the
            // WindowsFormsSynchronizationContext and was waiting to resume on
            // the STA thread that was itself blocked inside the call.
            // Off-loading to the thread pool removes that context capture and
            // lets OnExit return immediately; the host shuts down on a worker.
            //
            // Small deliberate delay before StopApplication so the SignalR
            // broadcast above has time to actually traverse the WebSocket and
            // reach the browser before Kestrel starts tearing connections down
            // — otherwise the "Yaesu Web Control has stopped" overlay never
            // gets to render. The previous 5-second STA-thread hang was
            // accidentally providing this grace window; with the hang fixed
            // (~1.2 s total exit), we need ~250 ms explicit delay to keep the
            // overlay UX.
            _logger.LogInformation("[TrayExit] Queuing _lifetime.StopApplication() on thread-pool worker");
            Task.Run(async () =>
            {
                await Task.Delay(250);
                _logger.LogInformation("[TrayExit] (worker) Calling _lifetime.StopApplication()");
                _lifetime.StopApplication();
                _logger.LogInformation("[TrayExit] (worker) _lifetime.StopApplication() returned");
            });
        }
    }
}
