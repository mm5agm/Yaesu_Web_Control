using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using RadioWebControl.Core.Services.Cw;
using Yaesu_Web_Control.Hubs;

namespace Yaesu_Web_Control.Services.Cw
{
    /// <summary>
    /// The key line for the dry run: it logs and does nothing else. It never
    /// opens a COM port and never touches DTR. It is the line at start-up,
    /// and what the service drops back to if the real port fails.
    /// </summary>
    public sealed class DryRunCwKeyLine : ICwKeyLine
    {
        private readonly ILogger _logger;
        public DryRunCwKeyLine(ILogger logger) => _logger = logger;
        public void SetKey(bool down) => _logger.LogDebug("CW keying (dry run): key {State}", down ? "down" : "up");
    }

    /// <summary>
    /// Plays a paddle keyer in the operator's browser on this PC: packets come
    /// in on RadioHub.CwKey, a <see cref="CwKeyingPlayer"/> holds them in its
    /// jitter buffer, and a dedicated thread plays each event at its moment.
    ///
    /// Ordinary timers are no use for this: a .NET timer on Windows fires on
    /// the 15.6 ms system tick, which is a quarter of a dit at 20 WPM. The
    /// thread sleeps 1 ms at a time with the system timer raised to 1 ms
    /// (timeBeginPeriod, only while it is running) and spins the last 2 ms.
    ///
    /// One browser owns the key at a time: the first to send, until it
    /// disconnects or goes quiet for <see cref="OwnerIdle"/> with the key up.
    /// Another browser sending meanwhile is told so and ignored.
    ///
    /// Reports (each element played, every safety let-go) go to the owning
    /// browser for its log, sent from a separate task so the timing thread
    /// never waits on the network.
    ///
    /// The key line starts as the dry run (<see cref="DryRunCwKeyLine"/>) at
    /// every start-up. The operator can switch it to DTR on the radio's
    /// Standard COM port (<see cref="SerialCwKeyLine"/>) from the test page;
    /// that choice is not saved. The CAT port is refused. If the port fails
    /// while keying, the key is let go and the line goes back to the dry run.
    ///
    /// Every platform (Fabio, D#274 2026-11-02): it keys once a key port is
    /// chosen, whatever the OS. Only Windows has been tried, so elsewhere the
    /// page and the log say so, and the page shows the timing (how late the
    /// thread played each element, and how much of the jitter buffer the
    /// network used) for the operator to judge. ThreadPriority does nothing
    /// on Linux, which is one reason a Pi needs watching.
    /// </summary>
    public sealed class CwKeyingService : IDisposable
    {
        public const string ReportMethod = "CwKeyReport";
        private static readonly TimeSpan OwnerIdle = TimeSpan.FromSeconds(5);
        private const double SpinMs = 2;

        private readonly IHubContext<RadioHub> _hub;
        private readonly ILogger<CwKeyingService> _logger;
        private readonly CwKeyingPlayer _player;
        private readonly object _lock = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Channel<(string ConnectionId, CwKeyingReport Report)> _reports =
            Channel.CreateUnbounded<(string, CwKeyingReport)>(new UnboundedChannelOptions { SingleReader = true });
        private Thread? _thread;
        private volatile bool _stopping;
        private readonly ISettingsService _settings;
        private readonly DryRunCwKeyLine _dryRun;
        private string? _owner;
        private double _ownerLastMs;
        // The real key line, when the operator has chosen one; null = dry run.
        private SerialCwKeyLine? _serial;
        // Set by the line when the port failed mid-keying; acted on after the player returns.
        private Exception? _lineFailed;

        public CwKeyingService(IHubContext<RadioHub> hub, ILogger<CwKeyingService> logger, ISettingsService settings)
        {
            _hub = hub;
            _logger = logger;
            _settings = settings;
            _dryRun = new DryRunCwKeyLine(logger);
            _player = new CwKeyingPlayer(new SwitchLine(this));
            _player.Report += OnReport;
            _ = Task.Run(ForwardReportsAsync);
        }

