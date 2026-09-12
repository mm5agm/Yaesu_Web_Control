// Yaesu Web Control — SdrManager
//
// Replaces SdrBackgroundService.
//
// Why this exists: the SDRplay API v3 service enforces one Selected device per
// host process. We can't open two RSPs from the main YWC process. So YWC main
// spawns a Yaesu_Sdr_Worker.exe per configured SDR; each worker holds exactly
// one device and streams FFT frames back to YWC over a localhost TCP
// connection. SdrManager supervises the workers and forwards their frames to
// SignalR using a per-VFO sdrId-tagged envelope so the frontend can route
// frames to the correct SpectrumPanel.
//
// See docs/decisions/0001-dual-sdr-architecture.md for the full reasoning.
//
// Envelope shape (v2.3.0+):
//   SpectrumUpdate value = { sdrId: "A"|"B", bins, centreHz, spanHz }
//   SdrStatus     value = { sdrId: "A"|"B", status: "..." }
//   SdrError      value = { sdrId: "A"|"B", error:  "..." }

using System.ComponentModel;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Sdr;

public sealed class SdrManager : BackgroundService
{
    private readonly ISettingsService             _settings;
    private readonly IHubContext<RadioHub>        _hub;
    private readonly RadioStateService            _state;
    private readonly ILogger<SdrManager>          _logger;

    private const int RetryDelayMs           = 5_000;
    private const int UnconfiguredPollMs     = 10_000;
    // The worker caps sends at 25/s (see WorkerHost), so this is ~3 s. It used
    // to read "~3 s at 10 fps", which no frame rate the worker ever produced
    // actually matched — the rate is set by the IQ rate over the FFT size.
    private const int StatusHeartbeatFrames  = 75;     // ~3 s at the 25/s send cap
    private const int WorkerConnectTimeoutMs = 10_000;

    // One restart token per VFO, so a span change that needs a new hardware
    // rate on one SDR respawns that worker alone; the other keeps streaming.
    // A settings save cycles both.
    private CancellationTokenSource _restartCtsA = new();
    private CancellationTokenSource _restartCtsB = new();

    // Device keys currently held by spawned workers, keyed by VFO id ("A"/"B").
    // Used by SdrController.GetDevices so the Settings page Scan can include
    // devices that workers have Selected (and which therefore disappear from
    // direct sdrplay_api_GetDevices enumeration). Updated under a lock so the
    // controller's read is consistent with concurrent session lifecycle.
    private readonly Dictionary<string, string> _activeDeviceKeys = new();

    // Frame writers for the currently-connected workers, keyed by VFO id.
    // Used by TrySendDspSettingsAsync to push live DSP-knob updates (gain,
    // dB clamp) into the worker that owns each VFO's spectrum. Populated
    // for the lifetime of one RunSessionAsync call; removed in its finally.
    //
    // Each entry pairs the writer with a SemaphoreSlim so that concurrent
    // slider POSTs serialise their writes on the same NetworkStream —
    // without the lock, two overlapping WriteDspSettingsAsync calls would
    // interleave bytes mid-frame and the worker's ControlReader would
    // read garbage (manifests as B going silent: corrupted length prefix
    // makes the reader block forever waiting for bytes that never come).
    private readonly Dictionary<string, WriterSlot> _writers = new();

    // Plan is what the session's worker was started with (see
    // SpectrumSpanPlan); a new span whose plan has the same hardware
    // settings can be applied to the running worker as one ViewWindow
    // message, anything else is a respawn. WideFftSize is the SdrFftSize
    // setting the plan was made from, needed to plan the next span.
    private sealed record WriterSlot(FrameWriter Writer, SemaphoreSlim Lock, SpectrumSpanPlan.Plan Plan, int WideFftSize);

    private readonly object _activeLock = new();

