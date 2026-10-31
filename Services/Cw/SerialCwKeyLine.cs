using System.IO.Ports;
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
    /// Windows only for now: the caller checks.
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
        /// The COM ports with Windows's names for them, e.g. "Silicon Labs Dual
        /// CP2105 USB to UART Bridge: Standard COM Port (COM3)", so the page
        /// can say which is the radio's Standard port. Ports with no name in
        /// the registry are listed by name only.
        /// </summary>
        public static List<(string Name, string Description)> Ports()
        {
            var names = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var described = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                .OrderBy(n => int.TryParse(n.AsSpan(3), out int k) ? k : int.MaxValue)
                .Select(n => (n, described.TryGetValue(n, out var d) ? d : n))
                .ToList();
        }
    }
}
