// Process-supervision wrapper for one Yaesu_Sdr_Worker.exe instance.
//
// Responsibilities:
//   • Locate Yaesu_Sdr_Worker.exe (installed dir, or dev bin folder)
//   • Spawn it with command-line args
//   • Pipe its stderr into the main Serilog stream with an [SdrWorker A|B] prefix
//   • Provide a graceful Stop() that requests cancellation then kills if needed
//
// Lifetime is managed by SdrManager: one WorkerProcess per configured SDR.

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Yaesu_Web_Control.Services.Sdr;

internal sealed class WorkerProcess : IDisposable
{
    private readonly ILogger _logger;
    private readonly string  _vfo;            // "A" or "B"
    private readonly Process _process;

    public int Port  { get; }
    public Task ExitedTask => _exitedTcs.Task;
    private readonly TaskCompletionSource<int> _exitedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Start a worker. Throws if the worker exe can't be located.</summary>
    public static WorkerProcess Start(
        ILogger logger,
        string  vfo,
        string  deviceKey,
        long    ifFrequencyHz,
        int     trimHz,
        double  sampleRateHz,
        int     fftSize,
        int     hopSize,
        long    viewCentreHz,
        long    viewSpanHz)
    {
        string exePath = LocateWorkerExe()
            ?? throw new FileNotFoundException(
                "Could not find Yaesu_Sdr_Worker.exe in the expected locations. " +
                "Has the worker been built? In dev, run `dotnet build Workers/Yaesu_Sdr_Worker/Yaesu_Sdr_Worker.csproj` first.");

        int port = PickFreePort();

        var psi = new ProcessStartInfo
        {
            FileName               = exePath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("--device-key");   psi.ArgumentList.Add(deviceKey);
        psi.ArgumentList.Add("--vfo");          psi.ArgumentList.Add(vfo);
        psi.ArgumentList.Add("--port");         psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--if-hz");        psi.ArgumentList.Add(ifFrequencyHz.ToString());
        if (trimHz != 0)
        {
            psi.ArgumentList.Add("--trim-hz");  psi.ArgumentList.Add(trimHz.ToString());
        }
        psi.ArgumentList.Add("--sample-rate");  psi.ArgumentList.Add(sampleRateHz.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--fft-size");     psi.ArgumentList.Add(fftSize.ToString());
        psi.ArgumentList.Add("--hop");          psi.ArgumentList.Add(hopSize.ToString());
        if (viewSpanHz > 0)
        {
            // Software-zoom span: the worker crops each FFT to this window
            // (see SpectrumSpanPlan). Omitted for hardware-rate spans, where
            // the worker sends the whole stream.
            psi.ArgumentList.Add("--view-centre"); psi.ArgumentList.Add(viewCentreHz.ToString());
            psi.ArgumentList.Add("--view-span");   psi.ArgumentList.Add(viewSpanHz.ToString());
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var wp = new WorkerProcess(logger, vfo, process, port);

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                logger.LogInformation("{Line}", e.Data);   // worker already prefixes "[SdrWorker A] ..."
        };
        process.OutputDataReceived += (_, e) =>
        {
            // Worker only writes to stderr by design; redirect stdout in case
            // a stray Console.Write slips through.
            if (!string.IsNullOrWhiteSpace(e.Data))
                logger.LogDebug("[SdrWorker {Vfo} stdout] {Line}", vfo, e.Data);
        };
        process.Exited += (_, _) =>
        {
            logger.LogInformation("[SdrWorker {Vfo}] process exited with code {Code}", vfo, process.ExitCode);
            wp._exitedTcs.TrySetResult(process.ExitCode);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {exePath}");
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        logger.LogInformation(
            "[SdrWorker {Vfo}] spawned PID {Pid} on port {Port} for device {Key}",
            vfo, process.Id, port, deviceKey);

        return wp;
    }

    private WorkerProcess(ILogger logger, string vfo, Process process, int port)
    {
        _logger  = logger;
        _vfo     = vfo;
        _process = process;
        Port     = port;
    }

    /// <summary>
    /// Request a graceful shutdown. The worker will exit cleanly when its
    /// TCP client disconnects (which SdrManager does just before calling Stop).
    /// If the worker doesn't exit within the timeout, kill it.
    /// </summary>
    public async Task StopAsync(TimeSpan gracePeriod)
    {
        try
        {
            if (_process.HasExited) return;

            // We don't have a graceful-cancel signal from main → worker yet
            // (the wire protocol is one-way), so the convention is: SdrManager
            // closes its TCP client first, which the worker detects as
            // disconnect and exits cleanly. By the time StopAsync is called,
            // that should already have happened.
            await Task.WhenAny(_exitedTcs.Task, Task.Delay(gracePeriod));

            if (!_process.HasExited)
            {
                _logger.LogWarning("[SdrWorker {Vfo}] PID {Pid} did not exit within {Grace}ms — killing",
                    _vfo, _process.Id, (int)gracePeriod.TotalMilliseconds);
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SdrWorker {Vfo}] error during stop", _vfo);
        }
    }

    public void Dispose() => _process.Dispose();

    // ── Locating the worker exe ──────────────────────────────────────────────

    private static string? LocateWorkerExe()
    {
        // 1. Adjacent to YWC's own exe — the case for installed builds and for
        //    dev builds where a post-build step copies the worker into the
        //    YWC bin folder.
        string adjacent = Path.Combine(AppContext.BaseDirectory, "Yaesu_Sdr_Worker.exe");
        if (File.Exists(adjacent)) return adjacent;

        // 2. Walk up looking for the worker project's build output. In `dotnet run`
        //    development the layout is roughly:
        //      <repo>/bin/Debug/net10.0-windows/Yaesu_Web_Control.exe
        //      <repo>/Workers/Yaesu_Sdr_Worker/bin/{Debug,Release}/net10.0-windows/Yaesu_Sdr_Worker.exe
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 6 && dir != null; up++, dir = dir.Parent)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                string candidate = Path.Combine(
                    dir.FullName, "Workers", "Yaesu_Sdr_Worker", "bin", config, "net10.0-windows", "Yaesu_Sdr_Worker.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    // Pick a free TCP port in the worker range (17001-17099).
    //
    // The naive "scan OS-in-use ports and pick the first not-in-use" approach
    // has a race: when SdrManager spawns the A and B workers within a few ms
    // of each other, the first worker hasn't yet bound its port by the time
    // the second worker is allocated, so both end up with the same port —
    // the second worker then dies with "Only one usage of each socket address"
    // and VFO B never gets a panel.
    //
    // Fix: a process-static rolling counter under a lock guarantees each
    // worker gets a distinct port number even when called concurrently.
    // We still cross-check IPGlobalProperties to skip ports the OS already
    // sees as in use (e.g. from a previous YWC run that exited uncleanly).
    private static int _portCursor = 17000;
    private static readonly object _portCursorLock = new();

    private static int PickFreePort()
    {
        var inUse = new HashSet<int>(
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port));

        lock (_portCursorLock)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                _portCursor++;
                if (_portCursor >= 17100) _portCursor = 17001;
                if (!inUse.Contains(_portCursor)) return _portCursor;
            }
        }
        throw new InvalidOperationException("No free TCP port found in 17001-17099 for SDR worker.");
    }
}
