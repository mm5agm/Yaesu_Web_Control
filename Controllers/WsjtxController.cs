using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yaesu_Web_Control.Controllers
{
    // Native methods for window management
    internal static class NativeMethods
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
        internal const int SW_MAXIMIZE = 3;

        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_SHOWWINDOW = 0x0040;
    }

    [ApiController]
    [Route("api/[controller]")]
    public class WsjtxController : ControllerBase
    {
        private readonly ISettingsService _settingsService;
        // Logger removed as part of cleanup
        private readonly WsjtxUdpService _udpService;
        private readonly ProcessStatusCacheService _processStatusCache;

        public WsjtxController(ISettingsService settingsService, ILogger<WsjtxController> logger, WsjtxUdpService udpService, ProcessStatusCacheService processStatusCache)
        {
            _settingsService = settingsService;
            // Logger removed
            _udpService = udpService;
            _processStatusCache = processStatusCache;
        }

        [HttpPost("launch")]
        public async Task<IActionResult> Launch()
        {
            // Check if WSJT-X is already running
            var existingProcesses = Process.GetProcessesByName("wsjtx");
            if (existingProcesses.Length > 0)
            {
                // Bring existing window to front (Windows only — no-op elsewhere)
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        foreach (var proc in existingProcesses)
                        {
                            if (!proc.HasExited && proc.MainWindowHandle != IntPtr.Zero)
                            {
                                var hwnd = proc.MainWindowHandle;
                                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
                                await Task.Delay(50);
                                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
                                NativeMethods.BringWindowToTop(hwnd);
                                NativeMethods.SetForegroundWindow(hwnd);
                            }
                        }
                    }
                    catch { /* Suppress diagnostics */ }
                }
                return Ok(new { launched = false, alreadyRunning = true });
            }

            var settings = await _settingsService.GetSettingsAsync();
            var commandLine = settings.WsjtxCommandLine;

            if (string.IsNullOrWhiteSpace(commandLine))
                return BadRequest(new { error = "WSJT-X command line is not configured. Please check Settings." });

            var (exe, args) = ParseCommandLine(commandLine);

            if (!System.IO.File.Exists(exe))
            {
                return BadRequest(new { error = $"WSJT-X executable not found: {exe}" });
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
                // Wait a moment for the window to be created, then ensure it's visible
                if (process != null && OperatingSystem.IsWindows())
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
                                    var hwnd = process.MainWindowHandle;
                                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
                                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, 
                                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
                                    await Task.Delay(50);
                                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
                                    NativeMethods.BringWindowToTop(hwnd);
                                    NativeMethods.SetForegroundWindow(hwnd);
                                    break;
                                }
                            }
                        }
                        catch { /* Suppress diagnostics */ }
                    });
                }
                _processStatusCache.InvalidateCache("wsjtx");
                return Ok(new { launched = true, alreadyRunning = false });
            }
            catch (Exception ex)
            {
                // Only log user-facing error
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private static (string exe, string args) ParseCommandLine(string commandLine)
        {
            commandLine = commandLine.Trim();
            if (commandLine.StartsWith('"'))
            {
                var closeQuote = commandLine.IndexOf('"', 1);
                if (closeQuote > 0)
                    return (commandLine[1..closeQuote], commandLine[(closeQuote + 1)..].Trim());
            }
            var spaceIndex = commandLine.IndexOf(' ');
            return spaceIndex < 0
                ? (commandLine, string.Empty)
                : (commandLine[..spaceIndex], commandLine[(spaceIndex + 1)..].Trim());
        }

        [HttpGet("status")]
        public IActionResult Status()
        {
            // Use cached process status to avoid expensive GetProcessesByName call on every request
            var running = _processStatusCache.IsProcessRunning("wsjtx");
            return Ok(new
            {
                running,
                connected = _udpService.IsConnected,
                transmitting = _udpService.IsTransmitting,
                mode = _udpService.WsjtxMode
            });
        }
    }
}
