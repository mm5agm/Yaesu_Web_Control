using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using RadioWebControl.Core.Services.Cw;
using Yaesu_Web_Control.Hubs;

namespace Yaesu_Web_Control.Services.Cw
{
    /// <summary>
    /// The key line for the dry run: it logs and does nothing else. It never
    /// opens a COM port and never touches DTR, so nothing can be keyed while
    /// the network side is being proved.
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
    /// Dry run: the line is <see cref="DryRunCwKeyLine"/>.
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
        private string? _owner;
        private double _ownerLastMs;

        public CwKeyingService(IHubContext<RadioHub> hub, ILogger<CwKeyingService> logger)
        {
            _hub = hub;
            _logger = logger;
            _player = new CwKeyingPlayer(new DryRunCwKeyLine(logger));
            _player.Report += OnReport;
            _ = Task.Run(ForwardReportsAsync);
        }

        private double NowMs => _clock.Elapsed.TotalMilliseconds;

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
                    _logger.LogInformation("CW keying (dry run): browser {ConnectionId} has the key.", connectionId);
                }
                _ownerLastMs = now;
                _player.Receive(packet, now);
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
            if (r.Kind == "safety") _logger.LogWarning("CW keying (dry run): {Text}", r.Text);
            else _logger.LogDebug("CW keying (dry run): {Text} (late {Late:0.0} ms)", r.Text, r.LateMs);
            if (_owner is not null) _reports.Writer.TryWrite((_owner, r));
        }

        private async Task ForwardReportsAsync()
        {
            await foreach (var (id, r) in _reports.Reader.ReadAllAsync())
            {
                try
                {
                    await _hub.Clients.Client(id).SendAsync(ReportMethod,
                        new { kind = r.Kind, text = r.Text, atMs = r.AtMs, lateMs = r.LateMs, playedMs = r.PlayedMs, sentMs = r.SentMs });
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
                        _player.Tick(NowMs);
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
                lock (_lock) _player.Abort("the keying thread failed", NowMs);
            }
            finally
            {
                if (raised) TimeEndPeriod(1);
            }
        }

        public void Dispose()
        {
            lock (_lock) _player.Abort("YWC is shutting down", NowMs);
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
