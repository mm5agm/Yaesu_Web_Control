using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api")]
    public class ExternalAppsController : ControllerBase
    {
        private readonly ISettingsService _settingsService;
        // Logger removed as part of cleanup
        private readonly ProcessStatusCacheService _processStatusCache;

        public ExternalAppsController(ISettingsService settingsService, ILogger<ExternalAppsController> logger, ProcessStatusCacheService processStatusCache)
        {
            _settingsService = settingsService;
            // Logger removed
            _processStatusCache = processStatusCache;
        }

        [HttpPost("jtalert/launch")]
        public async Task<IActionResult> LaunchJtalert()
        {
            return await LaunchExternalApp("JTAlert", "JTAlertV2", async () =>
            {
                var settings = await _settingsService.GetSettingsAsync();
                return settings.JtalertCommandLine;
            });
        }

        [HttpGet("jtalert/status")]
        public IActionResult JtalertStatus()
        {
            // Check for JTAlertV2 process (main JTAlert process) - uses cached status
            var running = _processStatusCache.IsProcessRunning("JTAlertV2");
            return Ok(new { running });
        }

        [HttpPost("log4om/launch")]
        public async Task<IActionResult> LaunchLog4om()
        {
            return await LaunchExternalApp("Log4OM", "Log4OM", async () =>
            {
                var settings = await _settingsService.GetSettingsAsync();
                return settings.Log4omCommandLine;
            });
        }

        [HttpGet("log4om/status")]
        public IActionResult Log4omStatus()
        {
            // Check for L4ONG process (Log4OM Next Generation) - uses cached status
            var running = _processStatusCache.IsProcessRunning("L4ONG");
            return Ok(new { running });
        }

        [HttpPost("gridtracker/launch")]
        public async Task<IActionResult> LaunchGridtracker()
        {
            return await LaunchExternalApp("GridTracker", "GridTracker2", async () =>
            {
                var settings = await _settingsService.GetSettingsAsync();
                return settings.GridtrackerCommandLine;
            });
        }

        [HttpGet("gridtracker/status")]
        public IActionResult GridtrackerStatus()
        {
            var running = _processStatusCache.IsProcessRunning("GridTracker2");
            return Ok(new { running });
        }

        [HttpPost("fldigi/launch")]
        public async Task<IActionResult> LaunchFldigi()
        {
            return await LaunchExternalApp("Fldigi", "fldigi", async () =>
            {
                var settings = await _settingsService.GetSettingsAsync();
                return settings.FldigiCommandLine;
            });
        }

        [HttpGet("fldigi/status")]
        public IActionResult FldigiStatus()
        {
            var running = _processStatusCache.IsProcessRunning("fldigi");
            return Ok(new { running });
        }

        private async Task<IActionResult> LaunchExternalApp(string appName, string processName, Func<Task<string>> getCommandLine)
        {
            // Check if the app is already running
            var existingProcesses = Process.GetProcessesByName(processName);
            if (existingProcesses.Length > 0)
            {
                // Bring existing window to front (no debug logging)
                try
                {
                    foreach (var proc in existingProcesses)
                    {
                        if (!proc.HasExited && proc.MainWindowHandle != IntPtr.Zero)
                        {
                            var hwnd = proc.MainWindowHandle;
                            BringWindowToFront(hwnd);
                        }
                    }
                }
                catch { /* Suppress diagnostics */ }
                return Ok(new { launched = false, alreadyRunning = true });
            }

            var commandLine = await getCommandLine();

            if (string.IsNullOrWhiteSpace(commandLine))
                return BadRequest(new { error = $"{appName} command line is not configured. Please check Settings." });

            var (exe, args) = ParseCommandLine(commandLine);

            if (!System.IO.File.Exists(exe))
            {
                return BadRequest(new { error = $"{appName} executable not found: {exe}" });
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                var process = Process.Start(startInfo);
                if (process != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            for (int i = 0; i < 50; i++)
                            {
                                await Task.Delay(100);
                                process.Refresh();
                                if (process.MainWindowHandle != IntPtr.Zero)
                                {
                                    BringWindowToFront(process.MainWindowHandle);
                                    break;
                                }
                            }
                        }
                        catch { /* Suppress diagnostics */ }
                    });
                }
                _processStatusCache.InvalidateCache(processName);
                return Ok(new { launched = true, alreadyRunning = false });
            }
            catch (Exception ex)
            {
                // Only log user-facing error
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private async void BringWindowToFront(IntPtr hwnd)
        {
            if (!OperatingSystem.IsWindows()) return;

            // Multi-step approach to force window to foreground
            WindowNativeMethods.ShowWindow(hwnd, WindowNativeMethods.SW_RESTORE);

            // Make it topmost temporarily
            WindowNativeMethods.SetWindowPos(hwnd, WindowNativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                WindowNativeMethods.SWP_NOMOVE | WindowNativeMethods.SWP_NOSIZE | WindowNativeMethods.SWP_SHOWWINDOW);

            // Small delay
            await Task.Delay(50);

            // Remove topmost flag
            WindowNativeMethods.SetWindowPos(hwnd, WindowNativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                WindowNativeMethods.SWP_NOMOVE | WindowNativeMethods.SWP_NOSIZE | WindowNativeMethods.SWP_SHOWWINDOW);

            // Bring to top and set foreground
            WindowNativeMethods.BringWindowToTop(hwnd);
            WindowNativeMethods.SetForegroundWindow(hwnd);
        }

        // Strict contract — the user owns quoting. Two cases:
        //   (a) starts with " — exe is the contents of the first quoted token,
        //       everything after the closing " is passed as arguments
        //   (b) otherwise — exe is everything up to the first space,
        //       everything after that space is passed as arguments
        //
        // No "is the whole string a file path" fallback: that heuristic was
        // safe for #15 (path with spaces, no args) but cannot distinguish
        // "path-with-spaces" from "path-no-spaces args-with-spaces", so it
        // creates more confusion than it removes. Users with spaces in their
        // path must quote it themselves — SettingsService migrates legacy
        // unquoted paths on read, and the USER_MANUAL documents the rule.
        private static (string exe, string args) ParseCommandLine(string commandLine)
        {
            commandLine = commandLine.Trim();

            if (commandLine.StartsWith('"'))
            {
                var closeQuote = commandLine.IndexOf('"', 1);
                if (closeQuote > 0)
                    return (commandLine[1..closeQuote], commandLine[(closeQuote + 1)..].Trim());
                // Malformed (opening quote, no close) — treat the rest as exe.
                return (commandLine[1..], string.Empty);
            }

            var spaceIndex = commandLine.IndexOf(' ');
            return spaceIndex < 0
                ? (commandLine, string.Empty)
                : (commandLine[..spaceIndex], commandLine[(spaceIndex + 1)..].Trim());
        }
    }

    // Native methods for window management (shared)
    internal static class WindowNativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        internal const int SW_RESTORE = 9;
        internal const int SW_SHOWNORMAL = 1;
        internal const int SW_SHOW = 5;

        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_SHOWWINDOW = 0x0040;
    }
}
