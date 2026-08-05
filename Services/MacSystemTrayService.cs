// macOS menu-bar status item for the CAT-only (net10.0) host.
// Mirrors Services/SystemTrayService.cs (Windows WinForms NotifyIcon):
//   Open browser · About · Open user data folder · Exit
//
// Uses Avalonia's TrayIcon + NativeMenu. AppKit requires the *main* thread, so
// Program.cs starts Kestrel with StartAsync() and then calls RunBlocking() on
// the process main thread (ShowInDock=false keeps this a menu-bar agent).

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Yaesu_Web_Control.Hubs;

namespace Yaesu_Web_Control.Services;

public sealed class MacSystemTrayService : IDisposable
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MacSystemTrayService> _logger;
    private readonly IHubContext<RadioHub> _hubContext;
    private readonly HttpPortInfo _portInfo;

    private CancellationTokenSource? _uiLoopCts;
    private TrayIcon? _trayIcon;

    public MacSystemTrayService(
        IHostApplicationLifetime lifetime,
        ILogger<MacSystemTrayService> logger,
        IHubContext<RadioHub> hubContext,
        HttpPortInfo portInfo)
    {
        _lifetime = lifetime;
        _logger = logger;
        _hubContext = hubContext;
        _portInfo = portInfo;
    }

    /// <summary>
    /// Blocks the calling thread (must be the process main thread on macOS)
    /// with Avalonia's dispatcher until <paramref name="stoppingToken"/> fires
    /// or the user chooses Exit from the menu.
    /// </summary>
    public void RunBlocking(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            _logger.LogDebug("MacSystemTrayService.RunBlocking skipped — not macOS");
            stoppingToken.WaitHandle.WaitOne();
            return;
        }

        _uiLoopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            AppBuilder.Configure<Application>()
                .UsePlatformDetect()
                .With(new MacOSPlatformOptions
                {
                    ShowInDock = false,
                    DisableDefaultApplicationMenuItems = true,
                })
                .SetupWithoutStarting();

            var menu = new NativeMenu();

            var openItem = new NativeMenuItem("Open Yaesu Web Control");
            openItem.Click += (_, _) => OpenBrowser();
            menu.Add(openItem);

            menu.Add(new NativeMenuItemSeparator());

            var aboutItem = new NativeMenuItem("About — version " + AppVersion.Current);
            aboutItem.Click += (_, _) => OpenAbout();
            menu.Add(aboutItem);

            var dataItem = new NativeMenuItem("Open user data folder");
            dataItem.Click += (_, _) => OpenAppDataFolder();
            menu.Add(dataItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("Exit Yaesu Web Control");
            exitItem.Click += (_, _) => OnExit();
            menu.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                ToolTipText = $"Yaesu Web Control v{AppVersion.Current} — {_portInfo.RootUrl}",
                Icon = LoadTrayIcon(),
                Menu = menu,
                IsVisible = true,
            };
            _trayIcon.Command = new SimpleCommand(OpenBrowser);

            _logger.LogInformation("macOS menu-bar status item ready ({Url})", _portInfo.RootUrl);

            Dispatcher.UIThread.MainLoop(_uiLoopCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mac tray UI crashed; menu-bar icon will not be available.");
            // Keep the host alive without a tray — wait until stop is requested.
            try { stoppingToken.WaitHandle.WaitOne(); } catch { }
        }
        finally
        {
            TearDownTray();
        }
    }

    public void Dispose()
    {
        try { _uiLoopCts?.Cancel(); } catch { }
        try { TearDownTray(); } catch { }
        try { _uiLoopCts?.Dispose(); } catch { }
        _uiLoopCts = null;
    }

    private void TearDownTray()
    {
        if (_trayIcon is null) return;
        try { _trayIcon.IsVisible = false; } catch { }
        try { _trayIcon.Dispose(); } catch { }
        _trayIcon = null;
    }

    private static WindowIcon LoadTrayIcon()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico"),
            Path.Combine(AppContext.BaseDirectory, "favicon.ico"),
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                return new WindowIcon(stream);
            }
            catch { /* try next */ }
        }

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO5WNloAAAAASUVORK5CYII=");
        return new WindowIcon(new MemoryStream(png));
    }

    private void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_portInfo.RootUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open browser from macOS tray");
        }
    }

    private void OpenAbout()
    {
        try
        {
            var url = _portInfo.RootUrl.TrimEnd('/') + "/About";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open About from macOS tray");
        }
    }

    private void OpenAppDataFolder()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MM5AGM", "Yaesu Web Control");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { folder },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open user data folder from macOS tray");
        }
    }

    private void OnExit()
    {
        if (!ConfirmExitWithOsascript())
            return;

        _logger.LogInformation("[MacTrayExit] User confirmed exit");

        try
        {
            _hubContext.Clients.All
                .SendAsync("RadioStateUpdate", new { property = "ServerShutdown", value = true })
                .Wait(TimeSpan.FromMilliseconds(300));
            _logger.LogInformation("[MacTrayExit] ServerShutdown broadcast complete");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast ServerShutdown before stopping host.");
        }

        try { if (_trayIcon != null) _trayIcon.IsVisible = false; } catch { }

        _logger.LogInformation("[MacTrayExit] Queuing StopApplication on thread-pool worker");
        Task.Run(async () =>
        {
            await Task.Delay(250);
            _logger.LogInformation("[MacTrayExit] (worker) Calling StopApplication()");
            _lifetime.StopApplication();
        });
    }

    private static bool ConfirmExitWithOsascript()
    {
        try
        {
            var script =
                "display dialog \"Stop Yaesu Web Control?\\n\\n" +
                "WSJT-X / Log4OM / JTAlert / GridTracker / Fldigi will lose their CAT connection until YWC is restarted.\" " +
                "buttons {\"Cancel\", \"OK\"} default button \"OK\" with icon caution " +
                "with title \"Exit Yaesu Web Control\"";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", script },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(15_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return true;
        }
    }

    private sealed class SimpleCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public SimpleCommand(Action action) => _action = action;
#pragma warning disable CS0067
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }
}
