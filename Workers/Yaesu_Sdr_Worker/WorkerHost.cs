// Worker host — the runtime loop for one SDR.
//
// Lifecycle:
//   1. Open a TCP listener on the chosen port (localhost only).
//   2. Wait for YWC main to connect (single client).
//   3. Open the SDR device (sdrplay or soapy).
//   4. Configure it (IF freq, sample rate, FFT size).
//   5. Start streaming.
//   6. Loop: TryReadIqFrame → FFT → frame-write to the TCP client.
//   7. On client disconnect, cancellation, or SDR error: clean up and exit.
//
// Designed for one-shot use. If anything goes wrong, the process exits with
// a non-zero code; YWC's SdrManager (step 2 of the dual-SDR work) supervises
// and restarts. Keeping the worker dumb keeps the supervisor sensible.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Yaesu_Web_Control.Services.Sdr;

namespace Yaesu_Web_Control.Workers.Sdr;

internal sealed class WorkerHost
{
    private readonly WorkerOptions     _opts;
    private readonly SpectrumProcessor _processor = new();

    public WorkerHost(WorkerOptions opts) => _opts = opts;

    public async Task<int> RunAsync(CancellationToken stoppingToken)
    {
        Log($"starting (deviceKey={_opts.DeviceKey}, vfo={_opts.Vfo}, port={_opts.Port}, " +
            $"ifHz={_opts.IfFrequencyHz}, sr={_opts.SampleRateHz}, fft={_opts.FftSize})");

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
        _ = Task.Run(async () =>
        {
            try { await controlReader.RunAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { Log($"control reader exited: {ex.Message}"); }
        }, stoppingToken);

        try
        {
            await writer.WriteStatusAsync("connecting", stoppingToken).ConfigureAwait(false);

            device = CreateDevice(_opts.DeviceKey);
            device.Configure(_opts.IfFrequencyHz, _opts.SampleRateHz, _opts.FftSize);
            device.StartStreaming();

            await writer.WriteStatusAsync("streaming", stoppingToken).ConfigureAwait(false);
            Log($"streaming '{device.Label}'");

            float[] iqBuffer = new float[_opts.FftSize * 2];
            ulong sequence = 0;

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
            long sendIntervalTicks = Stopwatch.Frequency / maxSendsPerSecond;
            long lastSendTicks = -sendIntervalTicks;
            var  sendClock = Stopwatch.StartNew();

            while (!stoppingToken.IsCancellationRequested)
            {
                bool got = await device.TryReadIqFrameAsync(iqBuffer, frameTimeoutMs, stoppingToken)
                    .ConfigureAwait(false);
                if (!got) continue;

                float[] bins = _processor.ComputeSpectrum(iqBuffer, _opts.FftSize);

                // After ComputeSpectrum, so the probe subtracts the same DC
                // estimate the display just used and therefore measures the
                // corrected stream rather than the raw one.
                var (dcI, dcQ) = _processor.DcEstimate;
                if (probe.Add(iqBuffer, dcI, dcQ))
                    foreach (string line in probe.Format(device.ActualSampleRateHz, _opts.IfFrequencyHz))
                        Log(line);

                long nowTicks = sendClock.ElapsedTicks;
                if (nowTicks - lastSendTicks < sendIntervalTicks) continue;
                lastSendTicks = nowTicks;

                // Round to 1 dp before transmission (same precision as the
                // current SignalR path, keeps frames small). After the send
                // gate, so it is not paid for frames that are never sent.
                for (int i = 0; i < bins.Length; i++)
                    bins[i] = MathF.Round(bins[i], 1);

                try
                {
                    await writer.WriteSpectrumAsync(
                        ++sequence,
                        _opts.IfFrequencyHz,
                        (long)device.ActualSampleRateHz,
                        bins,
                        stoppingToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // Client went away — clean shutdown.
                    Log($"client disconnected: {ex.Message}");
                    break;
                }
            }
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

    private static ISdrDevice CreateDevice(string key)
    {
        if (key.StartsWith(SdrplayDevice.KeyPrefix, StringComparison.OrdinalIgnoreCase))
            return new SdrplayDevice(key);

        // Everything else is treated as a SoapySDR kwargs string.
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
    double SampleRateHz,
    int    FftSize);
