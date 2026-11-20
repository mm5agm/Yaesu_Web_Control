using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RadioWebControl.Core.Services
{
    /// <summary>
    /// Whether a configured serial port is there, allowing for Linux's
    /// /dev/serial/by-id names.
    ///
    /// On Linux, SerialPort.GetPortNames() lists only /dev/ttyUSB0-style
    /// names, and only when that node exists. A /dev/serial/by-id/... path,
    /// which the manuals recommend because it survives the radio being
    /// power-cycled, is never in that list, so "is it in the list?" turned it
    /// away as "not found". In Docker it is worse: with only the by-id path
    /// passed in, the container has no ttyUSB node at all and the list is
    /// empty. Found 2026-11-20 checking the Docker paddle-keying docs.
    ///
    /// The caller passes the OS list in, so core needs no serial-port package.
    /// </summary>
    public static class SerialPortPaths
    {
        public const string ByIdDir = "/dev/serial/by-id";

        /// <summary>In the OS list, or on Linux a /dev path that exists.</summary>
        public static bool IsPresent(string port, IEnumerable<string> listed)
            => IsPresent(port, listed, OperatingSystem.IsLinux(), File.Exists);

        public static bool IsPresent(string port, IEnumerable<string> listed, bool linux, Func<string, bool> exists)
        {
            if (string.IsNullOrWhiteSpace(port)) return false;
            if (listed.Contains(port, StringComparer.OrdinalIgnoreCase)) return true;
            return linux && port.StartsWith("/dev/", StringComparison.Ordinal) && exists(port);
        }

        /// <summary>
        /// The OS list plus, on Linux, each /dev/serial/by-id entry that isn't
        /// already in it under its ttyUSB name. On a normal Linux host every
        /// by-id link leads to a listed node and nothing is added; in Docker
        /// the by-id entries are device nodes of their own and are the only
        /// names the radio has.
        /// </summary>
        public static List<string> WithById(IEnumerable<string> listed)
        {
            var names = listed.ToList();
            if (!OperatingSystem.IsLinux()) return names;
            string[] byId;
            try
            {
                byId = Directory.Exists(ByIdDir) ? Directory.GetFiles(ByIdDir) : Array.Empty<string>();
            }
            catch
            {
                return names;
            }
            return WithById(names, byId, RealPath);
        }

        public static List<string> WithById(IEnumerable<string> listed, IEnumerable<string> byId, Func<string, string> realPath)
        {
            var names = listed.ToList();
            var seen = new HashSet<string>(names.Select(realPath), StringComparer.Ordinal);
            foreach (var path in byId.OrderBy(p => p, StringComparer.Ordinal))
                if (seen.Add(realPath(path))) names.Add(path);
            return names;
        }

        /// <summary>Where a link leads; the path itself if it isn't one.</summary>
        public static string RealPath(string port)
        {
            if (OperatingSystem.IsWindows()) return port;
            try
            {
                return new FileInfo(port).ResolveLinkTarget(true)?.FullName ?? port;
            }
            catch
            {
                return port;
            }
        }
    }
}
