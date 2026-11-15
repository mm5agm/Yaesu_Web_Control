// Worker host — the runtime loop for one SDR.
//
// Lifecycle:
//   1. Open a TCP listener on the chosen port (localhost only).
//   2. Wait for YWC main to connect (single client).
//   3. Open the SDR device (sdrplay or soapy).
//   4. Configure it (IF freq, sample rate, hop size).
//   5. Start streaming.
//   6. Loop: TryReadIqFrame → overlap window → FFT → crop → frame-write.
//   7. On client disconnect, cancellation, or SDR error: clean up and exit.
//
// Designed for one-shot use. If anything goes wrong, the process exits with
// a non-zero code; YWC's SdrManager (step 2 of the dual-SDR work) supervises
// and restarts. Keeping the worker dumb keeps the supervisor sensible.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using RadioWebControl.Core.Services.Spectrum;
using Yaesu_Web_Control.Services.Sdr;

namespace Yaesu_Web_Control.Workers.Sdr;

internal sealed class WorkerHost
{
    private readonly WorkerOptions     _opts;
    private readonly SpectrumProcessor _processor = new();

    // The slice of the FFT that goes on the wire, or null for the whole
    // stream. Written by the control reader's thread, read by the streaming
    // loop; a stale read costs one frame at the old window, so a volatile
    // reference to an immutable object is enough (a nullable struct cannot
    // be volatile, hence the tiny class).
    private sealed record ViewWindow(long CentreHz, long SpanHz);
    private volatile ViewWindow? _view;

    // Most bins one frame may carry. A browser canvas is at most a couple of
    // thousand pixels wide, and every bin is a JSON number in a SignalR
    // message to every client, so a 16k-point FFT must be thinned before it
    // leaves here. SpectrumZoom keeps the peak of each group, so a CW carrier
    // survives the thinning.
    private const int MaxWireBins = 2048;

    public WorkerHost(WorkerOptions opts) => _opts = opts;

