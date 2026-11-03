using System.IO.Ports;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// The byte link under CatMultiplexerService: the radio's COM port, or the
    /// simulated radio in stub mode (see StubRadio). Everything above this line
    /// speaks CAT text, so swapping the link is the whole of stub mode.
    /// </summary>
    public interface ICatTransport : IDisposable
    {
        string PortName { get; }
        int BaudRate { get; }
        bool IsOpen { get; }
        int BytesToRead { get; }

        /// <summary>Raised with whatever text has arrived, on a background thread.</summary>
        event Action<string>? DataReceived;

        void Open();
        string ReadExisting();
        void Write(byte[] buffer, int offset, int count);

        /// <summary>Drops DTR/RTS, discards both buffers and closes.</summary>
        void Close();
    }

    public sealed class SerialCatTransport : ICatTransport
    {
        private readonly SerialPort _port;

        public SerialCatTransport(string portName, int baudRate)
        {
            _port = new SerialPort
            {
                PortName = portName,
                BaudRate = baudRate,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.Two,
                Handshake = Handshake.None,
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = true
            };
            _port.DataReceived += (s, e) => DataReceived?.Invoke(_port.ReadExisting());
        }

        public string PortName => _port.PortName;
        public int BaudRate => _port.BaudRate;
        public bool IsOpen => _port.IsOpen;
        public int BytesToRead => _port.BytesToRead;

        public event Action<string>? DataReceived;

        public void Open() => _port.Open();
        public string ReadExisting() => _port.ReadExisting();
        public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);

        public void Close()
        {
            _port.DtrEnable = false;
            _port.RtsEnable = false;
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
            _port.Close();
        }

        public void Dispose() => _port.Dispose();
    }
}
