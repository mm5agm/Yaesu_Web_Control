// Yaesu Web Control — AccAudioService: the ACC audio scope, sent to the pages
// showing it.
//
// A page subscribes when its MULTI group is open and on screen, and leaves
// when it's closed or the page goes. While anyone is watching this holds an
// AccScopeService subscription, so it shares the board with the SDR panels'
// ACC scope and the port is let go when nobody needs it. Every other frame is
// sent (about 12 a second) to the "accAudio" SignalR group, or once a second
// the reason there are none.

using Microsoft.AspNetCore.SignalR;
using Yaesu_Web_Control.Hubs;
using YaesuAccScope;

namespace Yaesu_Web_Control.Services.Sdr;

public sealed class AccAudioService : IDisposable
{
    public const string GroupName = "accAudio";

    // The board sends 25 frames a second; half is plenty to watch.
    private const int SendEvery = 2;

    // How long without a frame before the page is told why.
    private static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(1);

    // A page reload drops the connection and opens a new one a moment later.
    // Finding the board again takes seconds, so hold it across that gap.
    private static readonly TimeSpan ReleaseDebounce = TimeSpan.FromSeconds(3);

    private readonly AccScopeService _acc;
    private readonly IHubContext<RadioHub> _hub;
    private readonly ISettingsService _settings;
    private readonly ILogger<AccAudioService> _logger;

    private readonly object _gate = new();
    private readonly HashSet<string> _subscribers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pumpCts;
    private CancellationTokenSource? _releaseCts;
    private readonly SendGate _sendGate = new();

    public AccAudioService(
        AccScopeService acc,
        IHubContext<RadioHub> hub,
        ISettingsService settings,
        ILogger<AccAudioService> logger)
    {
        _acc      = acc;
        _hub      = hub;
        _settings = settings;
        _logger   = logger;
    }

    /// <summary>
    /// A page wants the audio scope. Returns null when frames will follow on
    /// RadioStateUpdate as property "AccAudio", or why they won't.
    /// </summary>
    public string? Subscribe(string connectionId)
    {
        if (!AccScope.ModelHasAcc(_settings.GetCachedSettings().RadioModel))
            return "Only the FTdx101MP, FTdx101D and FTdx10 send their audio scope from the ACC socket.";
        var s = _settings.GetCachedSettings();
        if (!AccScope.AudioScopeWanted(s.RadioModel, s.SdrDeviceKeyA, s.SdrDeviceKeyB))
            return AccScope.AudioScopeNotPicked;

        lock (_gate)
        {
            CancelReleaseLocked();
            _subscribers.Add(connectionId);
            if (_pumpCts is null)
            {
                _pumpCts = new CancellationTokenSource();
                var subscription = _acc.Subscribe();
                var token = _pumpCts.Token;
                _ = Task.Run(() => PumpAsync(subscription, token));
                _logger.LogInformation("ACC audio scope: started");
            }
        }
        return null;
    }

    /// <summary>A page has closed the group or gone. Lets the board go after the last.</summary>
    public void Unsubscribe(string connectionId)
    {
        lock (_gate)
        {
            if (!_subscribers.Remove(connectionId) || _subscribers.Count > 0 || _pumpCts is null)
                return;
            CancelReleaseLocked();
            var cts = new CancellationTokenSource();
            _releaseCts = cts;
            _ = ReleaseAfterDebounceAsync(cts.Token);
        }
    }

    private void CancelReleaseLocked()
    {
        if (_releaseCts is null) return;
        try { _releaseCts.Cancel(); } catch (ObjectDisposedException) { }
        _releaseCts.Dispose();
        _releaseCts = null;
    }

    private async Task ReleaseAfterDebounceAsync(CancellationToken token)
    {
        try { await Task.Delay(ReleaseDebounce, token); }
        catch (OperationCanceledException) { return; }

        lock (_gate)
        {
            if (token.IsCancellationRequested || _subscribers.Count > 0) return;
            StopPumpLocked();
            CancelReleaseLocked();
        }
        _logger.LogInformation("ACC audio scope: stopped (no page is showing it)");
    }

    private void StopPumpLocked()
    {
        if (_pumpCts is null) return;
        _pumpCts.Cancel();
        _pumpCts.Dispose();
        _pumpCts = null;
    }

    private async Task PumpAsync(AccScopeService.Subscription subscription, CancellationToken ct)
    {
        using (subscription)
        {
            int count = 0;
            while (!ct.IsCancellationRequested)
            {
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quiet.CancelAfter(QuietTime);
                try
                {
                    if (!await subscription.Frames.WaitToReadAsync(quiet.Token).ConfigureAwait(false))
                        return;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Send(AccAudio.Problem(AccAudio.ProblemText(_acc.State)));
                    continue;
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                AccFrame? latest = null;
                while (subscription.Frames.TryRead(out var frame)) latest = frame;
                if (latest is null || ++count % SendEvery != 0) continue;
                Send(AccAudio.Build(latest));
            }
        }
    }

    private void Send(AccAudioFrame frame)
    {
        // Drop a frame rather than queue behind a slow page: the next is 80 ms away.
        if (!_sendGate.TryBegin(out long token, out bool stuck)) return;
        if (stuck)
            _logger.LogWarning("ACC audio scope: a frame send has not finished after {Ms} ms; sending again", SendGate.StuckMs);

        Task send;
        try
        {
            send = _hub.Clients.Group(GroupName)
                .SendAsync("RadioStateUpdate", new { property = "AccAudio", value = frame });
        }
        catch (Exception ex)
        {
            _sendGate.End(token);
            _logger.LogWarning(ex, "ACC audio scope send threw");
            return;
        }
        _ = send.ContinueWith(t =>
        {
            _sendGate.End(token);
            if (t.IsFaulted)
                _logger.LogWarning(t.Exception, "ACC audio scope send failed");
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CancelReleaseLocked();
            StopPumpLocked();
            _subscribers.Clear();
        }
    }
}