    /// <summary>
    /// Snapshot of which device keys are currently being held by SDR workers.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetActiveDeviceKeys()
    {
        lock (_activeLock)
            return _activeDeviceKeys.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public SdrManager(
        ISettingsService             settings,
        IHubContext<RadioHub>        hub,
        RadioStateService            state,
        ILogger<SdrManager>          logger)
    {
        _settings = settings;
        _hub      = hub;
        _state    = state;
        _logger   = logger;

        // The dial moves through the IF OUT as the operator works the
        // filter (see YaesuIfOutOffset), so a cropped window has to follow
        // it. Cheap: a ViewWindow message is a volatile swap in the worker,
        // and nothing is sent unless the centre actually moved.
        _state.PropertyChanged += OnRadioStateChanged;
    }

    private void OnRadioStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case "ModeA" or "IfWidthA" or "IfShiftA": _ = RecentreViewAsync("A"); break;
            case "ModeB" or "IfWidthB" or "IfShiftB": _ = RecentreViewAsync("B"); break;
            case "CwPitch": _ = RecentreViewAsync("A"); _ = RecentreViewAsync("B"); break;
        }
    }

    /// <summary>
    /// Where the dial sits in this VFO's SDR stream right now: the radio's
    /// IF OUT for the receiver plus its current LO slide, or the SDR's own
    /// tune frequency for a model whose IF OUT is unmeasured. This is the
    /// centre of every software-zoom crop.
    /// </summary>
    private long DialCentreHz(string vfo, ApplicationSettings config)
    {
        bool b = vfo == "B";
        long? dial = YaesuIfOutOffset.DialIfHz(
            config.RadioModel, vfo,
            mode:        b ? _state.ModeB    : _state.ModeA,
            ifWidthCode: b ? _state.IfWidthB : _state.IfWidthA,
            ifShiftHz:   b ? _state.IfShiftB : _state.IfShiftA,
            cwPitchCode: _state.CwPitch);
        return dial ?? (b ? config.SdrIfFrequencyHzB : config.SdrIfFrequencyHzA);
    }

    /// <summary>
    /// Cancels both sessions so they restart with fresh settings. Triggered
    /// when the user saves SDR-related settings.
    /// </summary>
    public void RequestRestart()
    {
        RequestRestart("A");
        RequestRestart("B");
    }

    /// <summary>
    /// Cancels one VFO's session so it restarts with fresh settings, leaving
    /// the other VFO's worker streaming. Used by the span endpoint when the
    /// new span needs a different hardware rate (see TrySetSpanAsync).
    /// </summary>
    public void RequestRestart(string vfo)
    {
        if (vfo == "B") Cycle(ref _restartCtsB);
        else            Cycle(ref _restartCtsA);

        static void Cycle(ref CancellationTokenSource cts)
        {
            var old = Interlocked.Exchange(ref cts, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();
        }
    }

    private CancellationToken RestartTokenFor(string vfo) =>
        (vfo == "B" ? _restartCtsB : _restartCtsA).Token;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Each VFO is supervised on its own loop so a restart of one never
        // touches the other. The two share nothing below this point: each
        // has its own worker process, TCP connection and device.
        Task.WhenAll(
            SuperviseAsync("A", stoppingToken),
            SuperviseAsync("B", stoppingToken));

    private async Task SuperviseAsync(string vfo, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config       = await _settings.GetSettingsAsync().ConfigureAwait(false);
            var restartToken = RestartTokenFor(vfo);
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, restartToken);

            string? deviceKey = vfo == "B" ? config.SdrDeviceKeyB : config.SdrDeviceKeyA;
            if (string.IsNullOrWhiteSpace(deviceKey))
            {
                // Tell the frontend explicitly so the panel can clear itself
                // rather than show a stale state, then wait for a device to
                // be configured (a settings save cuts the wait short).
                await BroadcastStatus(vfo, "unconfigured", stoppingToken).ConfigureAwait(false);
                try { await Task.Delay(UnconfiguredPollMs, sessionCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                continue;
            }

            await RunSessionAsync(vfo, deviceKey, config, sessionCts.Token).ConfigureAwait(false);

            // Retry after a pause unless a restart was requested, in which
            // case go straight round; a request arriving during the pause
            // cuts it short too.
            if (!stoppingToken.IsCancellationRequested && !restartToken.IsCancellationRequested)
            {
                try { await Task.Delay(RetryDelayMs, sessionCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }

    // ── One worker session, tagged with VFO label ────────────────────────────

    private async Task RunSessionAsync(
        string              vfo,
        string              deviceKey,
        ApplicationSettings config,
        CancellationToken   stoppingToken)
    {
        WorkerProcess? worker = null;
        TcpClient?     client = null;

        try
        {
            await BroadcastStatus(vfo, "connecting", stoppingToken).ConfigureAwait(false);

            // Per-VFO span so each panel can run at a different width
            // (e.g. 2 MHz on the calling band, 2.5 kHz zoomed on the QSO).
            // The setting still carries the old name: below 250 kHz it is
            // no longer the hardware rate but a crop of a fixed 125 kHz
            // stream — SpectrumSpanPlan decides which.
            double spanHz = vfo == "B" ? config.SdrSampleRateHzB : config.SdrSampleRateHzA;
            // Per-VFO centre too: the FTdx101's SUB IF OUT is 100 kHz below
            // its MAIN one (see RadioCapabilities.DefaultSdrCentreHz).
            long   ifFrequencyHz = vfo == "B" ? config.SdrIfFrequencyHzB : config.SdrIfFrequencyHzA;
            var    plan = SpectrumSpanPlan.For(
                spanHz,
                wideFftSize: config.SdrFftSize,
                dialHz:      DialCentreHz(vfo, config));

            worker = WorkerProcess.Start(
                _logger,
                vfo:           vfo,
                deviceKey:     deviceKey,
                ifFrequencyHz: ifFrequencyHz,
                sampleRateHz:  plan.HardwareRateHz,
                fftSize:       plan.FftSize,
                hopSize:       plan.HopSize,
                viewCentreHz:  plan.ViewCentreHz,
                viewSpanHz:    plan.ViewSpanHz);

            lock (_activeLock) _activeDeviceKeys[vfo] = deviceKey;

            client = await ConnectToWorkerAsync(worker.Port, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("SDR {Vfo}: connected to worker on localhost:{Port}", vfo, worker.Port);

            using var stream = client.GetStream();
            var reader = new FrameReader(stream);
            var writer = new FrameWriter(stream);

            // Replay persisted DSP settings BEFORE publishing the writer so a
            // concurrent slider POST can't grab the slot mid-startup and race
            // its WriteDspSettingsAsync against ours on the same NetworkStream.
            // Force a WIDE clamp window and UNITY pre-dB gain. Since the client-side
            // auto-floor port the browser does all vertical scaling (the Range
            // slider) and all waterfall brightening (the Bright slider), so the
            // worker must stream the full, unmodified dynamic range. The persisted
            // Low/High and the old per-VFO Gain are deliberately ignored here —
            // unity gain keeps "Bright = Off" as the same dark waterfall baseline
            // for every user, matching the client's fixed colour window.
            const float wideFloor = -160f, wideCeiling = 0f;
            var dsp = new DspSettingsPayload(1.0f, wideFloor, wideCeiling);
            try { await writer.WriteDspSettingsAsync(dsp, stoppingToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SDR {Vfo}: initial DSP push failed — worker will run with defaults", vfo);
            }

            // Now safe to expose the writer to the controller. The per-slot
            // semaphore serialises any subsequent slider POSTs.
            var writeLock = new SemaphoreSlim(1, 1);
            lock (_activeLock) _writers[vfo] = new WriterSlot(writer, writeLock, plan, config.SdrFftSize);

            int heartbeatCounter = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                WorkerMessage? msg;
                try
                {
                    msg = await reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    _logger.LogInformation("SDR {Vfo}: worker disconnected — will restart", vfo);
                    break;
                }

                if (msg == null)
                {
                    _logger.LogInformation("SDR {Vfo}: worker EOF — will restart", vfo);
                    break;
                }

                // Every broadcast below is dispatched fire-and-forget (see Dispatch).
                // This loop's one job is to drain the worker; it must never block on
                // client I/O. Awaiting Clients.All.SendAsync waits on the *slowest*
                // client's transport pipe — a page frozen into the bfcache during
                // navigation, or any dead/slow socket, has a stalled pipe that large
                // spectrum frames quickly fill. A blocking await there freezes frame
                // delivery to *every* client for ~7 s until the zombie is reaped.
                switch (msg)
                {
                    case SpectrumFrameMsg sf:
                        Dispatch(BroadcastFrame(vfo, sf, stoppingToken), vfo);

                        if (++heartbeatCounter >= StatusHeartbeatFrames)
                        {
                            heartbeatCounter = 0;
                            Dispatch(BroadcastStatus(vfo, "streaming", stoppingToken), vfo);
                        }
                        break;

                    case StatusUpdateMsg s:
                        _logger.LogInformation("SDR {Vfo}: worker status — {Status}", vfo, s.Status);
                        Dispatch(BroadcastStatus(vfo, s.Status, stoppingToken), vfo);
                        break;

                    case ErrorReportMsg er:
                        _logger.LogWarning("SDR {Vfo}: worker error — {Error}", vfo, er.Error);
                        Dispatch(BroadcastDetail(vfo, er.Error, stoppingToken), vfo);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown / restart request.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SDR {Vfo}: session error — {Message}", vfo, ex.Message);
            await BroadcastStatus(vfo, "disconnected", stoppingToken).ConfigureAwait(false);
            await BroadcastDetail(vfo, ex.Message, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            WriterSlot? removedSlot = null;
            lock (_activeLock)
            {
                _activeDeviceKeys.Remove(vfo);
                if (_writers.Remove(vfo, out var slot)) removedSlot = slot;
            }
            removedSlot?.Lock.Dispose();
            try { client?.Close(); } catch { }
            if (worker != null)
            {
                await worker.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                worker.Dispose();
            }
        }
    }

    /// <summary>
    /// Push a live DSP-knob update (gain + dB clamp) to the worker holding
    /// the given VFO. Returns false if no worker is currently connected for
    /// that VFO (settings should still be persisted by the caller so they
    /// get re-sent on next session start).
    /// </summary>
    public async Task<bool> TrySendDspSettingsAsync(
        string vfo,
        DspSettingsPayload settings,
        CancellationToken ct = default)
    {
        WriterSlot? slot;
        lock (_activeLock) _writers.TryGetValue(vfo, out slot);
        if (slot == null) return false;

        // Serialise writes per VFO so back-to-back slider POSTs can't
        // interleave bytes on the worker's NetworkStream. The semaphore
        // is disposed in RunSessionAsync's finally block; if disposal
        // races with us, the WaitAsync throws ObjectDisposedException
        // and we return false — the caller treats this the same as "no
        // worker connected", which is accurate at that point.
        try { await slot.Lock.WaitAsync(ct).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return false; }
        try
        {
            await slot.Writer.WriteDspSettingsAsync(settings, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDR {Vfo}: failed to push DSP settings — {Message}", vfo, ex.Message);
            return false;
        }
        finally
        {
            try { slot.Lock.Release(); } catch (ObjectDisposedException) { /* session ended mid-write */ }
        }
    }

    /// <summary>
    /// Change the span of a running worker without restarting it. Only
    /// possible when the new span's plan runs the hardware exactly as the
    /// worker already does (see SpectrumSpanPlan) — in practice, between two
    /// software-zoom spans — so the change is one ViewWindow message.
    /// Returns false when a restart is needed instead — the caller has
    /// already persisted the new span, so RequestRestart picks it up.
    /// </summary>
    public async Task<bool> TrySetSpanAsync(string vfo, double spanHz, CancellationToken ct = default)
    {
        WriterSlot? slot;
        lock (_activeLock) _writers.TryGetValue(vfo, out slot);
        if (slot == null) return false;

        var config = await _settings.GetSettingsAsync().ConfigureAwait(false);
        var plan   = SpectrumSpanPlan.For(spanHz, slot.WideFftSize, DialCentreHz(vfo, config));
        if (!plan.SameHardwareAs(slot.Plan)) return false;

        return await PushViewAsync(vfo, slot, plan, $"span {spanHz} Hz", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Move a running worker's crop window to wherever the dial now sits in
    /// the stream, after a filter change on the radio. A no-op when the
    /// worker sends the whole stream, when the centre has not moved, or
    /// when there is no worker.
    /// </summary>
    private async Task RecentreViewAsync(string vfo)
    {
        try
        {
            WriterSlot? slot;
            lock (_activeLock) _writers.TryGetValue(vfo, out slot);
            if (slot == null || !slot.Plan.Crops) return;

            var  config = await _settings.GetSettingsAsync().ConfigureAwait(false);
            long centre = DialCentreHz(vfo, config);
            if (centre == slot.Plan.ViewCentreHz) return;

            await PushViewAsync(vfo, slot, slot.Plan with { ViewCentreHz = centre }, "dial moved").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDR {Vfo}: failed to recentre view — {Message}", vfo, ex.Message);
        }
    }

    /// <summary>Send one ViewWindow for <paramref name="plan"/> and record it in the slot.</summary>
    private async Task<bool> PushViewAsync(string vfo, WriterSlot slot, SpectrumSpanPlan.Plan plan, string why, CancellationToken ct = default)
    {
        var view = new ViewWindowPayload(plan.ViewCentreHz, plan.ViewSpanHz);

        try { await slot.Lock.WaitAsync(ct).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return false; }
        try
        {
            await slot.Writer.WriteViewWindowAsync(view, ct).ConfigureAwait(false);
            lock (_activeLock)
                if (_writers.TryGetValue(vfo, out var current) && ReferenceEquals(current, slot))
                    _writers[vfo] = slot with { Plan = plan };
            _logger.LogInformation("SDR {Vfo}: {Why} applied live (view {Centre} Hz ±{Half} Hz)",
                vfo, why, view.CentreHz, view.SpanHz / 2);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDR {Vfo}: failed to push view window — {Message}", vfo, ex.Message);
            return false;
        }
        finally
        {
            try { slot.Lock.Release(); } catch (ObjectDisposedException) { /* session ended mid-write */ }
        }
    }

    private static async Task<TcpClient> ConnectToWorkerAsync(int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(WorkerConnectTimeoutMs);
        Exception? lastEx = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var c = new TcpClient();
                await c.ConnectAsync("127.0.0.1", port, ct).ConfigureAwait(false);
                return c;
            }
            catch (SocketException ex) { lastEx = ex; }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Could not connect to SDR worker on localhost:{port} within {WorkerConnectTimeoutMs}ms",
            lastEx);
    }

    // ── SignalR helpers — sdrId-tagged for per-VFO routing on the frontend ────

    // Fire-and-forget a hub broadcast so the worker read loop never blocks on
    // client I/O. Faults are expected here — a slow or aborted client's send will
    // throw — so we observe and swallow them rather than leaving the Task
    // unobserved. Frames and status are real-time and disposable; a dropped one
    // is corrected by the next.
    private void Dispatch(Task sendTask, string vfo)
    {
        _ = sendTask.ContinueWith(
            t => _logger.LogDebug(t.Exception, "SDR {Vfo}: hub broadcast dropped", vfo),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task BroadcastFrame(string vfo, SpectrumFrameMsg sf, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync(
                "RadioStateUpdate",
                new
                {
                    property = "SpectrumUpdate",
                    value    = new { sdrId = vfo, bins = sf.Bins, centreHz = sf.CentreHz, spanHz = sf.SpanHz },
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SDR {Vfo}: spectrum frame broadcast dropped", vfo);
        }
    }

    private async Task BroadcastStatus(string vfo, string status, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync(
                "RadioStateUpdate",
                new { property = "SdrStatus", value = new { sdrId = vfo, status } },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SDR {Vfo}: failed to broadcast status '{Status}'", vfo, status);
        }
    }

    private async Task BroadcastDetail(string vfo, string error, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync(
                "RadioStateUpdate",
                new { property = "SdrError", value = new { sdrId = vfo, error } },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }
    }
}
