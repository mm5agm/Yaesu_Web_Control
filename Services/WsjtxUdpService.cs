using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

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
        // Removed hardcoded port; will use settings
        private const uint MagicNumber = 0xADBCCBDA;
        private const uint MessageTypeHeartbeat = 0;
        private const uint MessageTypeStatus = 1;
        private const uint MessageTypeClose = 6;

        private readonly ICatClient _catClient;
        private readonly RadioStateService _radioStateService;
        private readonly ILogger<WsjtxUdpService> _logger;
        private readonly ISettingsService _settingsService;

        private readonly object _lock = new();
        private DateTime _lastSeen = DateTime.MinValue;
        private bool _isTransmitting;
        private string _wsjtxMode = "";
        private string _wsjtxId = "";

        public bool IsConnected { get { lock (_lock) return (DateTime.UtcNow - _lastSeen).TotalSeconds < 30; } }
        public bool IsTransmitting { get { lock (_lock) return _isTransmitting; } }
        public string WsjtxMode { get { lock (_lock) return _wsjtxMode; } }
        public string WsjtxId { get { lock (_lock) return _wsjtxId; } }

        public WsjtxUdpService(
            ICatClient catClient,
            RadioStateService radioStateService,
            ILogger<WsjtxUdpService> logger,
            ISettingsService settingsService)
        {
            _catClient = catClient;
            _radioStateService = radioStateService;
            _logger = logger;
            _settingsService = settingsService;
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
                _logger.LogInformation("WSJT-X UDP listener ACTIVE on port {Port} (address filter: {Address})", settings.WsjtxUdpPort, settings.WsjtxUdpAddress);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(stoppingToken);
                        // Debug: WSJT-X sends a status packet roughly once a second
                        // while it is running, so this is a steady stream, not an event.
                        _logger.LogDebug("[WSJT-X UDP] Packet received: {Length} bytes from {RemoteEndPoint}", result.Buffer.Length, result.RemoteEndPoint);
                        await ProcessMessageAsync(result.Buffer, stoppingToken);
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

        private async Task ProcessMessageAsync(byte[] data, CancellationToken ct)
        {
            // Parse synchronously (no spans across await boundaries)

            var msg = TryParseMessage(data);
            if (msg == null)
            {
                // Log first 16 bytes of the packet for debugging
                var hex = BitConverter.ToString(data.Take(16).ToArray());
                _logger.LogWarning("[WSJT-X UDP] Failed to parse message. First 16 bytes: {Hex}", hex);
                return;
            }

            lock (_lock)
            {
                _lastSeen = DateTime.UtcNow;
                _wsjtxId = msg.Id;
            }

            switch (msg.Type)
            {
                case MessageTypeHeartbeat:
                    _logger.LogDebug("WSJT-X Heartbeat from '{Id}'", msg.Id);
                    break;

                case MessageTypeStatus:
                    lock (_lock)
                    {
                        _isTransmitting = msg.Transmitting;
                        _wsjtxMode = msg.Mode;
                    }

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
                    }
                    else if (msg.DialFrequency > 0)
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

                case MessageTypeClose:
                    _logger.LogInformation("WSJT-X '{Id}' closed", msg.Id);
                    lock (_lock)
                    {
                        _isTransmitting = false;
                        _lastSeen = DateTime.MinValue;
                    }
                    break;
            }
        }

        // --- Message parsing (all synchronous — spans are safe here) ---

        private record ParsedMessage(uint Type, string Id, long DialFrequency, bool Transmitting, string Mode);

        private static ParsedMessage? TryParseMessage(byte[] data)
        {
            if (data.Length < 8) return null;
            try
            {
                var span = data.AsSpan();
                int offset = 0;

                uint magic = ReadUInt32(span, ref offset);
                if (magic != MagicNumber) return null;

                ReadUInt32(span, ref offset); // schema version
                uint type = ReadUInt32(span, ref offset);
                string id = ReadQString(span, ref offset) ?? "";

                long dialFrequency = 0;
                bool transmitting = false;
                string mode = "";

                if (type == MessageTypeStatus)
                {
                    dialFrequency = (long)ReadUInt64(span, ref offset);
                    mode = ReadQString(span, ref offset) ?? "";
                    ReadQString(span, ref offset); // DX Call
                    ReadQString(span, ref offset); // Report
                    ReadQString(span, ref offset); // TX Mode
                    ReadBool(span, ref offset);    // TX Enabled
                    transmitting = ReadBool(span, ref offset);
                }

                return new ParsedMessage(type, id, dialFrequency, transmitting, mode);
            }
            catch
            {
                return null;
            }
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int offset)
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            offset += 4;
            return value;
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int offset)
        {
            var value = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));
            offset += 8;
            return value;
        }

        private static bool ReadBool(ReadOnlySpan<byte> data, ref int offset)
        {
            return data[offset++] != 0;
        }

        private static string? ReadQString(ReadOnlySpan<byte> data, ref int offset)
        {
            if (offset + 4 > data.Length) return null;
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            offset += 4;
            int length = (int)rawLength;
            if (length == -1) return null;       // null Qt string
            if (length == 0) return string.Empty;
            if (offset + length > data.Length) return null;
            var str = Encoding.UTF8.GetString(data.Slice(offset, length));
            offset += length;
            return str;
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
