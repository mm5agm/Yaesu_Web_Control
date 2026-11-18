using System.Net;
using System.Net.Sockets;
using System.Text;
using Yaesu_Web_Control.Services.Wsjtx;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Listens for WSJT-X UDP status broadcasts and syncs frequency/TX state to the app.
    /// WSJT-X default UDP address: 224.0.0.1:2237 (multicast).
    /// Frequency changes from WSJT-X are also handled via the rigctld TCP connection (port 4532);
    /// this service provides real-time redundancy and exposes TX status to the web UI.
    /// </summary>
    public class WsjtxUdpService : BackgroundService
    {

        private readonly ICatClient _catClient;
        private readonly RadioStateService _radioStateService;
        private readonly ILogger<WsjtxUdpService> _logger;
        private readonly ISettingsService _settingsService;
        private readonly FrequencyRejectionNotifier _rejectionNotifier;

        private readonly object _lock = new();
        private DateTime _lastSeen = DateTime.MinValue;
        private bool _isTransmitting;
        private string _wsjtxMode = "";
        private string _wsjtxId = "";
        private long _dialFrequency;
        private bool _txEnabled;
        private string _dxCall = "";
        private string _deCall = "";

        // Where WSJT-X/WSJT-Z sends from. Requests (Reply, HaltTx, Configure)
        // go back to this endpoint, which is the socket it listens on.
        private IPEndPoint? _wsjtxEndPoint;
        private UdpClient? _udpClient;

        // After the big-tile page changes band, WSJT-X keeps broadcasting the old
        // dial frequency until it next polls the radio. Without this pause the
        // Status handler below would put the radio straight back on the old band.
        private DateTime _dialSyncPausedUntil = DateTime.MinValue;

        /// <summary>Stations the big-tile page can call (issue #332).</summary>
        public WsjtxCallList CallList { get; } = new();

        // Where in the audio WSJT-X has decoded something lately, for the
        // audio waterfall's mode labels.
        private readonly List<(double OffsetHz, string Mode, DateTime At)> _recentDecodes = new();
        private const int MaxRecentDecodes = 400;

        /// <summary>The audio offsets and modes WSJT-X decoded within maxAge.</summary>
        public IReadOnlyList<Audio.AudioDecodeHint> RecentDecodeHints(TimeSpan maxAge)
        {
            var since = DateTime.UtcNow - maxAge;
            lock (_lock)
                return _recentDecodes.Where(d => d.At >= since)
                    .Select(d => new Audio.AudioDecodeHint(d.OffsetHz, d.Mode)).ToList();
        }

        /// <summary>
        /// A decode's mode: WSJT-X marks FT8 "~" and FT4 "+"; anything else
        /// takes the mode from its last Status.
        /// </summary>
        public static string DecodeModeName(string symbol, string statusMode) => symbol switch
        {
            "~" => "FT8",
            "+" => "FT4",
            _ => string.IsNullOrWhiteSpace(statusMode) ? "WSJT-X" : statusMode.Trim(),
        };

        internal void RememberDecode(WsjtxDecode decode, DateTime utc)
        {
            lock (_lock)
            {
                _recentDecodes.Add((decode.DeltaFrequency, DecodeModeName(decode.Mode, _wsjtxMode), utc));
                if (_recentDecodes.Count > MaxRecentDecodes)
                    _recentDecodes.RemoveRange(0, _recentDecodes.Count - MaxRecentDecodes);
            }
        }

        public bool IsConnected { get { lock (_lock) return (DateTime.UtcNow - _lastSeen).TotalSeconds < 30; } }
        public bool IsTransmitting { get { lock (_lock) return _isTransmitting; } }
        public string WsjtxMode { get { lock (_lock) return _wsjtxMode; } }
        public string WsjtxId { get { lock (_lock) return _wsjtxId; } }
        public long DialFrequency { get { lock (_lock) return _dialFrequency; } }
        public bool TxEnabled { get { lock (_lock) return _txEnabled; } }
        public string DxCall { get { lock (_lock) return _dxCall; } }
        public string DeCall { get { lock (_lock) return _deCall; } }

        /// <summary>Stops the WSJT-X dial frequency being copied to the radio for a while.</summary>
        public void PauseDialSync(TimeSpan duration)
        {
            lock (_lock) _dialSyncPausedUntil = DateTime.UtcNow + duration;
        }

        /// <summary>
        /// Asks WSJT-X/WSJT-Z to answer a station from the call list, as if the
        /// operator had double-clicked its latest decode. This can make it
        /// transmit, if Enable Tx is on: it is the operator's own call.
        /// </summary>
        public async Task<(bool Ok, string? Error)> SendReplyAsync(string call)
        {
            if (!CallList.TryGet(call, out var entry) || entry is null)
                return (false, $"{call} is no longer in the list");
            return await SendAsync(id => WsjtxProtocol.BuildReply(entry.Decode, id), $"Reply to {entry.Call}");
        }

        /// <summary>Stops transmitting now (autoTxOnly=false) or just turns Enable Tx off.</summary>
        public Task<(bool Ok, string? Error)> SendHaltTxAsync(bool autoTxOnly) =>
            SendAsync(id => WsjtxProtocol.BuildHaltTx(id, autoTxOnly), autoTxOnly ? "Halt auto Tx" : "Halt Tx now");

        /// <summary>
        /// WSJT-Z only: switches Auto Call on or off. Auto CQ is always switched
        /// off, because WSJT-Z sets both from the same message.
        /// </summary>
        public Task<(bool Ok, string? Error)> SendAutoCallAsync(bool on) =>
            SendAsync(id => WsjtxProtocol.BuildAutoConfigure(id, autoCq: false, autoCall: on), on ? "Auto Call on" : "Auto Call off");

        private async Task<(bool Ok, string? Error)> SendAsync(Func<string, byte[]> build, string what)
        {
            UdpClient? udp;
            IPEndPoint? target;
            string id;
            lock (_lock)
            {
                udp = _udpClient;
                target = _wsjtxEndPoint;
                id = _wsjtxId;
            }
            if (udp is null) return (false, "WSJT-X integration is off in YWC's settings");
            if (target is null || !IsConnected) return (false, "WSJT-X is not sending to YWC");
            var bytes = build(id);
            await udp.SendAsync(bytes, bytes.Length, target);
            _logger.LogInformation("[WSJT-X UDP] Sent {What} to '{Id}' at {EndPoint}", what, id, target);
            return (true, null);
        }

        public WsjtxUdpService(
            ICatClient catClient,
            RadioStateService radioStateService,
            ILogger<WsjtxUdpService> logger,
            ISettingsService settingsService,
            FrequencyRejectionNotifier rejectionNotifier)
        {
            _catClient = catClient;
            _radioStateService = radioStateService;
            _logger = logger;
            _settingsService = settingsService;
            _rejectionNotifier = rejectionNotifier;
            _logger.LogInformation("WsjtxUdpService constructor called. Service is being constructed.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("WsjtxUdpService ExecuteAsync started - listening for WSJT-X UDP packets");
            UdpClient? udpClient = null;
            try
            {
                _logger.LogDebug("Loading WSJT-X UDP settings...");
                var settings = await _settingsService.GetSettingsAsync();
                if (!settings.WsjtxIntegrationEnabled)
                {
                    _logger.LogInformation("WSJT-X integration is disabled in settings — not binding the UDP port, so it stays free for other WSJT-X tools.");
                    return;
                }
                _logger.LogInformation("WSJT-X UDP Settings: Address={Address}, Port={Port}", settings.WsjtxUdpAddress, settings.WsjtxUdpPort);
                udpClient = CreateUdpListener(settings.WsjtxUdpAddress, settings.WsjtxUdpPort);
                lock (_lock) _udpClient = udpClient;
                _logger.LogInformation("WSJT-X UDP listener ACTIVE on port {Port} (address filter: {Address})", settings.WsjtxUdpPort, settings.WsjtxUdpAddress);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(stoppingToken);
                        // Debug: WSJT-X sends a status packet roughly once a second
                        // while it is running, so this is a steady stream, not an event.
                        _logger.LogDebug("[WSJT-X UDP] Packet received: {Length} bytes from {RemoteEndPoint}", result.Buffer.Length, result.RemoteEndPoint);
                        await ProcessMessageAsync(result.Buffer, result.RemoteEndPoint, stoppingToken);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing WSJT-X UDP packet");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WSJT-X UDP service failed to start or crashed");
            }
            finally
            {
                lock (_lock) _udpClient = null;
                udpClient?.Close();
                _logger.LogInformation("WSJT-X UDP listener stopped");
                _logger.LogInformation("WsjtxUdpService ExecuteAsync has exited. Service should be stopped.");
            }

        }

        // --- Message parsing (all synchronous — spans are safe here) ---

        private UdpClient CreateUdpListener(string udpAddress, int udpPort)
        {
            var udp = new UdpClient();
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));

            _logger.LogInformation("UDP socket bound to port {Port} on all interfaces", udpPort);

            // If multicast address, join group on default interface and also on loopback.
            // WSJT-X may be configured with "Outgoing Interfaces: loopback_0", in which case
            // packets only arrive on the loopback adapter and won't be seen if we only join
            // on the default (LAN) interface.
            if (IPAddress.TryParse(udpAddress, out var ip) &&
                ip.AddressFamily == AddressFamily.InterNetwork &&
                ip.IsMulticast())
            {
                try
                {
                    udp.JoinMulticastGroup(ip);
                    _logger.LogInformation("✓ Joined WSJT-X multicast group {Address}:{Port} on default interface", udpAddress, udpPort);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "✗ FAILED to join multicast group {Address} on default interface - check firewall settings", udpAddress);
                }

                try
                {
                    udp.JoinMulticastGroup(ip, IPAddress.Loopback);
                    _logger.LogInformation("✓ Joined WSJT-X multicast group {Address}:{Port} on loopback interface", udpAddress, udpPort);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not join multicast {Address} on loopback (non-fatal): {Message}", udpAddress, ex.Message);
                }
            }
            else
            {
                _logger.LogInformation("Listening for unicast UDP on port {Port} (address {Address} is not multicast)", udpPort, udpAddress);
            }
            return udp;
        }

        private async Task ProcessMessageAsync(byte[] data, IPEndPoint sender, CancellationToken ct)
        {
            if (!WsjtxProtocol.TryReadHeader(data, out var header))
            {
                // Log first 16 bytes of the packet for debugging
                var hex = BitConverter.ToString(data.Take(16).ToArray());
                _logger.LogWarning("[WSJT-X UDP] Failed to parse message. First 16 bytes: {Hex}", hex);
                return;
            }

            // Our own requests come back to us when WSJT-X uses multicast.
            if (header.Type is WsjtxProtocol.TypeReply or WsjtxProtocol.TypeHaltTx or WsjtxProtocol.TypeConfigure)
                return;

            lock (_lock)
            {
                _lastSeen = DateTime.UtcNow;
                _wsjtxId = header.Id;
                _wsjtxEndPoint = sender;
            }

            switch (header.Type)
            {
                case WsjtxProtocol.TypeHeartbeat:
                    _logger.LogDebug("WSJT-X Heartbeat from '{Id}'", header.Id);
                    break;

                case WsjtxProtocol.TypeDecode:
                    var decode = WsjtxProtocol.TryParseDecode(data);
                    if (decode is not null)
                    {
                        CallList.Add(decode, DateTime.UtcNow);
                        RememberDecode(decode, DateTime.UtcNow);
                    }
                    break;

                case WsjtxProtocol.TypeClear:
                    CallList.Clear();
                    break;

                case WsjtxProtocol.TypeStatus:
                    var msg = WsjtxProtocol.TryParseStatus(data);
                    if (msg is null) break;
                    bool bandChanged;
                    bool syncPaused;
                    lock (_lock)
                    {
                        bandChanged = _dialFrequency != 0 && Math.Abs(msg.DialFrequency - _dialFrequency) > 100_000;
                        _isTransmitting = msg.Transmitting;
                        _wsjtxMode = msg.Mode;
                        _dialFrequency = msg.DialFrequency;
                        _txEnabled = msg.TxEnabled;
                        _dxCall = msg.DxCall;
                        _deCall = msg.DeCall;
                        syncPaused = DateTime.UtcNow < _dialSyncPausedUntil;
                    }
                    CallList.MyCall = msg.DeCall;
                    if (bandChanged) CallList.Clear();

                    // Log every status message with frequency info for debugging
                    _logger.LogDebug("WSJT-X Status: Id={Id}, DialFreq={Freq}, Mode={Mode}, TX={TX}",
                        msg.Id, msg.DialFrequency, msg.Mode, msg.Transmitting);

                    // Sync frequency to radio if meaningfully different (>100 Hz).
                    // WSJT-X sends dial frequency changes when:
                    // - Band is changed
                    // - User clicks outside current passband on wide graph
                    // - Split mode frequency changes
                    // Ignore a dial frequency the radio cannot tune. WSJT-X
                    // broadcasts whatever band it is sitting on, which need not
                    // be one this radio has: seen on 2026-08-14 with WSJT-X on
                    // 2 m, where 144.174 MHz was both written into VFO A
                    // (displayed with band "Unknown" until WSJT-X moved back to
                    // 20 m) and sent to the radio as FA144174000;. The rigctld
                    // path has always bounded this; this one never did, which is
                    // why the rigctld guard did not catch it.
                    if (msg.DialFrequency > 0
                        && !RadioCapabilities.IsTunableFrequency(
                               _radioStateService.RadioModel, msg.DialFrequency))
                    {
                        _logger.LogDebug(
                            "[WSJT-X UDP] Ignoring out-of-range dial frequency {Freq} Hz for {Model}",
                            msg.DialFrequency, _radioStateService.RadioModel);

                        // This path is otherwise completely silent to the
                        // operator — unlike rigctld, a UDP broadcast has no
                        // reply, so WSJT-X is never told and shows nothing.
                        // The notifier throttles: WSJT-X rebroadcasts this
                        // status about once a second while it sits on the band.
                        await _rejectionNotifier.NotifyAsync(msg.DialFrequency, "WSJT-X");
                    }
                    else if (msg.DialFrequency > 0 && !syncPaused)
                    {
                        var currentFreq = _radioStateService.FrequencyA;
                        var diff = Math.Abs(msg.DialFrequency - currentFreq);

                        if (diff > 100)
                        {
                            _logger.LogInformation("[WSJT-X UDP] Frequency change detected: {OldFreq} Hz → {NewFreq} Hz (diff={Diff} Hz)", 
                                currentFreq, msg.DialFrequency, diff);

                            // Send to radio
                            await _catClient.SetFrequencyAAsync(msg.DialFrequency);

                            // Update RadioStateService immediately so the UI updates via SignalR
                            _radioStateService.FrequencyA = msg.DialFrequency;
                        }
                    }
                    break;

                case WsjtxProtocol.TypeClose:
                    _logger.LogInformation("WSJT-X '{Id}' closed", header.Id);
                    lock (_lock)
                    {
                        _isTransmitting = false;
                        _lastSeen = DateTime.MinValue;
                    }
                    CallList.Clear();
                    break;
            }
        }
    }
    // Extension for multicast detection
    public static class IPAddressExtensions
    {
        public static bool IsMulticast(this IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] >= 224 && bytes[0] <= 239;
        }
    }
}