        private double NowMs => _clock.Elapsed.TotalMilliseconds;

        /// <summary>Where paddle keying has been tried (see the class comment).</summary>
        public static bool Tested => OperatingSystem.IsWindows();

        /// <summary>This OS, for "not tested on ..." in the page and the log.</summary>
        public static string Platform =>
            OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription;

        private string Mode => _serial is null ? "dry run" : $"DTR on {_serial.PortName}";

        /// <summary>The player's line: whichever is chosen. Called under _lock.</summary>
        private sealed class SwitchLine(CwKeyingService s) : ICwKeyLine
        {
            public void SetKey(bool down)
            {
                if (s._serial is null)
                {
                    s._dryRun.SetKey(down);
                    return;
                }
                try
                {
                    s._serial.SetKey(down);
                }
                catch (Exception ex)
                {
                    s._lineFailed = ex;
                    s.CloseSerial();
                }
            }
        }

        // Under _lock, after anything that may have keyed: a port that failed
        // is reported, and the player lets go and waits for the next key down.
        private void CheckLine(double now)
        {
            if (_lineFailed is not { } ex) return;
            _lineFailed = null;
            _logger.LogWarning(ex, "CW keying: the key port failed; back to the dry run.");
            _player.Abort($"the key port failed ({ex.Message}); back to the dry run", now);
        }

        private void CloseSerial()
        {
            var s = _serial;
            _serial = null;
            if (s is null) return;
            s.Dispose();
            _logger.LogInformation("CW keying: key port {Port} closed.", s.PortName);
        }

        /// <summary>For the page: the key line now, the CAT port, and the COM ports to choose from.</summary>
        public object LineStatus()
        {
            var ports = SerialCwKeyLine.Ports();
            string? cat = CatPort();
            lock (_lock)
            {
                return new
                {
                    tested = Tested,
                    platform = Platform,
                    bufferMs = _player.BufferMs,
                    keyPort = _serial?.PortName,
                    // As the port list names it: on Linux the CAT port may be
                    // set as a /dev/serial/by-id link to the same ttyUSB node.
                    catPort = cat is null ? null : SerialCwKeyLine.RealPath(cat),
                    ports = ports.Select(p => new { name = p.Name, description = p.Description }),
                };
            }
        }

        private string? CatPort()
        {
            var port = _settings.GetCachedSettings().SerialPort;
            return string.IsNullOrWhiteSpace(port) ? null : port.Trim();
        }

        /// <summary>
        /// Chooses the key line: a COM port to key by DTR, or null for the dry
        /// run. Lets go of the key first. Returns null when done, or why not.
        /// </summary>
        public string? SetKeyPort(string? port)
        {
            port = string.IsNullOrWhiteSpace(port) ? null : port.Trim();
            lock (_lock)
            {
                if (string.Equals(port, _serial?.PortName, StringComparison.OrdinalIgnoreCase)) return null;
                if (_player.KeyDown) _player.Abort("the key line changed", NowMs);
                CloseSerial();
                if (port is null)
                {
                    _logger.LogInformation("CW keying: key line is the dry run.");
                    return null;
                }
                if (CatPort() is { } cat && string.Equals(SerialCwKeyLine.RealPath(port), SerialCwKeyLine.RealPath(cat), StringComparison.OrdinalIgnoreCase))
                    return $"{port} is the CAT port. Choose the radio's Standard COM port.";
                if (!SerialPort.GetPortNames().Contains(port, StringComparer.OrdinalIgnoreCase))
                    return $"{port} is not on this PC.";
                try
                {
                    _serial = new SerialCwKeyLine(port);
                }
                catch (UnauthorizedAccessException)
                {
                    return $"{port} is in use by another program (WSJT-X or a logger using it for PTT?). Close that, or choose another port.";
                }
                catch (Exception ex)
                {
                    return $"{port} could not be opened: {ex.Message}";
                }
                _logger.LogWarning("CW keying: keying the radio by DTR on {Port}.", port);
                if (!Tested)
                    _logger.LogWarning("CW keying has not been tested on {Platform} yet: check the timing summary on the CW Paddle page.", Platform);
                return null;
            }
        }

