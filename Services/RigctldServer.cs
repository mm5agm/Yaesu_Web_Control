using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Implements a rigctld-compatible TCP server for WSJT-X and other Hamlib clients
    /// </summary>
    public class RigctldServer : BackgroundService
    {
        private readonly CatMultiplexerService _multiplexer;
        private readonly RadioStateService _radioStateService;
        private readonly ILogger<RigctldServer> _logger;
        private readonly FrequencyRejectionNotifier _rejectionNotifier;
        private readonly CatClientPresence _presence;
        private TcpListener? _listener;
        private readonly List<TcpClient> _clients = new();
        private readonly object _clientsLock = new();
        public const int RigctldPort = 4532;

        // If the port is taken at start-up, try again this often. It used to
        // try once: on 2026-11-27 (W2JAZ) the port was busy for two seconds as
        // YWC started, the server gave up, and WSJT-X got "connection refused"
        // until YWC was restarted.
        private static readonly TimeSpan BindRetry = TimeSpan.FromSeconds(2);
        private volatile bool _listening;
        private volatile string? _notListeningWhy;

        /// <summary>For the Settings page: listening, why not, and how many programs are connected.</summary>
        public (bool Listening, string? Why, int Clients) Status
        {
            get
            {
                int clients;
                lock (_clientsLock) clients = _clients.Count;
                return (_listening, _notListeningWhy, clients);
            }
        }

        // State for advanced commands
        private bool _splitEnabled = false;
        private long _splitFrequency = 0;
        private int _ritOffset = 0;
        private int _xitOffset = 0;

        // TX safety watchdog. A rigctld client can key the radio and then fail to
        // send the matching release — WSJT-X's Tune-stop not sending PTT-off, a
        // client crash, or a dropped connection all leave the radio keyed forever.
        // We track PTT keyed via rigctld and force TX0; if it is held past
        // TxSafetyTimeoutSeconds, or if the client that keyed it disconnects.
        private const int TxSafetyTimeoutSeconds = 180;
        private readonly object _pttLock = new();
        private bool _rigctldPttActive = false;
        private string? _pttClientId = null;
        private CancellationTokenSource? _pttWatchdogCts;

        private static readonly HashSet<string> SupportedModes = new()
        {
            "LSB", "USB", "CW", "CW-R", "RTTY", "RTTY-R", "AM", "FM",
            // Hamlib "packet" mode names — WSJT-X uses these for digital modes
            // (PKTUSB = FT8/FT4/PSK on the USB-data side, = Yaesu DATA-U).
            // GetModeAsync already translates DATA-USB → PKTUSB outbound, so
            // accepting them on inbound completes the round-trip.
            "PKTUSB", "PKTLSB", "PKTFM"
        };

        // Hamlib → Yaesu mode-name mapping for the set-mode path.
        // Anything not in this map is sent verbatim to CatCommands.FormatMode.
        private static readonly Dictionary<string, string> HamlibToYaesuMode = new()
        {
            { "PKTUSB", "DATA-U"   },
            { "PKTLSB", "DATA-L"   },
            { "PKTFM",  "DATA-FM"  },
            { "CW",     "CW-U"     },
            { "CW-R",   "CW-L"     },
            { "RTTY",   "RTTY-L"   },
            { "RTTY-R", "RTTY-U"   },
        };

        // The tunable range is per model — see RadioCapabilities.FrequencyRangeHz.
        // It used to be a pair of constants here, duplicating the same two
        // literals in CatController, while the WSJT-X UDP path had no bound at
        // all. IsTunableFrequency is now the single source for all of them.
        private bool IsTunable(long hz) =>
            RadioCapabilities.IsTunableFrequency(_radioStateService.RadioModel, hz);

        // Band mapping for set_band support (using BSxx; command codes)
        private static readonly Dictionary<string, string> BandCodes = new()
        {
            { "160m", "00" },
            { "80m",  "01" },
            { "60m",  "02" },
            { "40m",  "03" },
            { "30m",  "04" },
            { "20m",  "05" },
            { "17m",  "06" },
            { "15m",  "07" },
            { "12m",  "08" },
            { "10m",  "09" },
            { "6m",   "10" },
            { "4m",   "11" } // Added 4m band
        };

        public RigctldServer(
            CatMultiplexerService multiplexer,
            RadioStateService radioStateService,
            ILogger<RigctldServer> logger,
            FrequencyRejectionNotifier rejectionNotifier,
            CatClientPresence presence)
        {
            _presence = presence;
            _multiplexer = multiplexer;
            _radioStateService = radioStateService;
            _logger = logger;
            _rejectionNotifier = rejectionNotifier;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _listener = await StartListeningAsync(stoppingToken);
                _listening = true;
                _notListeningWhy = null;
                _logger.LogInformation("✓ rigctld server listening on port {Port}", RigctldPort);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                    var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                    _logger.LogInformation("rigctld client connected: {Endpoint}", clientEndpoint);

                    lock (_clientsLock)
                    {
                        _clients.Add(client);
                    }
                    _presence.RigctldConnected();

                    _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "rigctld server error");
            }
            finally
            {
                _listening = false;
                _listener?.Stop();
                _logger.LogInformation("rigctld server stopped");
                _logger.LogWarning("RigctldServer ExecuteAsync has exited. Service should be stopped.");
            }

        }

        /// <summary>
        /// Starts listening on the rigctld port, trying again every
        /// <see cref="BindRetry"/> while another program has it. Logs once
        /// when the port is taken (with the program holding it, if Windows
        /// says), every minute while it stays taken, and when it comes free.
        /// </summary>
        private async Task<TcpListener> StartListeningAsync(CancellationToken stoppingToken)
        {
            var since = DateTime.UtcNow;
            var nextReminder = since + TimeSpan.FromMinutes(1);
            bool warned = false;
            while (true)
            {
                var listener = new TcpListener(IPAddress.Any, RigctldPort);
                try
                {
                    listener.Start();
                    if (warned)
                        _logger.LogInformation("rigctld port {Port} came free after {Seconds:0} s.",
                            RigctldPort, (DateTime.UtcNow - since).TotalSeconds);
                    return listener;
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                {
                    listener.Stop();
                    if (!warned || DateTime.UtcNow >= nextReminder)
                    {
                        string owner = await PortOwner.DescribeAsync(RigctldPort);
                        _notListeningWhy = $"Port {RigctldPort} is in use{owner}. YWC tries again every {BindRetry.TotalSeconds:0} seconds.";
                        if (!warned)
                            _logger.LogWarning(
                                "rigctld port {Port} is in use{Owner}, so WSJT-X and loggers can't connect yet. Trying again every {Seconds:0} s.",
                                RigctldPort, owner, BindRetry.TotalSeconds);
                        else
                            _logger.LogWarning("rigctld port {Port} still in use after {Minutes:0} min{Owner}.",
                                RigctldPort, (DateTime.UtcNow - since).TotalMinutes, owner);
                        warned = true;
                        nextReminder = DateTime.UtcNow + TimeSpan.FromMinutes(1);
                    }
                }
                await Task.Delay(BindRetry, stoppingToken);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var remoteEndPoint = client.Client.RemoteEndPoint;
            var clientEndpoint = remoteEndPoint != null ? remoteEndPoint.ToString() : "unknown";
            var clientId = remoteEndPoint != null
                ? $"rigctld-{remoteEndPoint}"
                : "rigctld-unknown";
            var stream = client.GetStream();
            var buffer = new byte[1024];
            var pending = new StringBuilder();
            var catWarned = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                    if (bytesRead == 0) break;

                    var data = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                    // A program set to the radio's own Hamlib model (FTDX101MP
                    // and so on) with this port as its device sends Yaesu CAT
                    // ("FA;"), not rigctl text. Nothing here answers it, so the
                    // program times out and hangs up after a few seconds. Say why.
                    if (!catWarned && LooksLikeYaesuCat(data))
                    {
                        catWarned = true;
                        _logger.LogWarning(
                            "[{ClientId}] This program is sending Yaesu CAT commands, not rigctl commands. " +
                            "In its rig settings choose Hamlib NET rigctl (model 2), not the radio's own model, " +
                            "with localhost:{Port} as the device.", clientId, RigctldPort);
                    }

                    // Split on newlines. Hamlib may send several commands in one
                    // TCP write, or one command across two; keep any tail with no
                    // newline yet until the rest arrives.
                    var commands = TakeLines(pending, data);

                    foreach (var rawCmd in commands)
                    {
                        var command = rawCmd.Trim();
                        if (string.IsNullOrEmpty(command)) continue;

                        _logger.LogDebug("[{ClientId}] Received: {Command}", clientId, command);

                        var response = await AnswerLineAsync(command,
                            (name, args, vfo) => ProcessRigctldCommandAsync(name, args, vfo, clientId));

                        if (!string.IsNullOrEmpty(response))
                        {
                            var responseBytes = Encoding.ASCII.GetBytes(response + "\n");
                            await stream.WriteAsync(responseBytes, cancellationToken);
                            _logger.LogDebug("[{ClientId}] Sent: {Response}", clientId, response.TrimEnd());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ClientId}] Client handler error", clientId);
            }
            finally
            {
                lock (_clientsLock)
                {
                    _clients.Remove(client);
                }
                _presence.RigctldDisconnected();

                // Safety net: never leave the radio keyed because a client that
                // held PTT went away without releasing it.
                await ReleasePttIfClientHeldAsync(clientId);

                client.Close();
                _logger.LogInformation("[{ClientId}] Client disconnected", clientId);
            }
        }

        // Appends a chunk read from the socket and returns every complete
        // line in it; whatever follows the last newline stays in pending.
        internal static List<string> TakeLines(StringBuilder pending, string chunk)
        {
            pending.Append(chunk);
            var text = pending.ToString();
            var end = text.LastIndexOf('\n');
            if (end < 0)
            {
                // A client that never sends a newline mustn't grow this forever.
                if (pending.Length > 4096) pending.Clear();
                return new List<string>();
            }
            pending.Clear().Append(text, end + 1, text.Length - end - 1);
            return text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        // Yaesu CAT: two capital letters, optional parameters, a semicolon,
        // and no newline. rigctl text always ends in a newline and has no ';'.
        private static readonly System.Text.RegularExpressions.Regex YaesuCat =
            new(@"^([A-Z]{2}[0-9A-Z+\-]*;)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        internal static bool LooksLikeYaesuCat(string chunk) => YaesuCat.IsMatch(chunk);

        // Hamlib's extended response protocol (rigctld's --help: "+;|,"). A
        // command starting with one of these is answered as "name: args",
        // then one "Label: value" per value, then "RPRT n", joined by that
        // character ('+' means a newline). Log4OM uses it: ";V ?" once, then
        // "+\get_vfo_info VFOA" and "VFOB" every 0.6 s. Before #387 each got
        // RPRT -1, Log4OM stopped asking, and its frequency read OFFLINE.
        private const string ExtendedPrefixes = "+;|,";

        // Hamlib's one-letter commands (rigctl_parse.c), case-sensitive.
        // Before #387 x/z/r/c meant split / split freq / RIT / XIT here, but
        // in Hamlib they are split mode / XIT / repeater shift / CTCSS, and
        // the real s/S, i/I and j/J were missing, so netrigctl's split and
        // RIT commands (WSJT-X "Rig" split) got RPRT -1.
        private static readonly Dictionary<char, string> ShortNames = new()
        {
            ['f'] = "get_freq",       ['F'] = "set_freq",
            ['m'] = "get_mode",       ['M'] = "set_mode",
            ['v'] = "get_vfo",        ['V'] = "set_vfo",
            ['t'] = "get_ptt",        ['T'] = "set_ptt",
            ['s'] = "get_split_vfo",  ['S'] = "set_split_vfo",
            ['i'] = "get_split_freq", ['I'] = "set_split_freq",
            ['x'] = "get_split_mode", ['X'] = "set_split_mode",
            ['j'] = "get_rit",        ['J'] = "set_rit",
            ['z'] = "get_xit",        ['Z'] = "set_xit",
            ['l'] = "get_level",      ['L'] = "set_level",
            ['u'] = "get_func",       ['U'] = "set_func",
            ['_'] = "get_info",
            ['q'] = "quit",           ['Q'] = "quit",
        };

        // The labels Hamlib 4.6 puts on each value in an extended reply.
        // get_level and get_func values have none.
        private static readonly Dictionary<string, string[]> ReplyLabels = new()
        {
            ["get_freq"]       = new[] { "Frequency" },
            ["get_mode"]       = new[] { "Mode", "Passband" },
            ["get_vfo"]        = new[] { "VFO" },
            ["get_ptt"]        = new[] { "PTT" },
            ["get_split_vfo"]  = new[] { "Split", "TX VFO" },
            ["get_split_freq"] = new[] { "TX Frequency" },
            ["get_rit"]        = new[] { "RIT" },
            ["get_xit"]        = new[] { "XIT" },
            ["get_vfo_info"]   = new[] { "Freq", "Mode", "Width", "Split", "SatMode" },
            ["get_powerstat"]  = new[] { "Power Status" },
            ["get_info"]       = new[] { "Info" },
        };

        private static readonly HashSet<string> VfoNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "VFOA", "VFOB", "VFOC", "currVFO", "VFO", "MEM", "Main", "Sub",
            "MainA", "MainB", "SubA", "SubB", "TX", "RX",
        };

        internal static bool IsVfoB(string? vfo) => vfo is not null &&
            (vfo.Equals("VFOB", StringComparison.OrdinalIgnoreCase) ||
             vfo.StartsWith("Sub", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// One line from a client to its reply. <paramref name="plain"/>
        /// answers a long-form command (name, arguments, VFO or null) in
        /// Hamlib's plain format; this adds the extended format when asked.
        /// </summary>
        internal static async Task<string> AnswerLineAsync(
            string line, Func<string, string[], string?, Task<string>> plain)
        {
            char? sep = null;
            if (line.Length > 0 && ExtendedPrefixes.Contains(line[0]))
            {
                sep = line[0] == '+' ? '\n' : line[0];
                line = line[1..].TrimStart();
            }
            if (line.StartsWith('\\')) line = line[1..];

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "RPRT 0";

            var name = parts[0].Length == 1
                ? (ShortNames.TryGetValue(parts[0][0], out var shortName) ? shortName : null)
                : parts[0].ToLowerInvariant();
            if (name is null) return "RPRT -1";
            if (name == "quit") return "RPRT 0";

            // VFO mode (rigctld -o, Log4OM's "VFO MODE" box): every command
            // but set_vfo carries the VFO first, as in "F VFOA 14074000".
            // get_vfo_info's only argument is the VFO, so it keeps it.
            var args = parts[1..];
            string? vfo = null;
            if (name != "set_vfo" && name != "get_vfo_info" && args.Length > 0 && VfoNames.Contains(args[0]))
            {
                vfo = args[0];
                args = args[1..];
            }

            var reply = await plain(name, args, vfo);
            return sep is char c ? FormatExtended(name, parts[1..], reply, c) : reply;
        }

        internal static string FormatExtended(string name, string[] args, string reply, char sep)
        {
            var head = args.Length > 0 ? $"{name}: {string.Join(' ', args)}" : $"{name}:";
            var lines = reply.Split('\n').ToList();
            var status = "RPRT 0";
            if (lines[^1].StartsWith("RPRT"))
            {
                status = lines[^1];
                lines.RemoveAt(lines.Count - 1);
            }

            // Hamlib's own quirk, copied because clients are written against
            // it: the VFO list from "V ?" is followed by a newline, not sep.
            if (name == "set_vfo" && lines.Count > 0)
                return $"{head}{sep}{string.Join(sep, lines)}\n{status}";

            ReplyLabels.TryGetValue(name, out var labels);
            var fields = lines.Select((value, i) =>
                labels is not null && i < labels.Length ? $"{labels[i]}: {value}" : value);
            return string.Join(sep, new[] { head }.Concat(fields).Append(status));
        }

        private async Task<string> ProcessRigctldCommandAsync(string name, string[] args, string? vfo, string clientId)
        {
            string? Arg(int i) => args.Length > i ? args[i] : null;

            // Only VFO A can be set from here; a set aimed at VFO B is refused
            // rather than quietly applied to A.
            var onB = IsVfoB(vfo);

            return name switch
            {
                "get_freq"       => onB ? _radioStateService.FrequencyB.ToString() : await GetFrequencyAsync(clientId),
                "set_freq"       => !onB && Arg(0) is { } f ? await SetFrequencyAsync(f, clientId) : "RPRT -1",
                "get_mode"       => onB ? $"{ToHamlibMode(_radioStateService.ModeB ?? "")}\n0" : await GetModeAsync(clientId),
                "set_mode"       => !onB && Arg(0) is { } m ? await SetModeAsync(m, Arg(1), clientId) : "RPRT -1",
                "get_vfo"        => "VFOA",
                "set_vfo"        => Arg(0) == "?" ? "VFOA VFOB \nRPRT 0" : Arg(0) is { } v ? SetVfo(v) : "RPRT -1",
                "get_vfo_info"   => await GetVfoInfoAsync(Arg(0), clientId),
                "get_ptt"        => await GetPttAsync(clientId),
                "set_ptt"        => Arg(0) is { } p ? await SetPttAsync(p, clientId) : "RPRT -1",
                "get_level"      => Arg(0) is { } l ? await GetLevelAsync(l, clientId) : "RPRT -1",
                "set_level"      => args.Length > 1 ? SetLevel(args[0], args[1]) : "RPRT -1",
                "get_split_vfo"  => GetSplit(),
                "set_split_vfo"  => Arg(0) is { } s ? SetSplit(s) : "RPRT -1",
                "get_split_freq" => GetSplitFrequency(),
                "set_split_freq" => Arg(0) is { } sf ? SetSplitFrequency(sf) : "RPRT -1",
                "get_rit"        => GetRit(),
                "set_rit"        => Arg(0) is { } r ? SetRit(r) : "RPRT -1",
                "get_xit"        => GetXit(),
                "set_xit"        => Arg(0) is { } x ? SetXit(x) : "RPRT -1",
                "get_func"       => Arg(0) is { } fn ? await GetFuncAsync(fn, clientId) : "RPRT -1",
                "set_func"       => args.Length > 1 ? await SetFuncAsync(args[0], args[1], clientId) : "RPRT -1",
                "get_info"       => GetInfo(),
                "set_band"       => Arg(0) is { } band ? await SetBandAsync(band, clientId) : "RPRT -1",
                "get_powerstat"  => "1",
                "chk_vfo"        => "CHKVFO 0",
                "dump_state"     => GetDumpState(),
                _                => "RPRT -1"
            };
        }

        // get_vfo_info: frequency, mode, width, split, sat mode. Answered
        // from the cached state, as get_freq is, because Log4OM asks for
        // both VFOs every 0.6 s.
        private async Task<string> GetVfoInfoAsync(string? vfo, string clientId)
        {
            var onB = IsVfoB(vfo);
            var freq = onB ? _radioStateService.FrequencyB.ToString() : await GetFrequencyAsync(clientId);
            if (freq.StartsWith("RPRT")) return freq;

            var yaesuMode = onB ? _radioStateService.ModeB : _radioStateService.ModeA;
            var mode = string.IsNullOrEmpty(yaesuMode) && !onB
                ? (await GetModeAsync(clientId)).Split('\n')[0]
                : ToHamlibMode(yaesuMode ?? "");
            return VfoInfo(freq, mode, _splitEnabled);
        }

        internal static string VfoInfo(string freq, string mode, bool split) =>
            $"{freq}\n{mode}\n0\n{(split ? 1 : 0)}\n0";

        // --- Command Implementations and Stubs ---

        private Task<string> GetFrequencyAsync(string clientId)
        {
            // Read from the cached state (RadioStateService) rather than
            // querying the radio fresh on every get_freq call.
            //
            // Why: WSJT-X (and Hamlib in general) polls get_freq immediately
            // after every set_freq to confirm the change took. If we send a
            // fresh CAT 'FA;' query at that moment, the response can race
            // against:
            //   1. The radio's own ~50 ms internal apply-set delay (some
            //      Yaesu radios acknowledge FA<newfreq>; before the VFO has
            //      actually moved, so a FA; query in the same window can
            //      return the OLD frequency).
            //   2. YWC's continuous CAT poller, whose response queue may
            //      deliver an in-flight pre-set reading instead of our
            //      post-set query result.
            //
            // Either way WSJT-X briefly displays the old frequency, then
            // bounces to the new one a second or two later — exactly the
            // symptom reported by W1WRH (#22).
            //
            // SetFrequencyAsync below updates _radioStateService.FrequencyA
            // immediately after sending the CAT set, so the cached value is
            // already correct when WSJT-X polls. Manual knob-turns on the
            // radio update the cache via the CAT poller within ~100 ms,
            // much faster than WSJT-X's own typical poll interval — so the
            // "knob → WSJT-X follows" path stays responsive.
            //
            // The CAT fallback is kept only for the bootstrap case (cache
            // not yet populated at startup). Once the cache is populated
            // it stays the source of truth for rigctld GETs.
            if (_radioStateService.FrequencyA > 0)
                return Task.FromResult(_radioStateService.FrequencyA.ToString());
            return GetFrequencyFromCatAsync(clientId);
        }

        private async Task<string> GetFrequencyFromCatAsync(string clientId)
        {
            var response = await _multiplexer.SendCommandAsync("FA", clientId);
            var freq = CatCommands.ParseFrequency(response ?? string.Empty);
            return freq > 0 ? freq.ToString() : "RPRT -1";
        }

        private async Task<string> SetFrequencyAsync(string freqStr, string clientId)
        {
            // Hamlib sends frequencies as decimals: e.g. "10136055.000000"
            if (!double.TryParse(freqStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var freqDouble))
                return "RPRT -1";

            var freq = (long)Math.Round(freqDouble);
            if (!IsTunable(freq))
            {
                // RPRT -1 is the honest answer and is deliberately kept: the
                // radio genuinely cannot tune this. The notifier only explains
                // the refusal to the operator — it does not suppress the
                // client-side error, and cannot (see FrequencyRejectionNotifier).
                // "rigctld", not clientId: clientId is an endpoint like
                // rigctld-127.0.0.1:54321, and rigctld cannot know which
                // program is on the other end. Naming a specific one would be
                // a guess shown to the operator as fact.
                _logger.LogDebug("[{ClientId}] Refused out-of-range set_freq {Freq}", clientId, freq);
                await _rejectionNotifier.NotifyAsync(freq, "rigctld");
                return "RPRT -1";
            }

            var command = CatCommands.FormatFrequencyA(freq);
            await _multiplexer.SendCommandAsync(command, clientId);

            // Update RadioStateService immediately so the UI updates via SignalR
            // AND so the very next rigctld get_freq from WSJT-X (which always
            // follows a set_freq) returns the value we just set rather than
            // racing against the radio's apply-set delay or the CAT poller's
            // response queue. See GetFrequencyAsync above for the full rationale.
            _radioStateService.FrequencyA = freq;
            _logger.LogInformation("Rigctld set_freq: {Freq} Hz (client: {ClientId})", freq, clientId);

            return "RPRT 0";
        }

        private async Task<string> GetModeAsync(string clientId)
        {
            var response = await _multiplexer.SendCommandAsync("MD0", clientId);
            var mode = CatCommands.ParseMode(response ?? string.Empty, _radioStateService.RadioModel);
            var bandwidth = 0; // FT-dx101 manages bandwidth automatically

            return $"{ToHamlibMode(mode)}\n{bandwidth}";
        }

        /// <summary>
        /// Yaesu mode name (as CatCommands.ParseMode gives it) to the Hamlib
        /// name, the reverse of HamlibToYaesuMode. This used to match names
        /// ParseMode never produces ("CW", "DATA-USB"), so CW, RTTY and every
        /// DATA mode were reported as USB, and WSJT-X saw FT8 on DATA-U as USB.
        /// </summary>
        internal static string ToHamlibMode(string yaesuMode) => yaesuMode switch
        {
            "LSB"       => "LSB",
            "USB"       => "USB",
            "CW-U"      => "CW",
            "CW-L"      => "CW-R",
            "RTTY-L"    => "RTTY",
            "RTTY-U"    => "RTTY-R",
            "DATA-U"    => "PKTUSB",
            "PSK"       => "PKTUSB",   // the 101's PSK is a USB-side data mode
            "DATA-L"    => "PKTLSB",
            "DATA-FM" or "DATA-FM-N" => "PKTFM",
            "FM" or "FM-N" or "C4FM"  => "FM",
            "AM" or "AM-N" => "AM",
            _ => "USB"
        };

        private async Task<string> SetModeAsync(string mode, string? passband, string clientId)
        {
            var hamlibMode = mode.ToUpper();
            if (!SupportedModes.Contains(hamlibMode))
                return "RPRT -1 // E_MODE: Unsupported mode for this rig.";

            // Translate Hamlib mode names to Yaesu names where they differ
            // (PKTUSB → DATA-U, CW → CW-U, etc.). Sending raw Hamlib names to
            // CatCommands.FormatMode would set an unintended mode on the radio.
            var yaesuMode = HamlibToYaesuMode.TryGetValue(hamlibMode, out var mapped)
                ? mapped
                : hamlibMode;
            var command = CatCommands.FormatMode(yaesuMode, false);
            await _multiplexer.SendCommandAsync(command, clientId);
            return "RPRT 0";
        }

        private async Task<string> GetPttAsync(string clientId)
        {
            var response = await _multiplexer.SendCommandAsync("TX", clientId);
            return response?.Contains("TX1") == true ? "1" : "0";
        }

        private async Task<string> SetPttAsync(string ptt, string clientId)
        {
            // Hamlib set_ptt values: 0=off, 1=on, 2=on (mic), 3=on (data) — WSJT-X in
            // Data/Pkt PTT mode sends 3, not 1, so any nonzero value must key the radio.
            var keyed = ptt != "0";
            var command = keyed ? "TX1;" : "TX0;";
            _logger.LogInformation("Rigctld set_ptt: {Ptt} (client: {ClientId})", ptt, clientId);
            await _multiplexer.SendCommandAsync(command, clientId);

            if (keyed)
                ArmTxWatchdog(clientId);
            else
                DisarmTxWatchdog();

            return "RPRT 0";
        }

        // Start (or restart) the TX safety timeout for a rigctld-initiated key-up.
        private void ArmTxWatchdog(string clientId)
        {
            CancellationToken token;
            lock (_pttLock)
            {
                _pttWatchdogCts?.Cancel();
                _pttWatchdogCts?.Dispose();
                _pttWatchdogCts = new CancellationTokenSource();
                _rigctldPttActive = true;
                _pttClientId = clientId;
                token = _pttWatchdogCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(TxSafetyTimeoutSeconds), token);
                }
                catch (OperationCanceledException)
                {
                    return; // Released normally, or re-armed by a newer key-up.
                }

                bool stillActive;
                lock (_pttLock)
                {
                    stillActive = _rigctldPttActive;
                    _rigctldPttActive = false;
                    _pttClientId = null;
                }

                if (stillActive)
                {
                    _logger.LogWarning(
                        "TX safety watchdog: PTT held via rigctld for over {Seconds}s with no release (client: {ClientId}). Forcing TX0;.",
                        TxSafetyTimeoutSeconds, clientId);
                    try { await _multiplexer.SendCommandAsync("TX0;", clientId); }
                    catch (Exception ex) { _logger.LogError(ex, "TX safety watchdog: failed to force TX0;."); }
                }
            });
        }

        // Cancel the TX safety timeout after a normal release.
        private void DisarmTxWatchdog()
        {
            lock (_pttLock)
            {
                _pttWatchdogCts?.Cancel();
                _pttWatchdogCts?.Dispose();
                _pttWatchdogCts = null;
                _rigctldPttActive = false;
                _pttClientId = null;
            }
        }

        // On client disconnect: if that client keyed the radio and never released
        // it, force the radio back to RX so a dropped connection can't leave it keyed.
        private async Task ReleasePttIfClientHeldAsync(string clientId)
        {
            bool shouldRelease = false;
            lock (_pttLock)
            {
                if (_rigctldPttActive && _pttClientId == clientId)
                {
                    shouldRelease = true;
                    _pttWatchdogCts?.Cancel();
                    _pttWatchdogCts?.Dispose();
                    _pttWatchdogCts = null;
                    _rigctldPttActive = false;
                    _pttClientId = null;
                }
            }

            if (shouldRelease)
            {
                _logger.LogWarning(
                    "rigctld client {ClientId} disconnected while holding PTT. Forcing TX0; for safety.", clientId);
                try { await _multiplexer.SendCommandAsync("TX0;", clientId); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to force TX0; after client disconnect."); }
            }
        }

        private string SetVfo(string vfo)
        {
            // Always ignore VFO selection and force VFOA
            _logger.LogInformation("WSJT-X requested VFO change to {Vfo}, but only VFOA is supported. Ignoring.", vfo);
            return "RPRT 0";
        }

        private string GetVfo()
        {
            return "VFOA";
        }

        // Hamlib's get_split_vfo is two values: split on/off, then the TX VFO.
        private string GetSplit() => _splitEnabled ? "1\nVFOB" : "0\nVFOA";
        private string SetSplit(string enabled)
        {
            _splitEnabled = enabled == "1";
            return "RPRT 0";
        }

        private string GetSplitFrequency() => _splitFrequency.ToString();
        private string SetSplitFrequency(string freqStr)
        {
            // Hamlib sends "14076000.000000", as for set_freq.
            if (double.TryParse(freqStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz)
                && (long)Math.Round(hz) is var freq && IsTunable(freq))
            {
                _splitFrequency = freq;
                return "RPRT 0";
            }
            return "RPRT -1";
        }

        private string GetRit() => _ritOffset.ToString();
        private string SetRit(string offsetStr)
        {
            if (int.TryParse(offsetStr, out var offset) && offset >= -9990 && offset <= 9990)
            {
                _ritOffset = offset;
                return "RPRT 0";
            }
            return "RPRT -1";
        }

        private string GetXit() => _xitOffset.ToString();
        private string SetXit(string offsetStr)
        {
            if (int.TryParse(offsetStr, out var offset) && offset >= -9990 && offset <= 9990)
            {
                _xitOffset = offset;
                return "RPRT 0";
            }
            return "RPRT -1";
        }

        // --- Functions (get_func / set_func) ---
        // Only RIG_FUNC_TUNER is implemented — the ATU. Anything else falls
        // through to RPRT -1, matching every other unimplemented rigctld
        // command in this file.
        private async Task<string> GetFuncAsync(string func, string clientId)
        {
            if (func.ToUpperInvariant() != "TUNER")
                return "RPRT -1";

            // AC reply format after the multiplexer strips the trailing ';':
            //   "AC" P1 P2 P3   — 5 characters, P3 at index 4.
            //   P3: 0=Tuner OFF, 1=Tuner ON, 2=Tuning Start/Stop
            // Hamlib's get_func model is boolean, but the radio has a third
            // "tuning in progress" state — report "on" during tuning too,
            // since the tuner is engaged either way.
            var response = await _multiplexer.SendCommandAsync("AC", clientId);
            if (!string.IsNullOrEmpty(response) && response.StartsWith("AC") && response.Length >= 5)
            {
                var enabled = response[4] != '0';
                _radioStateService.AtuEnabled = enabled;
                return enabled ? "1" : "0";
            }
            return "RPRT -1";
        }

        private async Task<string> SetFuncAsync(string func, string value, string clientId)
        {
            if (func.ToUpperInvariant() != "TUNER")
                return "RPRT -1";

            // AC P1 P2 P3 ;  — P3=1 ATU ON, P3=0 ATU OFF (P1/P2 always 0).
            // Same command the front-end ATU button uses (CatController.SetAtu).
            var enabled = value == "1";
            await _multiplexer.SendCommandAsync(enabled ? "AC001;" : "AC000;", clientId);
            _radioStateService.AtuEnabled = enabled;
            _logger.LogInformation("Rigctld set_func TUNER: {Enabled} (client: {ClientId})", enabled, clientId);
            return "RPRT 0";
        }

        private async Task<string> GetLevelAsync(string level, string clientId)
        {
            if (level.ToUpper() == "STRENGTH")
                return await GetSignalStrengthAsync(clientId);
            // Add more levels as needed
            return "0";
        }

        private string SetLevel(string level, string value)
        {
            // Stub: accept but do nothing
            return "RPRT 0";
        }

        private async Task<string> GetSignalStrengthAsync(string clientId)
        {
            var response = await _multiplexer.SendCommandAsync("SM0", clientId);
            var sMeter = CatCommands.ParseSMeter(response ?? string.Empty);

            var dbm = (sMeter / 255.0) * 60 - 60;
            return $"{dbm:F0}";
        }

        private string GetInfo()
        {
            // Example info string for FTdx101MP (Hamlib expects: "mfg;model;version;serial;id")
            return "Yaesu;FTDX101MP;1.0.0;000000;3115";
        }

        internal static string GetDumpState()
        {
            // Minimal Hamlib dump_state response, as v2.1.0 shipped apart from
            // the ITU region line. Hamlib's netrigctl reads protocol, model,
            // then the region; before 2026-11-20 the region line was missing,
            // so Hamlib took the HF RX range as the region and kept only 6 m.
            // Found in IWC (D#51); Log4OM's frequency display problem may be
            // related, so check it again with this in place.
            return string.Join("\n",
                "0",            // protocol version
                "2",            // rig model (2 = NET rigctl)
                "0",            // ITU region
                "1800000.000000 30000000.000000 0x1ff -1 -1 0x10000003 0x3",  // HF RX range
                "50000000.000000 54000000.000000 0x1ff -1 -1 0x10000003 0x3", // 6m RX range
                "0 0 0 0 0 0 0",  // end of RX ranges
                "1800000.000000 30000000.000000 0x1ff 5 200 0x10000003 0x3",  // HF TX range
                "50000000.000000 54000000.000000 0x1ff 5 100 0x10000003 0x3", // 6m TX range
                "0 0 0 0 0 0 0",  // end of TX ranges
                "0 0",          // end of tuning steps
                "0 0",          // end of filters
                "0",            // max RIT
                "0",            // max XIT
                "0",            // max IF-shift
                "0",            // announces
                "0",            // preamp list
                "0",            // attenuator list
                "0x00000003",   // has_get_func
                "0x00000003",   // has_set_func
                "0x000fffff",   // has_get_level
                "0x000fffff",   // has_set_level
                "0",            // has_get_parm
                "0"             // has_set_parm
            );
        }

        // --- Band change support using BSxx; command ---
        private async Task<string> SetBandAsync(string band, string clientId)
        {
            if (BandCodes.TryGetValue(band.ToLower(), out var code))
            {
                // Always send to VFO A
                await _multiplexer.SendCommandAsync($"BS{code};", clientId);
                return "RPRT 0";
            }
            // Try parsing as frequency in Hz
            if (long.TryParse(band, out var freqHz) && IsTunable(freqHz))
            {
                var command = CatCommands.FormatFrequencyA(freqHz);
                await _multiplexer.SendCommandAsync(command, clientId);
                return "RPRT 0";
            }
            return "RPRT -1";
        }
    }
}