using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Yaesu_Web_Control.Services.N1mm
{
    /// <summary>
    /// A TCP CAT port for N1MM Logger+, so N1MM can control the radio while
    /// Yaesu Web Control holds its COM port, with no virtual COM port pair.
    /// In N1MM: Config, Configure Ports, Port = TCP, Radio = the radio's own
    /// model (FTDX101D for an FTdx101MP or D), IP Addr:Port = 127.0.0.1 and
    /// the port set here. Off by default.
    ///
    /// N1MM keys the radio itself (CAT TX1, or DTR/RTS on the Standard port,
    /// which this server never touches); commands pass through as N1MM sent
    /// them, as through a port splitter. Two safety nets, as in
    /// <see cref="RigctldServer"/>: if the connection that sent TX1 drops, or
    /// TX1 is held past <see cref="TxSafetyTimeoutSeconds"/>, the server
    /// sends TX0.
    ///
    /// Settings are read every two seconds, so turning it on or changing the
    /// port takes effect without a restart.
    /// </summary>
    public sealed class N1mmCatServer : BackgroundService
    {
        public const int DefaultPort = 4535;
        public const int TxSafetyTimeoutSeconds = 180;
        // What goes in the log for each N1MM connection, so a tester's log
        // shows what N1MM sent (#394: band buttons, a lock-up). Sets are few
        // (band and mode changes, band map clicks, macros); reads are polls.
        private const int UnansweredLogged = 50;
        private const int SetsLogged = 500;
        private const int SlowBatchMs = 1500;
        private const int SlowBatchesLogged = 20;
        private static readonly TimeSpan SettingsPoll = TimeSpan.FromSeconds(2);

        private readonly ICatPassThrough _link;
        private readonly ISettingsService _settings;
        private readonly RadioStateService _radio;
        private readonly ILogger<N1mmCatServer> _logger;

        private (int Port, bool Network)? _running;
        private TcpListener? _listener;
        private CancellationTokenSource? _listenerCts;
        private int _clientCount;

        public N1mmCatServer(CatMultiplexerService mux, ISettingsService settings, RadioStateService radio, ILogger<N1mmCatServer> logger)
        {
            _link = new MultiplexerPassThrough(mux);
            _settings = settings;
            _radio = radio;
            _logger = logger;
        }

        /// <summary>For the Settings page: listening, and how many N1MM connections.</summary>
        public (bool Listening, int Port, int Clients) Status =>
            (_running is not null, _running?.Port ?? 0, Volatile.Read(ref _clientCount));

        public static int ClampPort(int port) => port is >= 1024 and <= 65535 ? port : DefaultPort;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    Reconcile(stoppingToken);
                    await Task.Delay(SettingsPoll, stoppingToken);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                StopListener();
            }
        }

        private void Reconcile(CancellationToken stoppingToken)
        {
            (int, bool)? wanted = null;
            try
            {
                var s = _settings.GetCachedSettings();
                // The five-byte binary CAT radios (FT-847, FT-817 ...) don't
                // speak the text CAT this server passes on.
                if (s.N1mmCatServerEnabled && !RadioCapabilities.UsesFiveByteCat(_radio.RadioModel))
                    wanted = (ClampPort(s.N1mmCatServerPort), s.N1mmCatServerAllowNetwork);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "N1MM CAT server: settings not readable yet");
                return;
            }

            if (wanted == _running) return;
            StopListener();
            if (wanted is { } w) StartListener(w.Item1, w.Item2, stoppingToken);
        }

        private void StartListener(int port, bool network, CancellationToken stoppingToken)
        {
            var address = network ? IPAddress.Any : IPAddress.Loopback;
            try
            {
                var listener = new TcpListener(address, port);
                listener.Start();
                _listener = listener;
                _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _running = (port, network);
                _logger.LogInformation("N1MM CAT server listening on {Address}:{Port}", address, port);
                _ = AcceptLoopAsync(listener, _listenerCts.Token);
            }
            catch (SocketException ex)
            {
                // Port in use: try again on the next settings poll, quietly.
                if (_running is null)
                    _logger.LogWarning("N1MM CAT server couldn't listen on port {Port}: {Message}. Choose another port in Settings.", port, ex.Message);
                _running = null;
                _listener = null;
            }
        }

        private void StopListener()
        {
            if (_running is null && _listener is null) return;
            try { _listenerCts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listenerCts?.Dispose();
            _listenerCts = null;
            _listener = null;
            if (_running is not null) _logger.LogInformation("N1MM CAT server stopped");
            _running = null;
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleClientAsync(client, token), token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            var who = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            Interlocked.Increment(ref _clientCount);
            _logger.LogInformation("N1MM CAT client connected: {Who}", who);
            var session = new N1mmCatSession(_link);
            // The first few reads the radio didn't answer go in the log, so a
            // tester's log shows what N1MM was waiting for (#394).
            session.OnUnanswered = command =>
            {
                if (session.Unanswered <= UnansweredLogged)
                    _logger.LogWarning("N1MM CAT client {Who}: the radio didn't answer {Command} within {Ms} ms",
                        who, command, MultiplexerPassThrough.ReadTimeoutMs);
            };
            session.OnSet = command =>
            {
                if (session.Sets <= SetsLogged)
                    _logger.LogInformation("N1MM CAT client {Who} sent {Command}", who, command);
            };
            CancellationTokenSource? watchdog = null;
            bool hinted = false;
            int slowBatches = 0;
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    var buffer = new byte[1024];
                    while (!token.IsCancellationRequested)
                    {
                        int n = await stream.ReadAsync(buffer, token);
                        if (n == 0) break;
                        var wasKeyed = session.Keyed;
                        var before = session.Commands;
                        var started = System.Diagnostics.Stopwatch.StartNew();
                        await session.ProcessAsync(Encoding.ASCII.GetString(buffer, 0, n),
                            (answer, ct) => stream.WriteAsync(Encoding.ASCII.GetBytes(answer), ct).AsTask(), token);
                        if (started.ElapsedMilliseconds > SlowBatchMs && ++slowBatches <= SlowBatchesLogged)
                            _logger.LogWarning("N1MM CAT client {Who}: {Count} commands took {Ms} ms, so N1MM was kept waiting",
                                who, session.Commands - before, started.ElapsedMilliseconds);

                        if (!hinted && session.FirstInvalid is { } bad)
                        {
                            hinted = true;
                            _logger.LogWarning(
                                "N1MM CAT client {Who} sent something that isn't Yaesu CAT ({Text}). In N1MM's Configure Ports, set the Radio to your Yaesu model (FTDX101D for an FTdx101MP).",
                                who, bad);
                        }

                        if (session.Keyed && !wasKeyed)
                        {
                            _logger.LogInformation("N1MM CAT client {Who} keyed the radio (TX1)", who);
                            watchdog?.Cancel();
                            watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
                            _ = ReleaseAfterTimeoutAsync(session, who, watchdog.Token);
                        }
                        else if (!session.Keyed && wasKeyed)
                        {
                            watchdog?.Cancel();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "N1MM CAT client {Who} error", who);
            }
            finally
            {
                watchdog?.Cancel();
                if (session.Keyed)
                {
                    _logger.LogWarning("N1MM CAT client {Who} disconnected while it had the radio keyed. Sending TX0.", who);
                    await ForceReceiveAsync();
                }
                Interlocked.Decrement(ref _clientCount);
                _logger.LogInformation("N1MM CAT client disconnected: {Who} ({Count} commands, {Sets} sets, {Unanswered} reads unanswered)",
                    who, session.Commands, session.Sets, session.Unanswered);
            }
        }

        private async Task ReleaseAfterTimeoutAsync(N1mmCatSession session, string who, CancellationToken token)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(TxSafetyTimeoutSeconds), token);
            }
            catch (OperationCanceledException) { return; }
            if (!session.Keyed) return;
            _logger.LogWarning("TX safety: N1MM CAT client {Who} held TX1 for over {Seconds}s. Sending TX0.", who, TxSafetyTimeoutSeconds);
            await ForceReceiveAsync();
            session.ClearKeyed();
        }

        private async Task ForceReceiveAsync()
        {
            try
            {
                if (_link.IsConnected) await _link.WriteAsync("TX0;", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "N1MM CAT server: TX0 failed");
            }
        }
    }
}