        /// <summary>A packet from a browser's keyer (RadioHub.CwKey).</summary>
        public void Receive(string connectionId, CwKeyPacket packet)
        {
            lock (_lock)
            {
                double now = NowMs;
                if (_owner is not null && _owner != connectionId)
                {
                    bool ownerGone = !_player.KeyDown && now - _ownerLastMs > OwnerIdle.TotalMilliseconds;
                    if (!ownerGone)
                    {
                        if (packet.Events.Count > 0)
                            _reports.Writer.TryWrite((connectionId, new CwKeyingReport("busy", "Another browser has the key", now)));
                        return;
                    }
                }
                if (_owner != connectionId)
                {
                    _player.Reset(now);
                    _owner = connectionId;
                    _logger.LogInformation("CW keying ({Mode}): browser {ConnectionId} has the key.", Mode, connectionId);
                }
                _ownerLastMs = now;
                _player.Receive(packet, now);
                CheckLine(now);
                EnsureThread();
            }
            _wake.Set();
        }

        /// <summary>A browser connection closed: if it had the key, let go.</summary>
        public void Disconnected(string connectionId)
        {
            lock (_lock)
            {
                if (_owner != connectionId) return;
                _player.Abort("the browser disconnected", NowMs);
                _owner = null;
            }
            _wake.Set();
        }

        private void OnReport(CwKeyingReport r)
        {
            // Called under _lock, from Receive or the timing thread.
            if (r.Kind == "safety") _logger.LogWarning("CW keying ({Mode}): {Text}", Mode, r.Text);
            else _logger.LogDebug("CW keying ({Mode}): {Text} (late {Late:0.0} ms)", Mode, r.Text, r.LateMs);
            if (_owner is not null) _reports.Writer.TryWrite((_owner, r));
        }

        private async Task ForwardReportsAsync()
        {
            await foreach (var (id, r) in _reports.Reader.ReadAllAsync())
            {
                try
                {
                    await _hub.Clients.Client(id).SendAsync(ReportMethod,
                        new { kind = r.Kind, text = r.Text, atMs = r.AtMs, lateMs = r.LateMs, playedMs = r.PlayedMs, sentMs = r.SentMs, bufferLeftMs = r.BufferLeftMs });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "CW keying: report to {ConnectionId} not sent.", id);
                }
            }
        }

        private void EnsureThread()
        {
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "CW keying", Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        private void Run()
        {
            bool raised = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;
            try
            {
                while (!_stopping)
                {
                    double? due;
                    lock (_lock)
                    {
                        double now = NowMs;
                        _player.Tick(now);
                        CheckLine(now);
                        due = _player.NextDueMs;
                    }
                    if (due is null)
                    {
                        _wake.WaitOne(250);
                        continue;
                    }
                    double wait = due.Value - NowMs;
                    if (wait > SpinMs)
                        _wake.WaitOne(TimeSpan.FromMilliseconds(Math.Min(wait - SpinMs, 250)));
                    else
                        while (NowMs < due.Value && !_stopping) Thread.SpinWait(20);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CW keying thread failed.");
                lock (_lock)
                {
                    _player.Abort("the keying thread failed", NowMs);
                    CloseSerial();
                }
            }
            finally
            {
                if (raised) TimeEndPeriod(1);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _player.Abort("YWC is shutting down", NowMs);
                CloseSerial();
            }
            _stopping = true;
            _wake.Set();
            _thread?.Join(500);
            _reports.Writer.TryComplete();
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint ms);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint ms);
    }
}