    public async Task<int> RunAsync(CancellationToken stoppingToken)
    {
        if (_opts.ViewSpanHz > 0)
            _view = new ViewWindow(_opts.ViewCentreHz, _opts.ViewSpanHz);

        Log($"starting (deviceKey={_opts.DeviceKey}, vfo={_opts.Vfo}, port={_opts.Port}, " +
            $"ifHz={_opts.IfFrequencyHz}" + (_opts.TrimHz != 0 ? $"{_opts.TrimHz:+#;-#} trim" : "") +
            $", sr={_opts.SampleRateHz}, fft={_opts.FftSize}, hop={_opts.HopSize}" +
            (_view is { } v0 ? $", view={v0.CentreHz}±{v0.SpanHz / 2}" : "") + ")");

        // 1. Open TCP listener on localhost only.
        var listener = new TcpListener(IPAddress.Loopback, _opts.Port);
        try { listener.Start(); }
        catch (SocketException ex)
        {
            Log($"FATAL: could not bind localhost:{_opts.Port} — {ex.Message}");
            return 2;
        }
        Log($"listening on localhost:{_opts.Port}, waiting for client…");

        TcpClient? client;
        try
        {
            client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("cancelled before client connected — exiting cleanly");
            listener.Stop();
            return 0;
        }
        finally
        {
            // Stop accepting further connections — single-client design.
            listener.Stop();
        }
        Log($"client connected from {client.Client.RemoteEndPoint}");

        // 2. Open the SDR and stream.
        var stream = client.GetStream();
        var writer = new FrameWriter(stream);
        ISdrDevice? device = null;
        int exit = 0;

        // Background reader for main → worker DSP knob updates. Runs for the
        // lifetime of the connection; exceptions are logged but don't kill
        // the FFT pipeline.
        var controlReader = new ControlReader(stream);
        controlReader.DspSettingsReceived += s =>
        {
            _processor.GainLinear = s.GainLinear;
            _processor.DbFloor    = s.DbFloor;
            _processor.DbCeiling  = s.DbCeiling;
            // EMA history is from the previous render scale — drop it so the
            // new clamp/gain values land cleanly without a smear-in.
            _processor.ResetSmoothing();
        };
        controlReader.ViewWindowReceived += v =>
        {
            // Span 0 means "the whole stream". The crop is applied per frame
            // from _view, so this takes effect on the next FFT with no
            // stream restart — the whole point of a software zoom.
            _view = v.SpanHz > 0 ? new ViewWindow(v.CentreHz, v.SpanHz) : null;
            Log($"view window -> {(v.SpanHz > 0 ? $"{v.CentreHz}±{v.SpanHz / 2} Hz" : "full span")}");
        };
        _ = Task.Run(async () =>
        {
            try { await controlReader.RunAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { Log($"control reader exited: {ex.Message}"); }
        }, stoppingToken);

        // The radio's own scope is not an SDR: no IQ, no FFT, no tuning. It
        // shares only the connection and the frame format with the path
        // below, so it has a loop of its own rather than a branch in that one.
        if (Ft710ScopeFrame.IsScopeKey(_opts.DeviceKey))
        {
            int rc = await RunRadioScopeAsync(writer, stoppingToken).ConfigureAwait(false);
            try { stream.Dispose(); } catch { }
            client.Dispose();
            Log($"exit code {rc}");
            return rc;
        }

        try
        {
            await writer.WriteStatusAsync("connecting", stoppingToken).ConfigureAwait(false);

            device = CreateDevice(_opts.DeviceKey);
            // The device delivers one hop of samples per read; the FFT runs
            // over a sliding window of FftSize samples that each hop advances.
            // With hop == FftSize this is exactly the old one-FFT-per-frame
            // behaviour.
            // The trim is the one place the hardware and the labels part
            // company: the dongle is tuned off the nominal IF by its own
            // crystal error, and every frame is still labelled nominal.
            device.Configure(_opts.IfFrequencyHz + _opts.TrimHz, _opts.SampleRateHz, _opts.HopSize);
            device.StartStreaming();

            await writer.WriteStatusAsync("streaming", stoppingToken).ConfigureAwait(false);
            Log($"streaming '{device.Label}'");

            float[] iqBuffer = new float[_opts.HopSize * 2];
            var     window   = new SpectrumZoom.OverlapBuffer(_opts.FftSize);
            double  hzPerBin = device.ActualSampleRateHz / _opts.FftSize;

            // Averages a few seconds of IQ once, then logs the filter shape and
            // the level of the centre bin — neither of which can be read off
            // the display, whose bins are clamped and smoothed. See
            // SpectrumProbe.cs.
            var probe = new SpectrumProbe(_opts.FftSize);

            // FrameIntervalMs is enforced by the SDR's read-availability — we
            // pull as fast as the device emits and let TryReadIqFrameAsync block.
            const int frameTimeoutMs = 200;

            // Cap what goes on the wire, not what gets computed.
            //
            // Frames are produced at sampleRate/fftSize, so a correctly-reported
            // 2 MHz span emits 1953 a second. Every one of them was being
            // broadcast to the browser as a separate SignalR message carrying
            // 1024 numbers; SdrManager deliberately fire-and-forgets those so the
            // read loop never blocks, which means nothing upstream was applying
            // back-pressure and the client simply drowned.
            //
            // ComputeSpectrum still runs on every frame. That is the point: its
            // EMA then averages the whole IQ stream rather than the sparse subset
            // we happen to send, so throttling here improves the trace instead of
            // thinning it. The cost is ~20 us of FFT per frame — under 5% of one
            // core even at the widest span.
            const int maxSendsPerSecond = 25;

            // Which FFTs go out is decided by signal time, not the PC's clock:
            // one per 1/25 s of samples. The SDRplay API hands IQ over in
            // bunches (about three hops every 100 ms at 125 kHz), so a clock
            // gate could pass only one or two of each bunch and the trace
            // jumped about ten times a second. The chosen frames go to a
            // sender that spaces them evenly. Its queue holds three; if it
            // ever fills, the oldest goes, so the delay stays bounded.
            long samplesPerSend   = Math.Max(1L, (long)(device.ActualSampleRateHz / maxSendsPerSecond));
            long samplesSinceSend = samplesPerSend;
            var  outbox = Channel.CreateBounded<SpectrumZoom.View>(new BoundedChannelOptions(3)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
            using var sendStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task sender = SendSpacedAsync(writer, outbox.Reader, sendStop.Token);

            while (!stoppingToken.IsCancellationRequested && !sender.IsCompleted)
            {
                bool got = await device.TryReadIqFrameAsync(iqBuffer, frameTimeoutMs, stoppingToken)
                    .ConfigureAwait(false);
                if (!got) continue;

                window.Push(iqBuffer);
                if (!window.IsFull) continue;

                float[] bins = _processor.ComputeSpectrum(window.Window, _opts.FftSize);

                // After ComputeSpectrum, so the probe subtracts the same DC
                // estimate the display just used and therefore measures the
                // corrected stream rather than the raw one.
                var (dcI, dcQ) = _processor.DcEstimate;
                if (probe.Add(window.Window, dcI, dcQ))
                    foreach (string line in probe.Format(device.ActualSampleRateHz, _opts.IfFrequencyHz))
                        Log(line);

                samplesSinceSend += _opts.HopSize;
                if (samplesSinceSend < samplesPerSend) continue;
                samplesSinceSend = Math.Min(samplesSinceSend - samplesPerSend, samplesPerSend);

                // Cut the wanted window out of the full spectrum, or thin the
                // full spectrum to the wire cap when no window is set. Either
                // way the frame carries the centre and width of what it
                // actually holds, so the browser draws its axis from that.
                long tuneHz   = _opts.IfFrequencyHz;
                long fullSpan = (long)device.ActualSampleRateHz;
                var  view     = _view;
                SpectrumZoom.View cut = view is { } w
                    ? SpectrumZoom.Crop(bins, hzPerBin, tuneHz, w.CentreHz, w.SpanHz, MaxWireBins)
                    : bins.Length <= MaxWireBins
                        ? new SpectrumZoom.View(bins, tuneHz, fullSpan)
                        : SpectrumZoom.Crop(bins, hzPerBin, tuneHz, tuneHz, fullSpan, MaxWireBins);

                // A copy, because the frame now waits in the queue while the
                // next FFT is computed. Rounded to 1 dp (same precision as the
                // SignalR path, keeps frames small).
                float[] wire = new float[cut.Bins.Length];
                for (int i = 0; i < wire.Length; i++)
                    wire[i] = MathF.Round(cut.Bins[i], 1);

                outbox.Writer.TryWrite(cut with { Bins = wire });
            }

            outbox.Writer.TryComplete();
            try { await sender.ConfigureAwait(false); }
            catch (IOException ex) { Log($"client disconnected: {ex.Message}"); }
            catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException)
        {
            Log("cancelled — exiting cleanly");
        }
        catch (DllNotFoundException ex)
        {
            Log($"FATAL: required DLL not found — {ex.Message}");
            try { await writer.WriteStatusAsync("nodll", CancellationToken.None); } catch { }
            try { await writer.WriteErrorAsync(ex.Message, CancellationToken.None); } catch { }
            exit = 3;
        }
        catch (Exception ex)
        {
            Log($"FATAL: streaming error — {ex.GetType().Name}: {ex.Message}");
            try { await writer.WriteStatusAsync("disconnected", CancellationToken.None); } catch { }
            try { await writer.WriteErrorAsync(ex.Message, CancellationToken.None); } catch { }
            exit = 4;
        }
        finally
        {
            try { device?.Stop(); } catch { }
            device?.Dispose();
            try { stream.Dispose(); } catch { }
            client.Dispose();
        }

        Log($"exit code {exit}");
        return exit;
    }

    // How long the first FT4222 open may take before it is given up. Nexus
    // saw one hang for minutes on a bridge that had just appeared; every
    // later open was instant. Giving up exits the worker, and SdrManager
    // starts a fresh one after its usual retry pause.
    private const int ScopeOpenTimeoutMs = 20_000;

    // The FT-710 produces about 84 frames a second. The panel needs far
    // fewer, and every frame sent is a SignalR message to every browser.
    private const int ScopeSendsPerSecond = 20;

    /// <summary>
    /// Streams the FT-710's own scope from its internal FT4222. Each frame
    /// goes out as a SpectrumFrame whose centre and span are ZERO: the worker
    /// has no CAT, so it cannot know where the row sits. SdrManager fills
    /// both in from the radio's scope settings before anything reaches a
    /// browser. Bins are in the radio's order, low to high frequency.
    /// </summary>
    /// <summary>
    /// Sends queued spectrum frames at most one per 38 ms, on a running
    /// schedule, so frames computed in a bunch reach the browser spread out.
    /// The schedule (not "38 ms after the last send") keeps the average right
    /// even though Task.Delay on Windows can overshoot by up to ~15 ms.
    /// </summary>
    private static async Task SendSpacedAsync(FrameWriter writer, ChannelReader<SpectrumZoom.View> frames, CancellationToken ct)
    {
        long  spacing  = Stopwatch.Frequency * 38 / 1000;
        long  slack    = Stopwatch.Frequency * 16 / 1000;
        var   clock    = Stopwatch.StartNew();
        long  next     = 0;
        ulong sequence = 0;
        await foreach (var cut in frames.ReadAllAsync(ct).ConfigureAwait(false))
        {
            long now = clock.ElapsedTicks;
            if (next > now)
                await Task.Delay(TimeSpan.FromSeconds((double)(next - now) / Stopwatch.Frequency), ct).ConfigureAwait(false);
            // Never closer than spacing less the timer's overshoot, so a bunch
            // can't go out together, and never drifting late on overshoot.
            next = Math.Max(next + spacing, clock.ElapsedTicks + spacing - slack);
            await writer.WriteSpectrumAsync(++sequence, cut.CentreHz, cut.SpanHz, cut.Bins, ct).ConfigureAwait(false);
        }
    }

    private async Task<int> RunRadioScopeAsync(FrameWriter writer, CancellationToken ct)
    {
        Ft4222Bridge? bridge = null;
        try
        {
            await writer.WriteStatusAsync("connecting", ct).ConfigureAwait(false);

            bridge = await Task.Run(() => Ft4222Bridge.Open(Log), ct)
                .WaitAsync(TimeSpan.FromMilliseconds(ScopeOpenTimeoutMs), ct).ConfigureAwait(false);
            Log($"FT4222 open: '{bridge.Description}'");
            await writer.WriteStatusAsync("streaming", ct).ConfigureAwait(false);

            var   assembler = new Ft710ScopeFrame.Assembler();
            long  interval  = Stopwatch.Frequency / ScopeSendsPerSecond;
            var   clock     = Stopwatch.StartNew();
            long  lastSend  = -interval;
            ulong sequence  = 0;
            int   misses    = 0;

            while (!ct.IsCancellationRequested)
            {
                // Pace the reads rather than drain all 84 frames a second:
                // chip-select is released after each read (see Ft4222Bridge),
                // so a pause costs nothing and the next read resynchronises
                // on the trailer.
                long wait = lastSend + interval - clock.ElapsedTicks;
                if (wait > 0)
                    await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), ct).ConfigureAwait(false);

                byte[]? frame = assembler.Push(bridge.Read());
                if (frame is null)
                {
                    // One miss is the stream's rotation; a long run of them
                    // is a stream that is not FT-710 scope data at all.
                    if (++misses == 50)
                        Log("no frame trailer in 50 reads — is SCU-LAN10 ON and is this an FT-710?");
                    continue;
                }
                misses = 0;
                lastSend = clock.ElapsedTicks;

                float[] bins = Ft710ScopeFrame.ParseMainRowDb(frame);
                try
                {
                    await writer.WriteSpectrumAsync(++sequence, 0, 0, bins, ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    Log($"client disconnected: {ex.Message}");
                    break;
                }
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Log("cancelled — exiting cleanly");
            return 0;
        }
        catch (DllNotFoundException ex)
        {
            Log($"FATAL: FTDI library not found — {ex.Message}");
            try { await writer.WriteStatusAsync("noft4222", CancellationToken.None); } catch { }
            try { await writer.WriteErrorAsync(Ft4222Libraries.MissingMessage, CancellationToken.None); } catch { }
            return 3;
        }
        catch (TimeoutException)
        {
            Log($"FATAL: FT4222 open did not finish in {ScopeOpenTimeoutMs / 1000} s");
            try { await writer.WriteStatusAsync("disconnected", CancellationToken.None); } catch { }
            try { await writer.WriteErrorAsync("The FT-710 scope did not open in time. Retrying.", CancellationToken.None); } catch { }
            return 4;
        }
        catch (Exception ex)
        {
            Log($"FATAL: FT-710 scope error — {ex.GetType().Name}: {ex.Message}");
            try { await writer.WriteStatusAsync("disconnected", CancellationToken.None); } catch { }
            try { await writer.WriteErrorAsync(ex.Message, CancellationToken.None); } catch { }
            return 4;
        }
        finally
        {
            bridge?.Dispose();
        }
    }

    private ISdrDevice CreateDevice(string key)
    {
        if (key.StartsWith(SdrplayDevice.KeyPrefix, StringComparison.OrdinalIgnoreCase))
            return new SdrplayDevice(key);

        // Everything else is treated as a SoapySDR kwargs string. Opening it
        // makes SoapySDR load the plugin for its driver, so the shipped
        // runtime DLLs go in first — see PreloadSoapySdrRuntime (#164).
        foreach (var line in SdrplayDllResolver.PreloadSoapySdrRuntime())
            Log($"preload {line}");
        return new SoapySdrDevice(key);
    }

    // Log to stderr with a [SdrWorker A] prefix so YWC's Serilog file-sink (step 2)
    // can route worker output into the main log cleanly.
    private void Log(string msg) =>
        Console.Error.WriteLine($"[SdrWorker {_opts.Vfo}] {DateTime.UtcNow:HH:mm:ss.fff}  {msg}");
}

internal sealed record WorkerOptions(
    string DeviceKey,
    string Vfo,            // "A" or "B"
    int    Port,           // localhost TCP port to listen on
    long   IfFrequencyHz,
    int    TrimHz,         // hardware tune offset from IfFrequencyHz; frames stay labelled IfFrequencyHz
    double SampleRateHz,
    int    FftSize,
    int    HopSize,        // samples read per FFT; == FftSize means no overlap
    long   ViewCentreHz,   // initial crop window; 0/0 sends the whole span
    long   ViewSpanHz);
