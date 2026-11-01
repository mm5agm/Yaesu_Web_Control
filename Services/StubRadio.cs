using System.Text;
using System.Threading.Channels;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// A simulated Yaesu on the far side of the CAT link, so the whole app runs
    /// with no radio. Set YWC_USE_STUB_RADIO=1 before starting YWC and
    /// CatMultiplexerService opens this instead of the COM port.
    ///
    /// It answers reads from a table of values and keeps every set it is sent,
    /// so a control moved in the browser reads back as moved. With AI1 it
    /// reports each set back the way the radio's auto information does. The
    /// starting values are real FTdx101MP answers taken from a detail log; the
    /// table is not a full model of any radio. A read it has no value for gets
    /// "?;", which is what a real radio answers to a command it does not know.
    ///
    /// It covers the CAT link only. The SDR spectrum, the radio's USB audio and
    /// the TFT video still need the real hardware.
    /// </summary>
    public sealed class StubRadio : ICatTransport
    {
        public const string EnvironmentVariable = "YWC_USE_STUB_RADIO";

        public static bool Enabled { get; } = string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);

        // How many characters after the two command letters name WHICH value a
        // command reads or sets: MD0 and MD1 are two values, FA is one. A command
        // with no more than that after its letters is a read, anything longer is
        // a set. Commands not listed have none (FA, PC, KS, TX, ...).
        private static readonly Dictionary<string, int> AddressLength = new()
        {
            ["AG"] = 1, ["AN"] = 1, ["BC"] = 1, ["CT"] = 1, ["GT"] = 1, ["KM"] = 1,
            ["MD"] = 1, ["ML"] = 1, ["NA"] = 1, ["NB"] = 1, ["NL"] = 1, ["NR"] = 1,
            ["PA"] = 1, ["PR"] = 1, ["RA"] = 1, ["RF"] = 1, ["RG"] = 1, ["RI"] = 1,
            ["RL"] = 1, ["RM"] = 1, ["SM"] = 1, ["SQ"] = 1, ["VT"] = 1,
            ["BP"] = 2, ["CF"] = 2, ["CN"] = 2, ["CO"] = 2, ["IS"] = 2, ["SH"] = 2, ["SS"] = 2,
            ["EX"] = 6,
        };

        // Real FTdx101MP answers (detail logs, 2026-09 and 2026-10), with the
        // VFOs moved to FT8 on 20 m and 40 m.
        private static readonly string[] Defaults =
        {
            "FA014074000", "FB007074000", "FT0", "VS0", "MD0C", "MD1C",
            "PC050", "RG0255", "RG1255", "AN01", "AN11", "RF06", "RF16",
            "GT03", "GT13", "PA00", "PA10", "RA00", "RA10", "AG0100", "AG1100", "MG030",
            "NR00", "NR10", "RL001", "RL101", "NB00", "NB10", "NL0001", "NL1001",
            "CT00", "CT10", "ML0001", "ML1050", "SH0020", "SH1020", "IS00+0000", "IS10+0000",
            "BC00", "BC10", "BP00000", "BP01232", "BP10000", "BP11150",
            "CO000000", "CO010650", "CO020000", "CO030026", "CO100000", "CO110300", "CO120000", "CO130025",
            "SQ0000", "SQ1000", "NA00", "NA10", "PR00", "PR10", "PL019",
            "VX0", "VG000", "VD08", "RS0", "RO0000", "CN00026", "CN10026",
            "KS025", "KP16", "KR0", "BI0", "SD04", "AC000", "AI0", "TX0", "ST0", "XT0", "RT0",
            "MS13", "FR00", "BY00", "RI30", "RI40", "AO050", "VT00",
            "SS0040000", "SS0130000", "SS0210000", "SS0303000", "SS04+30.0", "SS0590000",
            "SS0610000", "SS0702400", "SS0800000", "SS1120000", "SS1550000",
        };

        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>();
        private readonly Random _random = new();
        private readonly int[] _sMeter = { 60, 30 };
        private readonly bool _eightDigitFrequency;
        private bool _open;
        private bool _poweredOn = true;
        private bool _autoInformation;
        private Task? _sender;

        public StubRadio(string radioModel, string portName = "STUB", int baudRate = 38400)
        {
            PortName = portName;
            BaudRate = baudRate;
            _eightDigitFrequency = radioModel == "FTDX3000";

            foreach (var answer in Defaults)
                _values[Key(answer)] = answer;
            _values["ID"] = "ID" + ModelId(radioModel);
            if (_eightDigitFrequency)
            {
                _values["FA"] = "FA14074000";
                _values["FB"] = "FB07074000";
            }
        }

        public string PortName { get; }
        public int BaudRate { get; }
        public bool IsOpen => _open;
        public int BytesToRead => 0;

        public event Action<string>? DataReceived;

        public bool PoweredOn { get { lock (_lock) return _poweredOn; } }

        public void Open()
        {
            _open = true;
            _sender = Task.Run(SendRepliesAsync);
        }

        public string ReadExisting() => string.Empty;

        public void Write(byte[] buffer, int offset, int count)
        {
            if (!_open) throw new InvalidOperationException("The stub radio link is not open.");

            var text = Encoding.ASCII.GetString(buffer, offset, count);
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var reply in Handle(part.Trim()))
                    _outgoing.Writer.TryWrite(reply + ";");
            }
        }

        public void Close()
        {
            _open = false;
            _outgoing.Writer.TryComplete();
        }

        public void Dispose() => Close();

        /// <summary>The value a read of this command would get now, or null. For tests.</summary>
        public string? Peek(string command)
        {
            lock (_lock) return _values.TryGetValue(command, out var v) ? v : null;
        }

        // A reply leaves a few milliseconds after the command, in order, on its
        // own thread, the way the serial port's DataReceived delivers it.
        private async Task SendRepliesAsync()
        {
            try
            {
                await foreach (var reply in _outgoing.Reader.ReadAllAsync())
                {
                    await Task.Delay(3);
                    if (_open) DataReceived?.Invoke(reply);
                }
            }
            catch (Exception)
            {
                // A handler that throws must not stop the replies that follow.
            }
        }

        private IEnumerable<string> Handle(string command)
        {
            if (command.Length < 2) return Array.Empty<string>();

            var letters = command.Substring(0, 2);
            var body = command.Substring(2);

            lock (_lock)
            {
                // Switched off: the radio answers nothing until PS1.
                if (!_poweredOn)
                {
                    if (letters == "PS" && body == "1")
                        _poweredOn = true;
                    return Array.Empty<string>();
                }

                switch (letters)
                {
                    case "PS":
                        if (body.Length == 0) return new[] { "PS1" };
                        if (body == "0") _poweredOn = false;
                        return Array.Empty<string>();
                    case "AI":
                        if (body.Length == 0) return new[] { _autoInformation ? "AI1" : "AI0" };
                        _autoInformation = body == "1";
                        return Array.Empty<string>();
                    case "DT":
                        if (body == "0") return new[] { "DT0" + DateTime.Now.ToString("yyyyMMdd") };
                        if (body == "1") return new[] { "DT1" + DateTime.Now.ToString("HHmmss") };
                        if (body == "2") return new[] { "DT2+0000" };
                        return Array.Empty<string>();
                    case "SM":
                        return new[] { $"SM{Receiver(body)}{NextSMeter(Receiver(body)):D3}" };
                    case "RM":
                        return new[] { MeterReading(body) };
                    case "IF":
                        return new[] { InformationReply() };
                    case "AB":
                        Copy("FA", "FB"); Copy("MD0", "MD1");
                        return Echo("FB", "MD1");
                    case "BA":
                        Copy("FB", "FA"); Copy("MD1", "MD0");
                        return Echo("FA", "MD0");
                    case "SV":
                        Swap("FA", "FB"); Swap("MD0", "MD1");
                        return Echo("FA", "FB", "MD0", "MD1");
                }

                AddressLength.TryGetValue(letters, out var addressLength);

                if (body.Length <= addressLength)
                {
                    var key = letters + body;
                    if (_values.TryGetValue(key, out var answer)) return new[] { answer };
                    // MS; reads the pair MS13 is stored under, SH0; the SH00 value.
                    var near = _values.Keys.FirstOrDefault(k => k.StartsWith(key, StringComparison.Ordinal));
                    return near != null ? new[] { _values[near] } : new[] { "?" };
                }

                var setKey = letters + body.Substring(0, addressLength);
                _values[setKey] = command;
                return _autoInformation ? new[] { command } : Array.Empty<string>();
            }
        }

        private static string Key(string answer)
        {
            var letters = answer.Substring(0, 2);
            AddressLength.TryGetValue(letters, out var addressLength);
            return answer.Substring(0, Math.Min(answer.Length, 2 + addressLength));
        }

        private static int Receiver(string body) => body.StartsWith('1') ? 1 : 0;

        // A slow random walk around S5 so the meters move.
        private int NextSMeter(int receiver)
        {
            _sMeter[receiver] = Math.Clamp(_sMeter[receiver] + _random.Next(-12, 13), 0, 160);
            return _sMeter[receiver];
        }

        private string MeterReading(string body)
        {
            var meter = body.Length > 0 ? body[0] : '1';
            var transmitting = _values.TryGetValue("TX", out var tx) && tx != "TX0";
            var power = _values.TryGetValue("PC", out var pc) && int.TryParse(pc.AsSpan(2), out var w) ? w : 0;
            int value = meter switch
            {
                '1' => NextSMeter(0),
                '2' => NextSMeter(1),
                '5' => transmitting ? Math.Min(255, power) : 0,
                '6' => transmitting ? 20 : 0,
                '7' => transmitting ? 90 : 10,
                '8' => 190,             // VDD, about 13.8 V
                '9' => 60,              // temperature
                _ => 0,
            };
            return $"RM{meter}{value:D3}000";
        }

        private string InformationReply()
        {
            var fa = _values["FA"].Substring(2).PadLeft(9, '0');
            var mode = _values.TryGetValue("MD0", out var md) ? md[3] : '2';
            return $"IF001{fa}+000000{mode}01000";
        }

        private void Copy(string from, string to) => _values[to] = to + _values[from].Substring(from.Length);

        private void Swap(string a, string b)
        {
            var valueA = _values[a].Substring(a.Length);
            _values[a] = a + _values[b].Substring(b.Length);
            _values[b] = b + valueA;
        }

        private IEnumerable<string> Echo(params string[] keys) =>
            _autoInformation ? keys.Select(k => _values[k]).ToArray() : Array.Empty<string>();

        private static string ModelId(string radioModel) => radioModel switch
        {
            "FTdx101D" => "0681",
            "FTdx10" => "0761",
            "FT-710" => "0800",
            "FTDX3000" => "0460",
            "FTDX5000MP" or "FTDX5000D" => "0362",
            "FT-991A" => "0670",
            _ => "0682",
        };
    }
}
