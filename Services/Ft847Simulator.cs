namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// A simulated FT-847 on the far side of its five-byte CAT link: the radio
    /// end of <see cref="Ft847CatTransport"/>, for the unit tests and for stub
    /// mode (YWC_USE_STUB_RADIO=1 with the FT-847 chosen in Settings).
    ///
    /// It keeps the main VFO's frequency and mode, answers 03, E7 and F7 the
    /// way Hamlib's ft847.c reads them, and ignores blocks until CAT ON, as the
    /// radio does. It is built from the manual and Hamlib, not from a real
    /// radio, so it proves the translation, not the radio.
    ///
    /// Given an FT-817, FT-818, FT-857 or FT-897 as its model, it acts as one:
    /// CAT is always on, 00 and 80 lock and unlock the front panel (recorded
    /// in <see cref="Locked"/> so the tests can prove YWC never sends them),
    /// and the meters are four bits.
    /// </summary>
    public sealed class Ft847Simulator : IFt847Port
    {
        private readonly object _lock = new();
        private readonly List<byte> _partial = new();
        private bool _open;

        public Ft847Simulator(string portName = "STUB", int baudRate = 4800, string model = "FT-847")
        {
            PortName = portName;
            BaudRate = baudRate;
            Model = model;
            if (Ft847Cat.IsFt8x7(model)) CatOn = true;
        }

        public string Model { get; }

        /// <summary>FT-8x7 only: true after a 00 (LOCK ON) block.</summary>
        public bool Locked { get; private set; }

        public string PortName { get; }
        public int BaudRate { get; }
        public bool IsOpen => _open;
        public event Action<byte[]>? BytesReceived;

        public bool CatOn { get; private set; }
        public long FrequencyHz { get; set; } = 14_074_000;
        public byte Mode { get; set; } = 0x01;
        public int SMeter { get; set; } = 9;          // 0-31 (FT-8x7: 0-15)
        public bool SquelchOpen { get; set; } = true;
        public bool Transmitting { get; set; }
        public int PowerMeter { get; set; }           // 0-31 (FT-8x7: 0-15)
        public bool AnswersReads { get; set; } = true; // false = early "FT-847UNI"

        /// <summary>Every complete block received, in order. For tests.</summary>
        public List<byte[]> Received { get; } = new();

        public void Open() => _open = true;
        public void Close() => _open = false;
        public void Dispose() => Close();

        public void Write(byte[] block)
        {
            if (!_open) throw new InvalidOperationException("The simulated FT-847 is not open.");

            var replies = new List<byte[]>();
            lock (_lock)
            {
                _partial.AddRange(block);
                while (_partial.Count >= 5)
                {
                    var b = _partial.GetRange(0, 5).ToArray();
                    _partial.RemoveRange(0, 5);
                    Received.Add(b);
                    var reply = Handle(b);
                    if (reply != null) replies.Add(reply);
                }
            }

            // A reply leaves a few milliseconds later on another thread, like a serial port.
            foreach (var reply in replies)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5);
                    if (_open) BytesReceived?.Invoke(reply);
                });
        }

        private byte[]? Handle(byte[] b)
        {
            var opcode = b[4];
            var ft8x7 = Ft847Cat.IsFt8x7(Model);
            var meterMask = ft8x7 ? 0x0F : 0x1F;
            if (ft8x7 && opcode == 0x00) { Locked = true; return null; }
            if (ft8x7 && opcode == 0x80) { Locked = false; return null; }
            if (opcode == Ft847Cat.CatOn) { CatOn = true; return null; }
            if (opcode == Ft847Cat.CatOff) { CatOn = false; return null; }
            if (!CatOn) return null;

            switch (opcode)
            {
                case Ft847Cat.SetMainFrequency:
                    var hz = Ft847Cat.FrequencyFromBcd(b);
                    if (hz != null) FrequencyHz = hz.Value;
                    return null;
                case Ft847Cat.SetMainMode:
                    Mode = b[0];
                    return null;
                case Ft847Cat.PttOff:
                    Transmitting = false;
                    return null;
                case Ft847Cat.ReadMainFrequencyAndMode:
                    if (!AnswersReads) return null;
                    var f = Ft847Cat.FrequencyBcd(FrequencyHz);
                    return new[] { f[0], f[1], f[2], f[3], Mode };
                case Ft847Cat.ReadRxStatus:
                    if (!AnswersReads) return null;
                    return new[] { (byte)((SquelchOpen ? 0 : 0x80) | (SMeter & meterMask)) };
                case Ft847Cat.ReadTxStatus:
                    if (!AnswersReads) return null;
                    return new[] { (byte)((Transmitting ? 0 : 0x80) | (PowerMeter & meterMask)) };
            }
            return null;
        }
    }
}
