// Yaesu_Sdr_Worker — one-SDR-per-process companion to Yaesu Web Control.
//
// Spawned by YWC main (via SdrManager) to hold a single SDRplay or SoapySDR
// device that YWC main can't open directly because of the SDRplay API's
// one-device-per-process limit (see docs/decisions/0001-dual-sdr-architecture.md).
//
// Usage (typically invoked by SdrManager, not the user):
//
//   Yaesu_Sdr_Worker.exe
//       --device-key  sdrplay:hw6-2405242660
//       --vfo         A
//       --port        17001
//       --if-hz       9000000
//       [--trim-hz     -44]           tune the hardware this far off --if-hz (crystal error)
//       --sample-rate 2000000
//       --fft-size    1024
//       [--hop         4096]          samples between FFTs (default = fft-size, no overlap)
//       [--view-centre 9005000]       crop the spectrum to this window before sending
//       [--view-span   2500]          (default: send the whole span)
//
// Listens on localhost:<port>, accepts one client (YWC main), opens the SDR,
// and streams FFT frames per the wire protocol in WireProtocol.cs.
//
// A second, one-shot mode:
//
//   Yaesu_Sdr_Worker.exe --enumerate
//
// runs the SoapySDR device scan and prints one JSON object to stdout (see
// EnumerateResult). YWC main uses this instead of calling SoapySDR in its own
// process because SoapySDRDevice_enumerate loads and probes every backend
// module it can find, and a bad one takes the whole process down with an
// access violation that no managed catch can stop. Issue #143: a user with no
// SDR at all lost YWC every time the Settings page opened. Here the crash
// costs a throwaway process and the scan reports "crashed" instead.

