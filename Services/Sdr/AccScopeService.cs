// Yaesu Web Control — AccScopeService: the one owner of the ACC board's port.
//
// Both SDR panels can show the ACC scope at once (Main on one, Sub on the
// other), and every frame from the board carries both receivers' rows, so one
// reader serves them both. It runs while at least one panel is subscribed and
// lets the port go when the last one leaves, so the ACC Spectrum Viewer or the
// firmware flasher can have it.
//
// The board is found with AccBoardFinder: it only ever looks at Espressif
// ports (USB ID 303A), opens them with DTR and RTS low and never writes to
// them. A radio's CAT port is never opened from here.

using System.Threading.Channels;
using YaesuAccScope;

namespace Yaesu_Web_Control.Services.Sdr;

public sealed class AccScopeService : IDisposable
{
    private const int RetryDelayMs = 5_000;

    private readonly ILogger<AccScopeService> _logger;
    private readonly object _lock = new();
    private readonly List<Channel<AccFrame>> _subscribers = new();
    private CancellationTokenSource? _runCts;
    private volatile AccScopeState _state = new(AccScopeStatus.Stopped, null, null);

    public AccScopeService(ILogger<AccScopeService> logger) => _logger = logger;

    /// <summary>What the reader is doing now: looking, reading, or why it can't.</summary>
    public AccScopeState State => _state;

    /// <summary>
    /// Frames from the board, newest kept: a slow reader loses old frames,
    /// never holds the board up. Dispose the subscription to leave; the last
    /// one to leave closes the port.
    /// </summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<AccFrame>(new BoundedChannelOptions(2)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        lock (_lock)
        {
            _subscribers.Add(channel);
            if (_runCts is null)
            {
                _runCts = new CancellationTokenSource();
                var token = _runCts.Token;
                _ = Task.Run(() => RunAsync(token));
            }
        }
        return new Subscription(this, channel);
    }

    private void Unsubscribe(Channel<AccFrame> channel)
    {
        CancellationTokenSource? stop = null;
        lock (_lock)
        {
            if (!_subscribers.Remove(channel)) return;
            channel.Writer.TryComplete();
            if (_subscribers.Count == 0)
            {
                stop = _runCts;
                _runCts = null;
            }
        }
        if (stop is not null)
        {
            _logger.LogInformation("ACC scope: no panel is using it — letting the board's port go");
            stop.Cancel();
            stop.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AccSerialReader? reader = null;
            try
            {
                SetState(AccScopeStatus.Searching, null, null);
                // Up to 6 s per Espressif port; never touches any other port.
                string? port = await Task.Run(() => AccBoardFinder.Find(), ct).ConfigureAwait(false);
                if (port is null)
                {
                    SetState(AccScopeStatus.NoBoard, null,
                        "No ACC board found. Plug the board's socket marked USB into this PC. If ACC Spectrum " +
                        "Viewer or ACC Firmware Flasher is running, close it: only one program can have the board.");
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                    continue;
                }

                var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                reader = new AccSerialReader(port);
                reader.FrameReceived += (_, e) => Publish(e.Frame);
                reader.Failed        += (_, ex) => failed.TrySetResult(ex);
                reader.Start();
                _logger.LogInformation("ACC scope: reading the board on {Port}", port);
                SetState(AccScopeStatus.Reading, port, null);

                using (ct.Register(() => failed.TrySetCanceled()))
                {
                    var ex = await failed.Task.ConfigureAwait(false);
                    _logger.LogWarning("ACC scope: the board on {Port} stopped — {Message}", port, ex.Message);
                    SetState(AccScopeStatus.Lost, port, $"The ACC board on {port} stopped ({ex.Message}). Looking for it again.");
                }
                await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ACC scope: could not open the board — {Message}", ex.Message);
                SetState(AccScopeStatus.Lost, null, $"Could not open the ACC board: {ex.Message}");
                try { await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            finally
            {
                reader?.Dispose();
            }
        }
        SetState(AccScopeStatus.Stopped, null, null);
    }

    private void Publish(AccFrame frame)
    {
        lock (_lock)
            foreach (var s in _subscribers) s.Writer.TryWrite(frame);
    }

    private void SetState(AccScopeStatus status, string? port, string? problem) =>
        _state = new AccScopeState(status, port, problem);

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _subscribers) s.Writer.TryComplete();
            _subscribers.Clear();
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly AccScopeService _owner;
        private readonly Channel<AccFrame> _channel;

        internal Subscription(AccScopeService owner, Channel<AccFrame> channel)
        {
            _owner = owner;
            _channel = channel;
        }

        public ChannelReader<AccFrame> Frames => _channel.Reader;

        public void Dispose() => _owner.Unsubscribe(_channel);
    }
}

public enum AccScopeStatus { Stopped, Searching, NoBoard, Reading, Lost }

/// <param name="Port">The board's port while it's being read.</param>
/// <param name="Problem">What to tell the operator, when something's wrong.</param>
public sealed record AccScopeState(AccScopeStatus Status, string? Port, string? Problem);
