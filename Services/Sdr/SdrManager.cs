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

using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Sdr;

public sealed class SdrManager : BackgroundService
{
    private readonly ISettingsService             _settings;
    private readonly IHubContext<RadioHub>        _hub;
    private readonly ILogger<SdrManager>          _logger;

    private const int RetryDelayMs           = 5_000;
    private const int UnconfiguredPollMs     = 10_000;
    // The worker caps sends at 25/s (see WorkerHost), so this is ~3 s. It used
    // to read "~3 s at 10 fps", which no frame rate the worker ever produced
    // actually matched — the rate is set by the IQ rate over the FFT size.
    private const int StatusHeartbeatFrames  = 75;     // ~3 s at the 25/s send cap
    private const int WorkerConnectTimeoutMs = 10_000;

    private CancellationTokenSource _restartCts = new();

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

    private sealed record WriterSlot(FrameWriter Writer, SemaphoreSlim Lock);

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
        ILogger<SdrManager>          logger)
    {
        _settings = settings;
        _hub      = hub;
        _logger   = logger;
    }

    /// <summary>
    /// Cancels the current sessions so they restart with fresh settings.
    /// Triggered when the user saves SDR-related settings.
    /// </summary>
    public void RequestRestart()
    {
        var old = Interlocked.Exchange(ref _restartCts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config       = await _settings.GetSettingsAsync().ConfigureAwait(false);
            var restartToken = _restartCts.Token;
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, restartToken);

            bool aConfigured = !string.IsNullOrWhiteSpace(config.SdrDeviceKeyA);
            bool bConfigured = !string.IsNullOrWhiteSpace(config.SdrDeviceKeyB);

            if (!aConfigured && !bConfigured)
            {
                await BroadcastStatus("A", "unconfigured", stoppingToken).ConfigureAwait(false);
                await BroadcastStatus("B", "unconfigured", stoppingToken).ConfigureAwait(false);
                try { await Task.Delay(UnconfiguredPollMs, sessionCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                continue;
            }

            // Tell the frontend explicitly when one side is unconfigured so the
            // corresponding panel can clear itself rather than show a stale state.
            if (!aConfigured) await BroadcastStatus("A", "unconfigured", stoppingToken).ConfigureAwait(false);
            if (!bConfigured) await BroadcastStatus("B", "unconfigured", stoppingToken).ConfigureAwait(false);

            // Run any configured sessions concurrently.
            var tasks = new List<Task>(2);
            if (aConfigured)
                tasks.Add(RunSessionAsync("A", config.SdrDeviceKeyA!, config, sessionCts.Token));
            if (bConfigured)
                tasks.Add(RunSessionAsync("B", config.SdrDeviceKeyB!, config, sessionCts.Token));

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Skip retry delay if a restart was requested (e.g. settings changed).
            if (!stoppingToken.IsCancellationRequested && !restartToken.IsCancellationRequested)
                await Task.Delay(RetryDelayMs, stoppingToken).ConfigureAwait(false);
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

            // Per-VFO sample rate so each panel can run at a different span
            // (e.g. 2 MHz on the calling band, 250 kHz zoomed on the QSO).
            double sampleRateHz = vfo == "B" ? config.SdrSampleRateHzB : config.SdrSampleRateHzA;

            worker = WorkerProcess.Start(
                _logger,
                vfo:           vfo,
                deviceKey:     deviceKey,
                ifFrequencyHz: config.SdrIfFrequencyHz,
                sampleRateHz:  sampleRateHz,
                fftSize:       config.SdrFftSize);

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
            lock (_activeLock) _writers[vfo] = new WriterSlot(writer, writeLock);

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
