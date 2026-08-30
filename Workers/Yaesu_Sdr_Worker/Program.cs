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
//       --sample-rate 2000000
//       --fft-size    1024
//
// Listens on localhost:<port>, accepts one client (YWC main), opens the SDR,
// and streams FFT frames per the wire protocol in WireProtocol.cs.

using Yaesu_Web_Control.Workers.Sdr;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Resolve sdrplay_api.dll from the SDRplay install location even when
        // PATH isn't set — same fix the main YWC process applies. See
        // SdrplayDllResolver and issue #53.
        Yaesu_Web_Control.Services.Sdr.SdrplayDllResolver.Register();

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

        if (port <= 0 || port > 65535)  throw new ArgumentException("--port out of range");
        if (ifHz <= 0)                  throw new ArgumentException("--if-hz must be positive");
        if (sr <= 0)                    throw new ArgumentException("--sample-rate must be positive");
        if (fft <= 0 || (fft & (fft - 1)) != 0)
            throw new ArgumentException("--fft-size must be a positive power of two");

        return new WorkerOptions(deviceKey, vfo, port, ifHz, sr, fft);
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
            "\n" +
            "Arguments:\n" +
            "  --device-key   SDR device key (e.g. sdrplay:hw6-2405242660, or a\n" +
            "                 SoapySDR kwargs string like driver=rtlsdr,serial=…)\n" +
            "  --vfo          A or B — which VFO this SDR serves. Used only as a\n" +
            "                 log prefix to disambiguate worker output.\n" +
            "  --port         TCP port to listen on (localhost only).\n" +
            "  --if-hz        Centre/IF frequency in Hz (typically 9000000).\n" +
            "  --sample-rate  Sample rate in Hz (typically 2000000).\n" +
            "  --fft-size     FFT size — must be a power of two.\n" +
            "\n" +
            "Exit codes:\n" +
            "  0  clean shutdown (cancelled or client disconnected)\n" +
            "  1  bad command-line arguments\n" +
            "  2  could not bind the chosen TCP port\n" +
            "  3  required SDR driver DLL not found\n" +
            "  4  SDR streaming error\n");
    }
}
