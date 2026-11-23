using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services
{
    /// <summary>The byte link to a radio with the five-byte CAT: its COM port, or the simulated radio.</summary>
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
    ///
    /// The FT-817, FT-818, FT-857 and FT-897 use the same blocks, so the same
    /// transport serves them, told which radio it is by <c>model</c>. For
    /// those it sends nothing at open or close (00 and 80 are LOCK ON and OFF
    /// there), scales their four-bit meters, uses DIG and PKT for data, and
    /// answers ID; with ID0817, ID0818, ID0857 or ID0897. See <see cref="Ft847Cat"/>.
    ///
    /// The FT-817 and FT-818 also get VFO A and B, as on the FTdx10: the
    /// transport reads which VFO the radio is on from its EEPROM (BB 0055),
    /// answers VS; with it, and swaps with 81 when YWC sends VS0; or VS1;
    /// (never while transmitting). FA and MD0 are VFO A, FB and MD1 VFO B.
    /// The VFO the radio is on is read live; the other one is the last
    /// frequency and mode read while the radio was on it. A set for the VFO
    /// the radio is not on is dropped rather than swapping behind the
    /// operator's back. If the EEPROM cannot be read while the radio answers
    /// everything else, it falls back to one VFO, as on the FT-857.
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
        private readonly string _model;
        private readonly bool _readsVfo;
        private int? _vfo;                          // 0 = A, 1 = B; null until read
        private DateTime _vfoReadUtc = DateTime.MinValue;
        private int _vfoReadFailures;
        private bool _vfoReadGivenUp;
        private readonly (long Hz, char Md)?[] _slot = new (long Hz, char Md)?[2];
        private int? _reportedVfo;
        private readonly (long Hz, char Md)?[] _reportedSlot = new (long Hz, char Md)?[2];
        private readonly long _minHz;
        private readonly long _maxHz;
        private readonly string _tag;

        public Ft847CatTransport(IFt847Port port, ILogger? logger = null, int replyTimeoutMs = 120, int pollMs = 300, string model = "FT-847")
        {
            _model = model;
            _readsVfo = Ft847Cat.ReadsVfoFromEeprom(model);
            (_minHz, _maxHz) = RadioCapabilities.FrequencyRangeHz(model);
            _tag = "[" + model + "]";
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

        /// <summary>The radio this transport speaks for: FT-847, FT-817, FT-818, FT-857 or FT-897.</summary>
        public string Model => _model;

        public void Open()
        {
            _port.Open();
            if (Ft847Cat.SendsCatOnOff(_model)) _port.Write(Ft847Cat.Block(Ft847Cat.CatOn));
            _open = true;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WorkAsync(_cts.Token));
        }

        public string ReadExisting() => string.Empty;

        public void Write(byte[] buffer, int offset, int count)
        {
            if (!_open) throw new InvalidOperationException($"The {_model} link is not open.");
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
                if (_port.IsOpen && Ft847Cat.SendsCatOnOff(_model)) _port.Write(Ft847Cat.Block(Ft847Cat.CatOff));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "{Tag} CAT OFF not sent", _tag);
            }
            _port.Close();
        }

        public void Dispose()
        {
            Close();
            _port.BytesReceived -= OnBytes;
            _port.Dispose();
        }

        // The poll runs every pollMs whether or not commands are coming in.
        // It used to run only after pollMs with no command, but YWC's meter
        // poll sends one every 200 ms, so it never ran: a mode or A/B change
        // made on the radio was never seen, and the frequency only caught up
        // through the meter poll's slow FA backstop (Felix DF3FX, D#379).
        private async Task WorkAsync(CancellationToken ct)
        {
            var lastPoll = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                string? command = null;
                var untilPoll = _pollMs - (int)(DateTime.UtcNow - lastPoll).TotalMilliseconds;
                if (untilPoll > 0)
                {
                    try
                    {
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        wait.CancelAfter(untilPoll);
                        command = await _commands.Reader.ReadAsync(wait.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested) return;
                    }
                }
                if (command == null) lastPoll = DateTime.UtcNow;

                try
                {
                    var replies = command != null ? await HandleAsync(command) : await PollAsync();
                    foreach (var reply in replies)
                        if (_open) DataReceived?.Invoke(reply + ";");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "{Tag} error handling {Command}", _tag, command ?? "(poll)");
                }
            }
        }

        // With AI1, report a frequency or mode the operator changed on the radio.
        private async Task<IEnumerable<string>> PollAsync()
        {
            if (!_autoInformation) return Array.Empty<string>();
            var vfo = await ReadVfoAsync(maxAgeMs: 0);
            var before = vfo is int b ? _slot[b] : null;
            var main = await ReadMainAsync(maxAgeMs: 0);
            if (vfo is not int v) return main == null ? Array.Empty<string>() : ChangesSinceReported(main.Value);

            // A/B pressed on the radio between the two reads: the 03 reply may
            // be the other VFO's, so undo what it stored and report only the
            // VFO change. The next poll reads the new VFO cleanly.
            if (await ReadVfoAsync(maxAgeMs: 0) is int after && after != v)
            {
                _slot[v] = before;
                _reportedVfo = after;
                return new[] { "VS" + after };
            }

            // Both VFOs: the one the radio is on was just read, and the other
            // may have been filled by a swap (VS) since the last report.
            var replies = new List<string>(5);
            if (_reportedVfo != v) replies.Add("VS" + v);
            _reportedVfo = v;
            for (int which = 0; which < 2; which++)
            {
                if (_slot[which] is not { } m) continue;
                if (_reportedSlot[which]?.Hz != m.Hz) replies.Add(which == 0 ? FaReply(m.Hz) : FbReply(m.Hz));
                if (_reportedSlot[which]?.Md != m.Md) replies.Add((which == 0 ? "MD0" : "MD1") + m.Md);
                _reportedSlot[which] = m;
            }
            return replies;
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
            // FT-817/818: FA, FB and MD act on the VFO they name.
            var known = letters is "FA" or "FB" or "MD" ? await ReadVfoAsync(maxAgeMs: 100) : null;

            switch (letters)
            {
                case "FA" when known is int va:
                    return await VfoFrequencyAsync(0, va, body);

                case "FB" when known is int vb:
                    return await VfoFrequencyAsync(1, vb, body);

                case "MD" when known is int vm && body.Length >= 1 && (body[0] == '0' || body[0] == '1'):
                    return await VfoModeAsync(body[0] - '0', vm, body[1..]);

                case "VS" when _readsVfo:
                    if (body.Length == 0)
                    {
                        var v = await ReadVfoAsync(maxAgeMs: 100);
                        return new[] { "VS" + (v ?? 0) };
                    }
                    if (body == "0" || body == "1") await SelectVfoAsync(body[0] - '0');
                    return none;

                case "FA":
                    if (body.Length == 0)
                    {
                        var main = await ReadMainAsync(maxAgeMs: 100);
                        return main == null ? none : new[] { FaReply(main.Value.Hz) };
                    }
                    if (long.TryParse(body, out var hz) && hz >= _minHz && hz <= _maxHz)
                    {
                        await SendSetAsync(Ft847Cat.SetFrequencyBlock(hz));
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
                        var mode = Ft847Cat.ModeFromMd(md, _model);
                        if (mode != null)
                        {
                            await SendSetAsync(Ft847Cat.SetModeBlock(mode.Value));
                            _lastSetMd = md;
                            _mainReadUtc = DateTime.MinValue;
                        }
                    }
                    return none;

                case "SM":
                    if (body == "0")
                    {
                        var rx = await ExchangeAsync(Ft847Cat.Block(Ft847Cat.ReadRxStatus), 1);
                        return rx == null ? none : new[] { $"SM0{Ft847Cat.MeterTo255(rx[0], _model):D3}" };
                    }
                    return new[] { "?" };

                case "RM":
                    if (body == "5")
                    {
                        var tx = await ExchangeAsync(Ft847Cat.Block(Ft847Cat.ReadTxStatus), 1);
                        if (tx == null) return none;
                        // Receiving, the meter bits are not a power reading.
                        if (!Ft847Cat.IsTransmitting(tx[0])) return new[] { "RM5000000" };
                        var po = Ft847Cat.MeterTo255(tx[0], _model);
                        // The FT-817 and FT-818: power from BD, as Hamlib reads it.
                        if (Ft847Cat.ReadsPowerWithBd(_model))
                        {
                            var bd = await ExchangeAsync(Ft847Cat.Block(Ft847Cat.ReadTxMetering), 2);
                            if (bd != null) po = Ft847Cat.PowerFromBd(bd);
                        }
                        return new[] { $"RM5{po:D3}000" };
                    }
                    return new[] { "?" };

                case "TX":
                    if (body.Length == 0)
                    {
                        var tx = await ExchangeAsync(Ft847Cat.Block(Ft847Cat.ReadTxStatus), 1);
                        if (tx == null) return none;
                        return new[] { Ft847Cat.IsTransmitting(tx[0]) ? "TX2" : "TX0" };
                    }
                    if (body == "0") await SendSetAsync(Ft847Cat.Block(Ft847Cat.PttOff));
                    else _logger?.LogWarning("{Tag} {Command} ignored: CAT PTT on is not enabled for the {Model}", _tag, command, _model);
                    return none;

                case "AI":
                    if (body.Length == 0) return new[] { _autoInformation ? "AI1" : "AI0" };
                    _autoInformation = body == "1";
                    if (_autoInformation)
                    {
                        _reported = null;
                        _reportedVfo = null;
                        _reportedSlot[0] = _reportedSlot[1] = null;
                    }
                    return none;

                case "DT":
                    if (body == "0") return new[] { "DT0" + DateTime.Now.ToString("yyyyMMdd") };
                    if (body == "1") return new[] { "DT1" + DateTime.Now.ToString("HHmmss") };
                    if (body == "2") return new[] { "DT2+0000" };
                    return none;

                case "ID": return body.Length == 0 ? new[] { Ft847Cat.IdReply(_model) } : none;
                case "PS": return body.Length == 0 ? new[] { "PS1" } : none;
                case "VS": return body.Length == 0 ? new[] { "VS0" } : none;
                case "FT": return body.Length == 0 ? new[] { "FT0" } : none;
            }

            return LooksLikeRead(body) ? new[] { "?" } : none;
        }

        // A bare command, or one with a single P1 digit (AG0;, NR1;), is a read.
        private static bool LooksLikeRead(string body) => body.Length <= 1;

        private string FaReply(long hz) => "FA" + hz.ToString("D9");
        private string FbReply(long hz) => "FB" + hz.ToString("D9");

        // FT-817/818: which VFO the radio is on, from its EEPROM. Null when
        // this radio isn't read that way, or the read has never worked.
        private async Task<int?> ReadVfoAsync(int maxAgeMs)
        {
            if (!_readsVfo || _vfoReadGivenUp) return null;
            if (_vfo != null && (DateTime.UtcNow - _vfoReadUtc).TotalMilliseconds < maxAgeMs) return _vfo;

            var radioWasAnswering = _consecutiveTimeouts == 0;
            var reply = await ExchangeAsync(Ft847Cat.ReadEepromBlock(Ft847Cat.VfoEepromAddress), 2);
            if (reply == null)
            {
                // A radio that answers 03 but never BB: fall back to one VFO.
                if (_vfo == null && radioWasAnswering && ++_vfoReadFailures >= 3)
                {
                    _vfoReadGivenUp = true;
                    _logger?.LogWarning("{Tag} the radio doesn't answer the EEPROM read, so YWC can't tell VFO A from B; it shows one VFO", _tag);
                }
                return _vfo;
            }

            _vfoReadFailures = 0;
            var v = Ft847Cat.VfoFromEeprom(reply);
            if (_vfo != v)
            {
                _logger?.LogDebug("{Tag} radio is on VFO {Vfo}", _tag, v == 0 ? "A" : "B");
                _vfo = v;
                _mainReadUtc = DateTime.MinValue;   // the cached 03 was the other VFO
            }
            _vfoReadUtc = DateTime.UtcNow;
            return v;
        }

        // FA / FB on a radio whose VFO is known: live for the VFO it's on,
        // remembered for the other. A set for the other VFO is dropped.
        private async Task<IEnumerable<string>> VfoFrequencyAsync(int which, int on, string body)
        {
            if (body.Length == 0)
            {
                var hz = which == on ? (await ReadMainAsync(maxAgeMs: 100))?.Hz : _slot[which]?.Hz;
                if (hz == null) return Array.Empty<string>();
                return new[] { which == 0 ? FaReply(hz.Value) : FbReply(hz.Value) };
            }
            if (!long.TryParse(body, out var set) || set < _minHz || set > _maxHz) return Array.Empty<string>();
            if (which != on)
            {
                _logger?.LogDebug("{Tag} VFO {Vfo} frequency not set: the radio is on the other VFO", _tag, which == 0 ? "A" : "B");
                return Array.Empty<string>();
            }
            await SendSetAsync(Ft847Cat.SetFrequencyBlock(set));
            _mainReadUtc = DateTime.MinValue;
            return Array.Empty<string>();
        }

        // MD0 / MD1 likewise.
        private async Task<IEnumerable<string>> VfoModeAsync(int which, int on, string rest)
        {
            var p1 = which == 0 ? "MD0" : "MD1";
            if (rest.Length == 0)
            {
                var md = which == on ? (await ReadMainAsync(maxAgeMs: 100))?.Md : _slot[which]?.Md;
                return md == null ? Array.Empty<string>() : new[] { p1 + md.Value };
            }
            if (rest.Length != 1 || which != on) return Array.Empty<string>();
            var asked = char.ToUpperInvariant(rest[0]);
            var mode = Ft847Cat.ModeFromMd(asked, _model);
            if (mode != null)
            {
                await SendSetAsync(Ft847Cat.SetModeBlock(mode.Value));
                _lastSetMd = asked;
                _mainReadUtc = DateTime.MinValue;
            }
            return Array.Empty<string>();
        }

        // VS0; / VS1;: swap with 81 if the radio is on the other VFO. Never
        // while transmitting, and only when the radio's VFO has been read.
        private async Task SelectVfoAsync(int target)
        {
            var on = await ReadVfoAsync(maxAgeMs: 0);
            if (on == null || on == target) return;

            var tx = await ExchangeAsync(Ft847Cat.Block(Ft847Cat.ReadTxStatus), 1);
            if (tx == null || Ft847Cat.IsTransmitting(tx[0]))
            {
                _logger?.LogInformation("{Tag} VFO not changed: the radio is transmitting or not answering", _tag);
                return;
            }

            await SendSetAsync(Ft847Cat.Block(Ft847Cat.ToggleVfo));
            // Hamlib waits 50 ms for the swap before reading the EEPROM again.
            await Task.Delay(60);
            await ReadVfoAsync(maxAgeMs: 0);
            await ReadMainAsync(maxAgeMs: 0);   // fills the new VFO's slot
        }

        private async Task<(long Hz, char Md)?> ReadMainAsync(int maxAgeMs)
        {
            if (_main != null && (DateTime.UtcNow - _mainReadUtc).TotalMilliseconds < maxAgeMs)
                return _main;

            var reply = await ExchangeAsync(Ft847Cat.Block(Ft847Cat.ReadMainFrequencyAndMode), 5);
            if (reply == null) return null;

            var hz = Ft847Cat.FrequencyFromBcd(reply);
            var md = Ft847Cat.MdFromMode(reply[4], _model);
            if (hz == null || md == null)
            {
                _logger?.LogDebug("{Tag} unreadable 03 reply {Bytes}", _tag, Convert.ToHexString(reply));
                return null;
            }

            // The FT-847 has no DATA modes, so a DATA-U set reads back as USB,
            // and the FT-8x7's DIG reads back the same for DATA-L, DATA-U and RTTY.
            // Report the mode YWC asked for while the radio is still in its base.
            var shown = md.Value;
            if (_lastSetMd is char asked && Ft847Cat.ModeFromMd(asked, _model) == reply[4])
                shown = asked;
            else
                _lastSetMd = null;

            _main = (hz.Value, shown);
            _mainReadUtc = DateTime.UtcNow;
            if (_vfo is int v && !_vfoReadGivenUp) _slot[v] = _main;
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
            {
                if (_model is "FT-817" or "FT-818")
                    _logger?.LogWarning("{Tag} no reply to five reads in a row. Check the CAT cable in the ACC jack and menu 14 CAT RATE against YWC's baud rate", _tag);
                else if (Ft847Cat.IsFt8x7(_model))
                    _logger?.LogWarning("{Tag} no reply to five reads in a row. Check the CAT cable in the CAT/LINEAR jack, menu 019 CAT RATE against YWC's baud rate, and that menu 020 CAT/LIN/TUN is set to CAT", _tag);
                else
                    _logger?.LogWarning("[FT-847] no reply to five reads in a row. Check the null-modem cable, menu 37 CAT RATE against YWC's baud rate, and that the FC-20 tuner cable is unplugged");
            }
            return null;
        }

        // A set block. On the FT-8x7 the radio answers it with a one-byte
        // ack, which is read here so it can't land in the next read's reply
        // (see Ft847Cat.AcksSets). No ack is not an error: Hamlib carries on
        // too, and a missing ack is not a missing radio, so it doesn't count
        // towards the five-timeouts warning.
        private async Task SendSetAsync(byte[] block)
        {
            if (!Ft847Cat.AcksSets(_model))
            {
                _port.Write(block);
                return;
            }

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_rxLock)
            {
                _rx.Clear();
                _expect = 1;
                _pending = tcs;
            }

            _port.Write(block);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(_replyTimeoutMs));

            lock (_rxLock)
            {
                _pending = null;
                _rx.Clear();
            }

            if (done != tcs.Task)
                _logger?.LogDebug("{Tag} no ack for {Bytes}", _tag, Convert.ToHexString(block));
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
