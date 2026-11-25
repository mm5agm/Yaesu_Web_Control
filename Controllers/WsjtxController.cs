using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Wsjtx;
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
        private readonly ICatClient _catClient;
        private readonly RadioStateService _radioState;

        public WsjtxController(ISettingsService settingsService, ILogger<WsjtxController> logger, WsjtxUdpService udpService, ProcessStatusCacheService processStatusCache,
            ICatClient catClient, RadioStateService radioState)
        {
            _catClient = catClient;
            _radioState = radioState;
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

        // GET api/wsjtx/station: the operator's callsign and grid as WSJT-X/WS
        // reports them in its Status, for the Spot Map page. Read-only, and not
        // behind the FT8 Tiles switch: the Spot Map is a page anyone can open.
        // Empty strings until WSJT-X has sent a Status.
        [HttpGet("station")]
        public IActionResult Station() => Ok(new
        {
            connected = _udpService.IsConnected,
            call = _udpService.DeCall,
            grid = _udpService.DeGrid,
        });

        // --- The big-tile page (issue #332) ---

        // The page is opt-in (Settings, ApplicationSettings.Ft8TilesEnabled).
        // While it's off, nothing here asks WSJT-Z or the radio to do anything,
        // whoever calls it. Stop transmit is the exception: stopping is never refused.
        private const string TilesOffError = "FT8 Tiles is off. Turn it on in Application Setup: tick Show the FT8 Tiles page.";
        private bool TilesEnabled => _settingsService.GetCachedSettings().Ft8TilesEnabled;

        // GET api/wsjtx/calls: everything the page shows, polled about once a second.
        [HttpGet("calls")]
        public IActionResult Calls()
        {
            if (!TilesEnabled) return Ok(new { enabled = false });
            var now = DateTime.UtcNow;
            var mode = _udpService.WsjtxMode;
            double period = WsjtxCallList.PeriodSeconds(mode);
            // Two periods plus a margin: a station heard last period is still shown.
            var stations = _udpService.CallList.Snapshot(now, TimeSpan.FromSeconds(period * 2 + 3));
            long dial = _udpService.DialFrequency;
            if (dial == 0) dial = _radioState.FrequencyA;
            var current = WsjtxBands.BandOf(dial);
            // The last logged QSO, for a minute: the page shows it as a banner.
            var logged = _udpService.LastLogged;
            object? loggedQso = logged is { } l && now - l.ReceivedUtc < TimeSpan.FromSeconds(60)
                ? new
                {
                    call = l.Qso.DxCall,
                    grid = l.Qso.DxGrid,
                    frequency = l.Qso.TxFrequencyHz,
                    mode = l.Qso.Mode,
                    sent = l.Qso.ReportSent,
                    received = l.Qso.ReportReceived,
                    ageSeconds = Math.Round((now - l.ReceivedUtc).TotalSeconds, 1),
                }
                : null;
            return Ok(new
            {
                enabled = true,
                connected = _udpService.IsConnected,
                transmitting = _udpService.IsTransmitting,
                txEnabled = _udpService.TxEnabled,
                mode,
                dialFrequency = dial,
                dxCall = _udpService.DxCall,
                myCall = _udpService.DeCall,
                txMessage = _udpService.TxMessage,
                periodSeconds = period,
                cqStartsIn = _udpService.CqStartsInSeconds,
                logged = loggedQso,
                stations = stations.Select(e => new
                {
                    call = e.Call,
                    kind = e.Kind switch { CallKind.CallingYou => "you", CallKind.Finished => "finished", _ => "cq" },
                    grid = e.Grid,
                    snr = e.Decode.Snr,
                    ageSeconds = Math.Round((now - e.ReceivedUtc).TotalSeconds, 1),
                }),
                bands = WsjtxBands.All
                    .Where(b => RadioCapabilities.IsTunableFrequency(_radioState.RadioModel, b.Ft8Hz))
                    .Select(b => new { name = b.Name, current = b == current }),
            });
        }

        public record CallRequest(string Call);
        public record AutoCallRequest(bool On);
        public record BandRequest(string Band);

        // POST api/wsjtx/reply: answer a station, as a double-click in WSJT-Z would.
        [HttpPost("reply")]
        public async Task<IActionResult> Reply([FromBody] CallRequest request)
        {
            if (!TilesEnabled) return Conflict(new { error = TilesOffError });
            if (string.IsNullOrWhiteSpace(request.Call)) return BadRequest(new { error = "No callsign" });
            var (ok, error) = await _udpService.SendReplyAsync(request.Call.Trim());
            return ok ? Ok(new { sent = true }) : Conflict(new { error });
        }

        // POST api/wsjtx/halt: stop transmitting now.
        [HttpPost("halt")]
        public async Task<IActionResult> Halt()
        {
            var (ok, error) = await _udpService.SendHaltTxAsync(autoTxOnly: false);
            return ok ? Ok(new { sent = true }) : Conflict(new { error });
        }

        // POST api/wsjtx/autocall: WSJT-Z's Auto Call on or off (Auto CQ goes off too).
        [HttpPost("autocall")]
        public async Task<IActionResult> AutoCall([FromBody] AutoCallRequest request)
        {
            if (!TilesEnabled) return Conflict(new { error = TilesOffError });
            var (ok, error) = await _udpService.SendAutoCallAsync(request.On);
            return ok ? Ok(new { sent = true }) : Conflict(new { error });
        }

        // POST api/wsjtx/autocq: WSJT-Z's Auto CQ on or off (Auto Call goes off too).
        [HttpPost("autocq")]
        public async Task<IActionResult> AutoCq([FromBody] AutoCallRequest request)
        {
            if (!TilesEnabled) return Conflict(new { error = TilesOffError });
            var (ok, error) = await _udpService.SendAutoCqAsync(request.On);
            return ok ? Ok(new { sent = true, startsIn = _udpService.CqStartsInSeconds }) : Conflict(new { error });
        }

        // POST api/wsjtx/band: put the radio on a band's FT8 (or FT4) frequency.
        // WSJT-X follows when it next reads the radio's frequency.
        [HttpPost("band")]
        public async Task<IActionResult> Band([FromBody] BandRequest request)
        {
            if (!TilesEnabled) return Conflict(new { error = TilesOffError });
            var band = WsjtxBands.All.FirstOrDefault(b => string.Equals(b.Name, request.Band, StringComparison.OrdinalIgnoreCase));
            if (band is null) return BadRequest(new { error = $"Unknown band {request.Band}" });
            if (_udpService.IsTransmitting) return Conflict(new { error = "Transmitting: stop first" });
            long hz = WsjtxBands.DialFor(band, _udpService.WsjtxMode);
            if (!RadioCapabilities.IsTunableFrequency(_radioState.RadioModel, hz))
                return BadRequest(new { error = $"This radio can't tune {band.Name}" });

            // WSJT-X goes on broadcasting the old band until it reads the radio
            // again; don't let that pull the radio back.
            _udpService.PauseDialSync(TimeSpan.FromSeconds(10));
            if (!await _catClient.SetFrequencyAAsync(hz)) return StatusCode(503, new { error = "The radio didn't take the frequency" });
            _radioState.FrequencyA = hz;
            _radioState.MarkUserFrequencyWrite();
            _udpService.CallList.Clear();
            return Ok(new { band = band.Name, frequency = hz });
        }
    }
}