using Yaesu_Web_Control.Workers.Sdr;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Resolve sdrplay_api.dll from the SDRplay install location even when
        // PATH isn't set — same fix the main YWC process applies. See
        // SdrplayDllResolver and issue #53.
        Yaesu_Web_Control.Services.Sdr.SdrplayDllResolver.Register();

        if (args.Length == 1 && args[0].Equals("--enumerate", StringComparison.OrdinalIgnoreCase))
            return RunEnumerate();

        WorkerOptions? opts;
        try { opts = ParseArgs(args); }
        catch (ArgumentException ex)
        {
            PrintUsage(ex.Message);
            return 1;
        }
        if (opts == null) { PrintUsage(null); return 0; }   // --help path

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var host = new WorkerHost(opts);
        return await host.RunAsync(cts.Token).ConfigureAwait(false);
    }

    // Result of --enumerate, one JSON object on stdout. Field names are the
    // wire contract with SoapySdrScan in YWC main — change both or neither.
    private sealed record EnumerateResult(
        bool Ok,
        string? Error,
        string? Message,
        Yaesu_Web_Control.Services.Sdr.SdrDeviceInfo[] Devices,
        string? Diagnostics);

    private static int RunEnumerate()
    {
        // Say where SoapySDR.dll resolved from *before* enumerating, on
        // stderr, so that when the scan takes the process down the parent's
        // log still shows which copy of the DLL did it (#143).
        try
        {
            if (Yaesu_Web_Control.Services.Sdr.SdrplayDllResolver.TryResolveSoapySdr(out string? soapyPath))
                Console.Error.WriteLine($"SoapySDR.dll <- {soapyPath}");
            else
                Console.Error.WriteLine("SoapySDR.dll <- (not found next to the app; default search)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SoapySDR.dll preload: {ex.Message}");
        }

        // Likewise the plugin search paths and the module files in them,
        // *before* the enumerate — that is the call that loads each plugin,
        // and when one faults the process dies inside it, so nothing written
        // afterwards survives (#164). These three calls only list
        // directories. Console.Error is unbuffered, so the lines are on the
        // parent's pipe before the enumerate starts.
        try
        {
            foreach (var line in Yaesu_Web_Control.Services.Sdr.SoapySdrInterop.DescribePluginSearch())
                Console.Error.WriteLine(line);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SoapySDR plugin search: {ex.Message}");
        }

        EnumerateResult result;
        try
        {
            var devices = Yaesu_Web_Control.Services.Sdr.SoapySdrInterop.EnumerateDevices();
            // The plugin diagnostics are only interesting when nothing was
            // found, and they cost another round of native calls. They repeat
            // the search-path lines above and add which native DLLs the
            // enumerate actually loaded, which only exists after it returns.
            string? diag = devices.Count == 0
                ? Yaesu_Web_Control.Services.Sdr.SoapySdrInterop.GetPluginDiagnostics()
                : null;
            result = new EnumerateResult(true, null, null, devices.ToArray(), diag);
        }
        catch (DllNotFoundException ex)
        {
            result = new EnumerateResult(false, "DllNotFound", ex.Message, [], null);
        }
        catch (Exception ex)
        {
            result = new EnumerateResult(false, ex.GetType().Name, ex.Message, [], null);
        }

        Console.Out.Write(System.Text.Json.JsonSerializer.Serialize(result));
        Console.Out.Flush();
        return 0;
    }

    private static WorkerOptions? ParseArgs(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
            return null;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected token '{a}' (expected --name value).");
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {a}.");
            dict[a[2..]] = args[++i];
        }

        string Required(string name) =>
            dict.TryGetValue(name, out var v) ? v
                : throw new ArgumentException($"Missing required --{name}.");

        string deviceKey = Required("device-key");
        string vfo       = Required("vfo");
        if (vfo != "A" && vfo != "B")
            throw new ArgumentException($"--vfo must be 'A' or 'B' (got '{vfo}').");

        int    port   = int.Parse(Required("port"));
        long   ifHz   = long.Parse(Required("if-hz"));
        double sr     = double.Parse(Required("sample-rate"), System.Globalization.CultureInfo.InvariantCulture);
        int    fft    = int.Parse(Required("fft-size"));

        int  hop        = dict.TryGetValue("hop",         out var h)  ? int.Parse(h)   : fft;
        int  trimHz     = dict.TryGetValue("trim-hz",     out var t)  ? int.Parse(t)   : 0;
        long viewCentre = dict.TryGetValue("view-centre", out var vc) ? long.Parse(vc) : 0;
        long viewSpan   = dict.TryGetValue("view-span",   out var vs) ? long.Parse(vs) : 0;

        if (port <= 0 || port > 65535)  throw new ArgumentException("--port out of range");
        if (ifHz <= 0)                  throw new ArgumentException("--if-hz must be positive");
        if (Math.Abs(trimHz) > 100_000)  throw new ArgumentException("--trim-hz must be within ±100000");
        if (sr <= 0)                    throw new ArgumentException("--sample-rate must be positive");
        if (fft <= 0 || (fft & (fft - 1)) != 0)
            throw new ArgumentException("--fft-size must be a positive power of two");
        if (hop <= 0 || hop > fft)      throw new ArgumentException("--hop must be between 1 and --fft-size");
        if (viewSpan < 0 || viewCentre < 0)
            throw new ArgumentException("--view-span and --view-centre must not be negative");

        return new WorkerOptions(deviceKey, vfo, port, ifHz, trimHz, sr, fft, hop, viewCentre, viewSpan);
    }

    private static void PrintUsage(string? error)
    {
        if (error != null)
            Console.Error.WriteLine($"Error: {error}\n");

        Console.Error.WriteLine(
            "Yaesu_Sdr_Worker — one-SDR-per-process companion to Yaesu Web Control\n" +
            "\n" +
            "Usage:\n" +
            "  Yaesu_Sdr_Worker.exe --device-key KEY --vfo A|B --port N \\\n" +
            "                       --if-hz HZ --sample-rate HZ --fft-size N\n" +
            "  Yaesu_Sdr_Worker.exe --enumerate\n" +
            "\n" +
            "--enumerate runs the SoapySDR device scan in this process and prints\n" +
            "one JSON object to stdout, so a scan that crashes takes down this\n" +
            "process and not Yaesu Web Control.\n" +
            "\n" +
            "Arguments:\n" +
            "  --device-key   SDR device key (e.g. sdrplay:hw6-2405242660, or a\n" +
            "                 SoapySDR kwargs string like driver=rtlsdr,serial=…)\n" +
            "  --vfo          A or B — which VFO this SDR serves. Used only as a\n" +
            "                 log prefix to disambiguate worker output.\n" +
            "  --port         TCP port to listen on (localhost only).\n" +
            "  --if-hz        Centre/IF frequency in Hz (typically 9000000).\n" +
            "  --trim-hz      Tune the hardware this many Hz off --if-hz to correct\n" +
            "                 its crystal; every frame is still labelled --if-hz.\n" +
            "  --sample-rate  Sample rate in Hz (typically 2000000).\n" +
            "  --fft-size     FFT size — must be a power of two.\n" +
            "  --hop          Samples between successive FFTs (default: fft-size).\n" +
            "                 Smaller than fft-size overlaps FFTs for a faster display.\n" +
            "  --view-centre  With --view-span: crop each FFT to this window before\n" +
            "  --view-span    sending, so a narrow span costs no hardware retune.\n" +
            "\n" +
            "Exit codes:\n" +
            "  0  clean shutdown (cancelled or client disconnected)\n" +
            "  1  bad command-line arguments\n" +
            "  2  could not bind the chosen TCP port\n" +
            "  3  required SDR driver DLL not found\n" +
            "  4  SDR streaming error\n");
    }
}
