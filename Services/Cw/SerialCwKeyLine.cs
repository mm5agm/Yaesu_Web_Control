using System.IO.Ports;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RadioWebControl.Core.Services.Cw;

namespace Yaesu_Web_Control.Services.Cw
{
    /// <summary>
    /// Keys the radio by DTR on its Standard COM port: the radio's PC KEYING
    /// menu set to DTR treats that line as a straight key. Never the CAT port
    /// (the Enhanced port), which YWC holds open with DTR raised; the service
    /// refuses it by name.
    ///
    /// The port opens with DTR and RTS low, and is set low again straight
    /// after opening in case the driver raised it. Closing sets DTR low first.
    /// </summary>
    public sealed class SerialCwKeyLine : ICwKeyLine, IDisposable
    {
        private readonly SerialPort _port;

        public SerialCwKeyLine(string portName)
        {
            _port = new SerialPort(portName)
            {
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
            };
            _port.Open();
            _port.DtrEnable = false;
            _port.RtsEnable = false;
        }

        public string PortName => _port.PortName;

        /// <summary>Throws if the port has gone (cable pulled); the service lets go and drops back to the dry run.</summary>
        public void SetKey(bool down) => _port.DtrEnable = down;

        public void Dispose()
        {
            try { _port.DtrEnable = false; } catch { /* the port may already be gone */ }
            _port.Dispose();
        }

        /// <summary>
        /// The serial ports, each with the best name the OS gives it, so the
        /// page can say which is the radio's Standard port:
        /// - Windows: the registry's name, e.g. "Silicon Labs Dual CP2105 USB
        ///   to UART Bridge: Standard COM Port (COM3)".
        /// - Linux: from udev's /dev/serial/by-id links (see <see cref="DescribeById"/>).
        /// - macOS: each port appears twice, as /dev/tty.* and /dev/cu.*.
        ///   Opening tty.* can block waiting for carrier detect, so only the
        ///   cu.* ones are offered (Fabio, D#274), by name.
        /// Ports with no better name are listed by name only.
        /// </summary>
        public static List<(string Name, string Description)> Ports()
        {
            var names = SerialPort.GetPortNames().Distinct(StringComparer.Ordinal).ToList();
            var described = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (OperatingSystem.IsMacOS())
                names = names.Where(n => n.StartsWith("/dev/cu.", StringComparison.Ordinal)).ToList();
            else if (OperatingSystem.IsLinux())
                described = LinuxNames();
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                    foreach (var device in usb?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        using var dev = usb!.OpenSubKey(device);
                        foreach (var instance in dev?.GetSubKeyNames() ?? Array.Empty<string>())
                        {
                            using var inst = dev!.OpenSubKey(instance);
                            using var parms = inst?.OpenSubKey("Device Parameters");
                            if (parms?.GetValue("PortName") is string port && inst?.GetValue("FriendlyName") is string friendly)
                                described[port] = friendly;
                        }
                    }
                }
                catch
                {
                    // Names are a help, not a need: list the ports without them.
                }
            }
            return names
                .OrderBy(n => n.TrimEnd("0123456789".ToCharArray()), StringComparer.Ordinal)
                .ThenBy(n => int.TryParse(n.AsSpan(n.TrimEnd("0123456789".ToCharArray()).Length), out int k) ? k : -1)
                .Select(n => (n, described.TryGetValue(n, out var d) ? d : n))
                .ToList();
        }

        /// <summary>
        /// Where a port name really leads: a /dev/serial/by-id link becomes
        /// the ttyUSB node it points at, so one port set two ways is seen as
        /// one. Windows names are returned as they are.
        /// </summary>
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

        // Linux names ports only ttyUSB0, ttyUSB1...; udev's by-id links say
        // what each one is.
        private static Dictionary<string, string> LinuxNames()
        {
            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var link in Directory.GetFiles("/dev/serial/by-id"))
                {
                    var port = new FileInfo(link).ResolveLinkTarget(true)?.FullName;
                    if (port is not null) named[port] = DescribeById(Path.GetFileName(link), port);
                }
            }
            catch
            {
                // No udev links (a container without /dev/serial, say): names only.
            }
            return named;
        }

        /// <summary>
        /// A name for the page from a udev by-id link name, which is
        /// usb-{maker}_{product}_{serial number}-if{interface}-port{n}, e.g.
        /// usb-Silicon_Labs_CP2105_Dual_USB_to_UART_Bridge_Controller_01234567-if01-port0.
        /// A CP2105 (the FTdx101, FTdx10 and FT-710 have one) gives the radio
        /// two ports: interface 0 is its Enhanced port, which carries CAT, and
        /// interface 1 its Standard port, which is the one to key.
        /// </summary>
        public static string DescribeById(string id, string port)
        {
            var m = Regex.Match(id, @"^usb-(.+)-if(\d+)(?:-port\d+)?$");
            if (!m.Success) return port;
            string device = m.Groups[1].Value;
            int cut = device.LastIndexOf('_');
            if (cut > 0) device = device[..cut];   // the serial number
            device = device.Replace('_', ' ');
            int iface = int.Parse(m.Groups[2].Value);
            string role = device.Contains("CP2105", StringComparison.OrdinalIgnoreCase) && iface <= 1
                ? (iface == 0 ? ": Enhanced port" : ": Standard port")
                : $", interface {iface}";
            return $"{device}{role} ({port})";
        }
    }
}
