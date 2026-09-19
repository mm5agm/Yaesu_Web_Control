// Runs the SoapySDR device scan in a throwaway Yaesu_Sdr_Worker.exe process
// and reads the result back, so that a scan which dies inside native code
// takes the child with it and not Yaesu Web Control.
//
// Why a separate process: SoapySDRDevice_enumerate loads every backend module
// it can find (rtlsdr, airspy, hackrf …) and each of those pulls in its own
// dependencies. A stale libusb on the PATH, or a module built against a
// different SoapySDR ABI, faults inside that call with an access violation —
// which on .NET is fatal to the whole process, no managed catch ever sees it.
// Issue #143: a user with no SDR at all lost YWC every time the Settings page
// opened, because that page scans on load. In a child, the same fault costs a
// process nobody will miss and this class reports it as a scan failure.
//
// The wire contract with the worker's --enumerate mode is the JSON shape in
// Program.cs (EnumerateResult) — change both or neither.

using System.Diagnostics;
using System.Text.Json;

namespace Yaesu_Web_Control.Services.Sdr;

internal static class SoapySdrScan
{
    /// <summary>What the child reported, or why it couldn't.</summary>
    // Diagnostics: plugin details when the scan found nothing, else null.
    // Error: null on success; otherwise "DllNotFound", "Crashed", "Timeout",
    // "NoWorker", "BadReply" or the exception type name the child caught.
    public sealed record Result(
        IReadOnlyList<SdrDeviceInfo> Devices,
        string? Diagnostics,
        string? Error,
        string? Message);

    /// <summary>Exit code Windows gives a process killed by an access violation.</summary>
    private const int AccessViolationExitCode = unchecked((int)0xC0000005);

    /// <summary>Longer than any sane scan; the rtlsdr backend can take a second or two per USB probe.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // Matches the record the worker serialises, minus the fields it never
    // sends on the success path. Case-insensitive so the worker's PascalCase
    // and any future camelCase both parse.
    private sealed record WorkerReply(bool Ok, string? Error, string? Message, SdrDeviceInfo[]? Devices, string? Diagnostics);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly System.Text.RegularExpressions.Regex AnsiEscape = new("\u001b\\[[0-9;]*m");

    // A scan that crashed is not re-run on its own for the rest of this
    // process. The Settings page scans on every open, and on a PC where the
    // scan faults that is a three-second crash and an [ERR] in the log per
    // visit, for a result that cannot change until something on the PC
    // does (#164 — six crashes in one log, no SDR on the machine at all).
    // The Scan button passes retry=true and always runs a fresh scan, so the
    // operator can still ask; only the automatic one is suppressed.
    private static readonly object CrashLock = new();
    private static Result? _crashed;

    /// <param name="retry">True when the operator asked for the scan (the Scan
    /// button); false for the automatic scan on page open, which does not
    /// repeat a scan that already crashed this session.</param>
    public static Result Run(ILogger logger, bool retry = false)
    {
        Result? earlier;
        lock (CrashLock) earlier = _crashed;
        if (earlier != null && !retry)
        {
            logger.LogInformation("SDR: SoapySDR scan not repeated — it crashed earlier this session; " +
                                  "the Scan button on the Settings page runs it again");
            return earlier with
            {
                Message = "The SoapySDR device scan crashed earlier in this session and was not run again. " +
                          "Click Scan to try again. If you do not use an SDR with this program you can ignore this.",
            };
        }

        var result = RunOnce(logger, Stopwatch.StartNew());
        lock (CrashLock) _crashed = result.Error == "Crashed" ? result : null;
        return result;
    }

    private static Result RunOnce(ILogger logger, Stopwatch sw)
    {
        string? exePath = WorkerProcess.LocateWorkerExe();
        if (exePath == null)
        {
            // Dev tree without the worker built. Streaming needs the worker
            // too, so this is never the installed case — say so rather than
            // fall back to an in-process scan that could take the app down.
            logger.LogWarning("SDR: Yaesu_Sdr_Worker.exe not found — SoapySDR scan skipped");
            return new Result([], null, "NoWorker",
                "Yaesu_Sdr_Worker.exe is missing, so the SoapySDR scan could not run. Try re-installing the application.");
        }

        var psi = new ProcessStartInfo
        {
            FileName               = exePath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        psi.ArgumentList.Add("--enumerate");

        var stderr = new List<string>();
        string stdout;
        int exitCode;
        try
        {
            using var process = new Process { StartInfo = psi };
            process.ErrorDataReceived += (_, e) =>
            {
                // SoapySDR colours its own stderr; the escape codes are noise in a log file.
                string? line = e.Data == null ? null : AnsiEscape.Replace(e.Data, "").Trim();
                if (!string.IsNullOrWhiteSpace(line)) lock (stderr) stderr.Add(line);
            };
            process.Start();
            process.BeginErrorReadLine();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                logger.LogWarning("SDR: SoapySDR scan timed out after {Seconds:0}s; stderr: {Stderr}",
                    Timeout.TotalSeconds, string.Join(" | ", stderr));
                return new Result([], null, "Timeout",
                    $"The SoapySDR device scan did not finish within {Timeout.TotalSeconds:0} seconds and was stopped.");
            }
            process.WaitForExit();   // flush the async stderr reader
            stdout   = stdoutTask.GetAwaiter().GetResult();
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SDR: could not start the SoapySDR scan process");
            return new Result([], null, ex.GetType().Name, $"Could not start the SoapySDR device scan: {ex.Message}");
        }

        // Whatever the child said before enumerating — where SoapySDR.dll
        // came from, the plugin search paths, the module files found — goes
        // in the log every time. Those are the only lines that survive when
        // the exit code below is an access violation.
        foreach (var line in stderr)
            logger.LogInformation("SDR: [scan] {Line}", line);

        if (exitCode != 0)
        {
            string how = exitCode == AccessViolationExitCode
                ? "an access violation (0xC0000005)"
                : $"exit code 0x{exitCode:X8}";
            logger.LogError("SDR: SoapySDR scan process died with {How} after {Ms} ms", how, sw.ElapsedMilliseconds);
            return new Result([], null, "Crashed",
                "The SoapySDR device scan crashed inside a native driver (" + how + "). " +
                "Yaesu Web Control itself is unaffected. If you do not use an SDR with this program you can ignore this; " +
                "otherwise the log names the SoapySDR.dll and plugin folders that were loaded, " +
                "and the Windows Event Viewer (Application log, source \"Application Error\", " +
                "Yaesu_Sdr_Worker.exe) names the file that faulted.");
        }

        WorkerReply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<WorkerReply>(stdout, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "SDR: SoapySDR scan returned unreadable output: {Stdout}", stdout);
            return new Result([], null, "BadReply", "The SoapySDR device scan returned something unreadable; see the log.");
        }
        if (reply == null)
            return new Result([], null, "BadReply", "The SoapySDR device scan returned nothing.");

        if (!reply.Ok)
            return new Result([], null, reply.Error ?? "Error", reply.Message);

        return new Result(reply.Devices ?? [], reply.Diagnostics, null, null);
    }
}
