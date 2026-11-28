using System.Diagnostics;
using System.Globalization;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Which program is listening on a TCP port, for the log when one of
    /// YWC's ports is taken. Windows only (netstat -ano); empty elsewhere
    /// or if netstat can't be run.
    /// </summary>
    public static class PortOwner
    {
        /// <summary>" by MyApp.exe (process 1234)", or "" if it can't tell.</summary>
        public static async Task<string> DescribeAsync(int port)
        {
            if (!OperatingSystem.IsWindows()) return "";
            try
            {
                var psi = new ProcessStartInfo("netstat", "-ano -p TCP")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return "";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var text = await p.StandardOutput.ReadToEndAsync(cts.Token);
                var names = ListeningPids(text, port)
                    .Select(pid => ProcessName(pid) is { } name ? $"{name} (process {pid})" : $"process {pid}")
                    .ToList();
                return names.Count == 0 ? "" : " by " + string.Join(", ", names);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// The process IDs netstat -ano shows listening on <paramref name="port"/>.
        /// A line reads "  TCP    0.0.0.0:4532    0.0.0.0:0    LISTENING    1234".
        /// </summary>
        public static IReadOnlyList<int> ListeningPids(string netstat, int port)
        {
            var pids = new List<int>();
            foreach (var raw in netstat.Split('\n'))
            {
                var f = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5 || !f[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!f[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                int colon = f[1].LastIndexOf(':');
                if (colon < 0 || !int.TryParse(f[1][(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int p) || p != port) continue;
                if (int.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && !pids.Contains(pid))
                    pids.Add(pid);
            }
            return pids;
        }

        private static string? ProcessName(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.ProcessName + ".exe";
            }
            catch
            {
                return null;
            }
        }
    }
}
