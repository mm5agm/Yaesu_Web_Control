using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services
{
    /// <summary>The byte link to an FT-847: its COM port, or the simulated radio.</summary>
    public interface IFt847Port : IDisposable
    {
        string PortName { get; }
        int BaudRate { get; }
        bool IsOpen { get; }

        /// <summary>Raised with whatever bytes have arrived, on a background thread.</summary>
        event Action<byte[]>? BytesReceived;

        void Open();
        void Write(byte[] block);
        void Close();
    }

    /// <summary>
    /// The FT-847's COM port. DTR and RTS are held LOW for the whole session,
    /// unlike <see cref="SerialCatTransport"/>, which raises them: the FT-847
    /// needs neither, and on interfaces that wire them to PTT or CW key lines
    /// (the HamInterface III can) raising them would key the transmitter.
    /// </summary>
    public sealed class SerialFt847Port : IFt847Port
    {
        private readonly SerialPort _port;

        public SerialFt847Port(string portName, int baudRate)
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
                DtrEnable = false,
                RtsEnable = false
            };
            _port.DataReceived += (s, e) =>
            {
                try
                {
                    var n = _port.BytesToRead;
                    if (n <= 0) return;
                    var buffer = new byte[n];
                    var read = _port.Read(buffer, 0, n);
                    if (read > 0) BytesReceived?.Invoke(buffer[..read]);
                }
                catch (Exception)
                {
                    // Port closed under us; the transport's timeout covers it.
                }
            };
        }

        public string PortName => _port.PortName;
        public int BaudRate => _port.BaudRate;
        public bool IsOpen => _port.IsOpen;
        public event Action<byte[]>? BytesReceived;

        public void Open() => _port.Open();
        public void Write(byte[] block) => _port.Write(block, 0, block.Length);

        public void Close()
        {
            if (!_port.IsOpen) return;
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
            _port.Close();
        }

        public void Dispose() => _port.Dispose();
    }

    /// <summary>
    /// Makes an FT-847 look like a modern Yaesu to the rest of YWC. Everything
    /// above <see cref="ICatTransport"/> speaks text CAT (FA;, MD0;, SM0;), so
    /// this takes those commands, sends the FT-847's binary blocks for the few
    /// it has (see <see cref="Ft847Cat"/>), and answers in text.
    ///
    /// What it translates:
    ///   FA; / MD0;   read with 03 (one read answers both, cached briefly)
    ///   FA…; / MD0…; set with 01 / 07
    ///   SM0;         E7, S-meter bits scaled to 0-255
    ///   RM5;         F7, PO bits scaled to 0-255
    ///   TX;          F7, bit 7 clear = transmitting (answered TX2, radio-keyed)
    ///   TX0;         88, PTT off (harmless when already receiving)
    /// What it answers itself, because the FT-847 has no such command:
    ///   ID; (ID0847), PS; (PS1), AI, DT0/1/2 (the PC's clock), VS; (VS0),
    ///   FT; (FT0), and FB / MD1, which it keeps locally: the FT-847's VFO B
    ///   cannot be read or set over CAT.
    /// Every other read gets "?;", which is what a real radio answers to a
    /// command it does not know; every other set is dropped.
    ///
    /// TX1; (PTT on) is deliberately NOT translated in this first version. The
    /// FT-847's PTT ON opcode is 08; whether YWC should send it is a decision
    /// for after a tester has confirmed the reads on a real radio.
    ///
    /// The FT-847 never speaks unless spoken to, so there is no auto
    /// information. With AI1 set, this polls 03 every <c>pollMs</c> and reports
    /// a changed frequency or mode the way AI1 would on a modern radio, so the
    /// VFO knob on the radio still moves the display in the browser.
    /// </summary>
    public sealed class Ft847CatTransport : ICatTransport
    {
        private readonly IFt847Port _port;
        private readonly ILogger? _logger;
        private readonly int _replyTimeoutMs;
        private readonly int _pollMs;
        private readonly Channel<string> _commands = Channel.CreateUnbounded<string>();
        private readonly object _rxLock = new();
        private readonly List<byte> _rx = new();
        private int _expect;
        private TaskCompletionSource<byte[]>? _pending;
        private CancellationTokenSource? _cts;
        private Task? _worker;
        private bool _open;

        // State the radio cannot give us, or that is cached between reads.
        private bool _autoInformation;
        private long _fb;
        private char _mdB = '2';
        private char? _lastSetMd;
        private (long Hz, char Md)? _main;
        private DateTime _mainReadUtc = DateTime.MinValue;
        private (long Hz, char Md)? _reported;
        private int _consecutiveTimeouts;

        public Ft847CatTransport(IFt847Port port, ILogger? logger = null, int replyTimeoutMs = 120, int pollMs = 300)
        {
            _port = port;
            _logger = logger;
            _replyTimeoutMs = replyTimeoutMs;
            _pollMs = pollMs;
            _port.BytesReceived += OnBytes;
        }

        public string PortName => _port.PortName;
        public int BaudRate => _port.BaudRate;
        public bool IsOpen => _open && _port.IsOpen;
        public int BytesToRead => 0;

        public event Action<string>? DataReceived;

        public void Open()
        {
            _port.Open();
            _port.Write(Ft847Cat.Block(Ft847Cat.CatOn));
            _open = true;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WorkAsync(_cts.Token));
        }

        public string ReadExisting() => string.Empty;

        public void Write(byte[] buffer, int offset, int count)
        {
            if (!_open) throw new InvalidOperationException("The FT-847 link is not open.");
            var text = Encoding.ASCII.GetString(buffer, offset, count);
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var command = part.Trim();
                if (command.Length >= 2) _commands.Writer.TryWrite(command);
            }
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            _cts?.Cancel();
            try { _worker?.Wait(1000); } catch (AggregateException) { }
            try
            {
                if (_port.IsOpen) _port.Write(Ft847Cat.Block(Ft847Cat.CatOff));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[FT-847] CAT OFF not sent");
            }
            _port.Close();
        }

        public void Dispose()
        {
            Close();
            _port.BytesReceived -= OnBytes;
            _port.Dispose();
        }

        private async Task WorkAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                string? command = null;
                try
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(_pollMs);
                    command = await _commands.Reader.ReadAsync(wait.Token);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) return;
                }

                try
                {
                    var replies = command != null ? await HandleAsync(command) : await PollAsync();
                    foreach (var reply in replies)
                        if (_open) DataReceived?.Invoke(reply + ";");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[FT-847] error handling {Command}", command ?? "(poll)");
                }
            }
        }

        // With AI1, report a frequency or mode the operator changed on the radio.
        private async Task<IEnumerable<string>> PollAsync()
        {
            if (!_autoInformation) return Array.Empty<string>();
            var main = await ReadMainAsync(maxAgeMs: 0);
            return main == null ? Array.Empty<string>() : ChangesSinceReported(main.Value);
        }

        private IEnumerable<string> ChangesSinceReported((long Hz, char Md) main)
        {
            var replies = new List<string>(2);
            if (_reported?.Hz != main.Hz) replies.Add(FaReply(main.Hz));
            if (_reported?.Md != main.Md) replies.Add("MD0" + main.Md);
            _reported = main;
            return replies;
        }

        private async Task<IEnumerable<string>> HandleAsync(string command)
        {
            var letters = command[..2];
            var body = command[2..];
            var none = Array.Empty<string>();

            switch (letters)
            {
                case "FA":
                    if (body.Length == 0)
                    {
                        var main = await ReadMainAsync(maxAgeMs: 100);
                        return main == null ? none : new[] { FaReply(main.Value.Hz) };
                    }
                    if (long.TryParse(body, out var hz) && hz >= Ft847Cat.MinHz && hz <= Ft847Cat.MaxHz)
                    {
                        _port.Write(Ft847Cat.SetFrequencyBlock(hz));
                        _mainReadUtc = DateTime.MinValue;
                    }
                    return none;

                case "FB":
                    if (body.Length == 0)
                    {
                        if (_fb == 0) _fb = (await ReadMainAsync(maxAgeMs: 100))?.Hz ?? 0;
                        return _fb == 0 ? none : new[] { "FB" + _fb.ToString("D9") };
                    }
                    if (long.TryParse(body, out var fb)) _fb = fb;
                    return none;

                case "MD":
                    if (body == "0")
                    {
                        var main = await ReadMainAsync(maxAgeMs: 100);
                        return main == null ? none : new[] { "MD0" + main.Value.Md };
                    }
                    if (body == "1") return new[] { "MD1" + _mdB };
                    if (body.Length == 2 && body[0] == '1') { _mdB = char.ToUpperInvariant(body[1]); return none; }
                    if (body.Length == 2 && body[0] == '0')
                    {
                        var md = char.ToUpperInvariant(body[1]);
                        var mode = Ft847Cat.ModeFromMd(md);
                        if (mode != null)
                        {
                            _port.Write(Ft847Cat.SetModeBlock(mode.Value));
                            _lastSetMd = md;
                            _mainReadUtc = DateTime.MinValue;
                        }
                    }
                    return none;

                case "SM":
                    if (body == "0")
                    {
                        var rx = await ExchangeAsync(Ft847Cat.Block(Ft847Cat.ReadRxStatus), 1);
                        return rx == null ? none : new[] { $"SM0{Ft847Cat.MeterTo255(rx[0]):D3}" };
                    }
                    return new[] { "?" };

                case "RM":
                    if (body == "5")
                    {
                        var tx = await ExchangeAsync(Ft847Cat.Block(Ft847Cat.ReadTxStatus), 1);
                        return tx == null ? none : new[] { $"RM5{Ft847Cat.MeterTo255(tx[0]):D3}000" };
                    }
                    return new[] { "?" };

                case "TX":
                    if (body.Length == 0)
                    {
                        var tx = await ExchangeAsync(Ft847Cat.Block(Ft847Cat.ReadTxStatus), 1);
                        if (tx == null) return none;
                        return new[] { Ft847Cat.IsTransmitting(tx[0]) ? "TX2" : "TX0" };
                    }
                    if (body == "0") _port.Write(Ft847Cat.Block(Ft847Cat.PttOff));
                    else _logger?.LogWarning("[FT-847] {Command} ignored: CAT PTT on is not enabled for the FT-847", command);
                    return none;

                case "AI":
                    if (body.Length == 0) return new[] { _autoInformation ? "AI1" : "AI0" };
                    _autoInformation = body == "1";
                    if (_autoInformation) _reported = null;
                    return none;

                case "DT":
                    if (body == "0") return new[] { "DT0" + DateTime.Now.ToString("yyyyMMdd") };
                    if (body == "1") return new[] { "DT1" + DateTime.Now.ToString("HHmmss") };
                    if (body == "2") return new[] { "DT2+0000" };
                    return none;

                case "ID": return body.Length == 0 ? new[] { "ID0847" } : none;
                case "PS": return body.Length == 0 ? new[] { "PS1" } : none;
                case "VS": return body.Length == 0 ? new[] { "VS0" } : none;
                case "FT": return body.Length == 0 ? new[] { "FT0" } : none;
            }

            return LooksLikeRead(body) ? new[] { "?" } : none;
        }

        // A bare command, or one with a single P1 digit (AG0;, NR1;), is a read.
        private static bool LooksLikeRead(string body) => body.Length <= 1;

        private string FaReply(long hz) => "FA" + hz.ToString("D9");

        private async Task<(long Hz, char Md)?> ReadMainAsync(int maxAgeMs)
        {
            if (_main != null && (DateTime.UtcNow - _mainReadUtc).TotalMilliseconds < maxAgeMs)
                return _main;

            var reply = await ExchangeAsync(Ft847Cat.Block(Ft847Cat.ReadMainFrequencyAndMode), 5);
            if (reply == null) return null;

            var hz = Ft847Cat.FrequencyFromBcd(reply);
            var md = Ft847Cat.MdFromMode(reply[4]);
            if (hz == null || md == null)
            {
                _logger?.LogDebug("[FT-847] unreadable 03 reply {Bytes}", Convert.ToHexString(reply));
                return null;
            }

            // The FT-847 has no DATA modes, so a DATA-U set reads back as USB.
            // Report the mode YWC asked for while the radio is still in its base.
            var shown = md.Value;
            if (_lastSetMd is char asked && Ft847Cat.ModeFromMd(asked) == reply[4])
                shown = asked;
            else
                _lastSetMd = null;

            _main = (hz.Value, shown);
            _mainReadUtc = DateTime.UtcNow;
            return _main;
        }

        // Send a read block and wait for its fixed-length reply.
        private async Task<byte[]?> ExchangeAsync(byte[] block, int replyLength)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_rxLock)
            {
                _rx.Clear();
                _expect = replyLength;
                _pending = tcs;
            }

            _port.Write(block);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(_replyTimeoutMs));

            lock (_rxLock)
            {
                _pending = null;
                _rx.Clear();
            }

            if (done == tcs.Task)
            {
                _consecutiveTimeouts = 0;
                return await tcs.Task;
            }

            if (++_consecutiveTimeouts == 5)
                _logger?.LogWarning("[FT-847] no reply to five reads in a row. Check the null-modem cable, menu 37 CAT RATE against YWC's baud rate, and that the FC-20 tuner cable is unplugged");
            return null;
        }

        private void OnBytes(byte[] data)
        {
            lock (_rxLock)
            {
                if (_pending == null) return;   // nothing asked: stray bytes
                _rx.AddRange(data);
                if (_rx.Count >= _expect)
                {
                    var reply = _rx.GetRange(0, _expect).ToArray();
                    var tcs = _pending;
                    _pending = null;
                    tcs.TrySetResult(reply);
                }
            }
        }
    }
}
