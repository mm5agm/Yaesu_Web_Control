using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Models;
// DxSpot lives in the Radio_Web_Control_Core subtree at core/ — it is shared
// verbatim with Icom Web Control, because a cluster spot is the same thing
// whatever radio you are pointing at it.
using RadioWebControl.Core.Models;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Maintains a TCP connection to a DX cluster server, parses incoming spot
    /// lines into <see cref="DxSpot"/> records, keeps an in-memory ring buffer,
    /// and broadcasts new spots over SignalR.
    ///
    /// Disabled (silently does nothing) when the user has not configured a
    /// cluster host in Settings or has unticked the enable flag. Reconnects
    /// on its own if the connection drops.
    /// </summary>
    public class DxClusterService : BackgroundService
    {
        private const int RingBufferSize = 500;
        private const int ReconnectDelaySeconds = 15;

        private readonly ISettingsService _settingsService;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly ILogger<DxClusterService> _logger;

        private readonly LinkedList<DxSpot> _spots = new();
        private readonly object _spotsLock = new();

        // Recent raw lines from the cluster — captured for browser-visible
        // diagnostics so the user does not need to find the dev console.
        // Capped at 100 entries.
        private readonly LinkedList<string> _recentLines = new();
        private readonly object _recentLinesLock = new();
        public List<string> GetRecentLines()
        {
            lock (_recentLinesLock) return _recentLines.ToList();
        }

        // Diagnostic log file. Cleared on every new session so it does not
        // grow without bound. User can open this file in any text editor
        // to see exactly what the cluster has been sending.
        public static readonly string LogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "dx-cluster.log");
        private readonly object _logFileLock = new();

        private void AppendLogFile(string line)
        {
            try
            {
                lock (_logFileLock)
                    File.AppendAllText(LogFilePath, $"{DateTime.UtcNow:HH:mm:ss}  {line}{Environment.NewLine}");
            }
            catch { /* never let log file errors break the cluster session */ }
        }

        private void ResetLogFile()
        {
            try
            {
                lock (_logFileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                    File.WriteAllText(LogFilePath,
                        $"# DX cluster diagnostic log — opened {DateTime.UtcNow:u}{Environment.NewLine}");
                }
            }
            catch { }
        }

        // Connection lifecycle: "off" (disabled in Settings or missing fields),
        // "connecting" (TCP open in progress), "connected" (logged in / receiving),
        // "disconnected" (last attempt failed or remote dropped — service is
        // waiting before reconnecting). LastError holds the most recent failure
        // message so the UI can show why a connection isn't established.
        public string Status { get; private set; } = "off";
        public string LastError { get; private set; } = "";
        public int SpotCount { get { lock (_spotsLock) return _spots.Count; } }

        // Cluster lines tend to look like one of:
        //   "DX de F5OYE-#:   14074.0  W2AAA       FT8 RTTY                 1234Z"
        //   "DX de SP9XYZ:    7050.5   DL1ABC      CW EU                    0815Z"
        // Field widths vary by cluster software (AR-Cluster vs CC-Cluster vs DXSpider).
        // This regex is permissive on whitespace and tolerant of the spotter
        // suffix (-#, -@ etc.). It captures: 1=spotter, 2=freq-kHz, 3=callsign,
        // 4=comment-and-time.
        private static readonly Regex SpotRegex = new(
            @"^DX\s+de\s+([A-Z0-9/\-#@]+)\s*:\s*([\d.]+)\s+([A-Z0-9/]+)\s+(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public DxClusterService(
            ISettingsService settingsService,
            IHubContext<RadioHub> hubContext,
            ILogger<DxClusterService> logger)
        {
            _settingsService = settingsService;
            _hubContext = hubContext;
            _logger = logger;
        }

        /// <summary>Returns a snapshot of all current (non-aged-off) spots, newest first.</summary>
        public List<DxSpot> GetAllSpots()
        {
            lock (_spotsLock)
            {
                return _spots.OrderByDescending(s => s.ReceivedUtc).ToList();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[DxCluster] Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                ApplicationSettings settings;
                try { settings = await _settingsService.GetSettingsAsync(); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DxCluster] Failed to read settings — retrying in {Sec}s", ReconnectDelaySeconds);
                    await SafeDelay(ReconnectDelaySeconds, stoppingToken);
                    continue;
                }

                // Disabled or no host configured — idle until the user enables
                // it. Re-check every 15 s so a Settings save takes effect
                // without restarting the app.
                if (!settings.DxClusterEnabled
                    || string.IsNullOrWhiteSpace(settings.DxClusterHost)
                    || string.IsNullOrWhiteSpace(settings.DxClusterLoginCallsign))
                {
                    await SetStatus("off",
                        !settings.DxClusterEnabled ? "Cluster disabled in Settings" :
                        string.IsNullOrWhiteSpace(settings.DxClusterHost) ? "No cluster host configured" :
                        "No login callsign configured");
                    await SafeDelay(ReconnectDelaySeconds, stoppingToken);
                    continue;
                }

                AgeOffOldSpots(settings.DxSpotAgeMinutes);

                try
                {
                    await RunSession(settings, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DxCluster] Session ended unexpectedly — reconnecting in {Sec}s",
                        ReconnectDelaySeconds);
                    await SetStatus("disconnected", ex.Message);
                }

                await SafeDelay(ReconnectDelaySeconds, stoppingToken);
            }

            _logger.LogInformation("[DxCluster] Service stopped");
        }

        private async Task RunSession(ApplicationSettings settings, CancellationToken stoppingToken)
        {
            _logger.LogInformation("[DxCluster] Connecting to {Host}:{Port} as {Callsign}",
                settings.DxClusterHost, settings.DxClusterPort, settings.DxClusterLoginCallsign);

            ResetLogFile();
            AppendLogFile($"# Connecting to {settings.DxClusterHost}:{settings.DxClusterPort} as {settings.DxClusterLoginCallsign}");

            await SetStatus("connecting", $"Opening {settings.DxClusterHost}:{settings.DxClusterPort}");

            using var client = new TcpClient();
            // When the host begins shutting down, `StreamReader.ReadLineAsync`
            // over a `NetworkStream` does NOT honour its cancellation token on
            // Windows — the underlying TCP read keeps blocking until either
            // data arrives or the socket is closed. That meant `_lifetime.
            // StopApplication()` was taking up to 30 s to actually progress
            // past this service, because the read only unblocked when the
            // 30-second `lineCts.CancelAfter` deadline below fired.
            //
            // We tear the socket down aggressively when stoppingToken cancels:
            // `Shutdown(Both)` actively signals FIN to the remote so the OS
            // unblocks any pending recv immediately; then `Close()` releases
            // the handle. A plain `Close()` alone was observed to leave the
            // read blocked on this NetworkStream on Windows 11 (2026-06-04).
            //
            // The log line proves whether the callback actually fires — if it
            // does and the read still doesn't break, the problem is somewhere
            // else; if it doesn't, the registration isn't reaching the
            // cancelled token.
            using var closeOnShutdown = stoppingToken.Register(() =>
            {
                _logger.LogInformation("[DxCluster] stoppingToken fired — tearing down TcpClient");
                try { client.Client?.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { /* may already be closed */ }
                try { client.Close(); } catch { /* swallow — best-effort */ }
            });
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(settings.DxClusterHost ?? "", settings.DxClusterPort, connectCts.Token);

            await SetStatus("connected", "");

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };

            // Many DXSpider nodes send "login: " with no trailing newline,
            // which causes ReadLineAsync to hang forever waiting for a \n.
            // Send the callsign proactively after a short pause so the
            // cluster receives it whether the prompt has been sent or not.
            // DXSpider treats unsolicited input as the login response.
            await Task.Delay(1500, stoppingToken);
            await writer.WriteLineAsync(settings.DxClusterLoginCallsign);
            AppendLogFile($">> {settings.DxClusterLoginCallsign}");
            _logger.LogInformation("[DxCluster] Sent proactive login: {Callsign}", settings.DxClusterLoginCallsign);

            // Send any user-configured post-login commands (set/qra IO85CX,
            // set/name Colin, set/filter ... etc.). Pause briefly first so
            // the cluster has time to process the login.
            if (!string.IsNullOrWhiteSpace(settings.DxClusterPostLoginCommands))
            {
                await Task.Delay(1500, stoppingToken);
                foreach (var raw in settings.DxClusterPostLoginCommands.Split('\n'))
                {
                    var cmd = raw.Trim().TrimStart('/');
                    if (string.IsNullOrEmpty(cmd) || cmd.StartsWith("#")) continue;
                    await writer.WriteLineAsync(cmd);
                    AppendLogFile($">> {cmd}");
                    _logger.LogInformation("[DxCluster] Sent post-login command: {Cmd}", cmd);
                    await Task.Delay(250, stoppingToken);
                }
            }

            bool loggedIn = true;
            long ageOffCounter = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                // Read with a soft timeout so we can periodically age off old
                // spots even when the cluster is quiet.
                using var lineCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lineCts.CancelAfter(TimeSpan.FromSeconds(30));

                string? line;
                try
                {
                    line = await reader.ReadLineAsync(lineCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Read timeout — refresh age-off and continue.
                    if ((++ageOffCounter % 2) == 0)
                        AgeOffOldSpots(settings.DxSpotAgeMinutes);
                    continue;
                }

                if (line == null) break; // remote closed
                if (line.Length == 0) continue;

                // Log every line received. Three destinations:
                //   1. Standard ILogger (Debug — see below)
                //   2. Browser-visible ring buffer via /api/dxcluster/recent
                //   3. Diagnostic file (%APPDATA%\MM5AGM\Yaesu Web Control\dx-cluster.log)
                //
                // Debug, not Information: a busy cluster feed is a line every two
                // or three seconds, which was ~5 MB a day and the largest single
                // source left in the default log. Nothing is lost by demoting it —
                // destinations 2 and 3 both keep the full stream regardless of
                // log level, and (3) is the file to look at for cluster problems.
                _logger.LogDebug("[DxCluster] << {Line}", line);
                AppendLogFile($"<< {line}");
                lock (_recentLinesLock)
                {
                    _recentLines.AddLast($"{DateTime.UtcNow:HH:mm:ss}  {line}");
                    while (_recentLines.Count > 100) _recentLines.RemoveFirst();
                }

                // Login: most clusters print a prompt containing "call:" or
                // "callsign" or "login". Respond with the configured callsign.
                if (!loggedIn && (line.Contains("call:", StringComparison.OrdinalIgnoreCase)
                                || line.Contains("login", StringComparison.OrdinalIgnoreCase)
                                || line.Contains("Please enter", StringComparison.OrdinalIgnoreCase)))
                {
                    await writer.WriteLineAsync(settings.DxClusterLoginCallsign);
                    loggedIn = true;
                    _logger.LogInformation("[DxCluster] Sent login callsign");
                    continue;
                }

                // After login, some clusters still prompt for things ("name?",
                // "Welcome — set/name?", "QTH?"). Be forgiving: any line ending
                // with ":" or "?" after we've logged in, reply with the
                // callsign again. Harmless on clusters that don't need it.
                if (loggedIn && (line.TrimEnd().EndsWith(":") || line.TrimEnd().EndsWith("?")))
                {
                    await writer.WriteLineAsync(settings.DxClusterLoginCallsign);
                    _logger.LogInformation("[DxCluster] Replied to post-login prompt with callsign");
                    continue;
                }

                if (TryParseSpot(line, out var spot))
                {
                    // Re-fetch the watch list on every spot rather than using
                    // the `settings` captured at session start. SettingsService
                    // re-reads from disk on each GetSettingsAsync() call, so
                    // the captured local copy goes stale the moment the user
                    // edits the DX Watch popup via the UI — the file is
                    // updated but our captured reference still has the old
                    // list, and we'd keep alerting on removed entries until
                    // the cluster connection is recycled. (Bug reported by
                    // the user 2026-06-01: only G4* in the watch list, but
                    // EA* spots still triggered alerts.)
                    var liveSettings = await _settingsService.GetSettingsAsync();
                    spot.IsWatched = MatchesWatchList(spot.Callsign, liveSettings.DxClusterWatchedCallsigns ?? "");
                    AddSpot(spot);
                    await BroadcastSpot(spot);
                    if (spot.IsWatched)
                        await BroadcastAlert(spot);
                }
            }

            await SetStatus("disconnected", "Remote closed the connection");
        }

        /// <summary>
        /// Parses a single line from the cluster into a <see cref="DxSpot"/>.
        /// Returns false for any line that isn't a spot (login prompts,
        /// announcements, etc.). Public for unit testing.
        /// </summary>
        public static bool TryParseSpot(string line, out DxSpot spot)
        {
            spot = new DxSpot();
            var m = SpotRegex.Match(line.Trim());
            if (!m.Success) return false;

            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kHz))
                return false;

            spot.Spotter     = m.Groups[1].Value.TrimEnd('-', '#', '@');
            spot.FrequencyHz = (long)Math.Round(kHz * 1000.0);
            spot.Callsign    = m.Groups[3].Value.ToUpperInvariant();
            spot.Comment     = m.Groups[4].Value.Trim();
            spot.ReceivedUtc = DateTime.UtcNow;
            return true;
        }

        private void AddSpot(DxSpot spot)
        {
            lock (_spotsLock)
            {
                _spots.AddFirst(spot);
                while (_spots.Count > RingBufferSize)
                    _spots.RemoveLast();
            }
        }

        private void AgeOffOldSpots(int ageMinutes)
        {
            if (ageMinutes <= 0) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-ageMinutes);
            lock (_spotsLock)
            {
                var node = _spots.Last;
                while (node != null && node.Value.ReceivedUtc < cutoff)
                {
                    var prev = node.Previous;
                    _spots.Remove(node);
                    node = prev;
                }
            }
        }

        private async Task BroadcastSpot(DxSpot spot)
        {
            await _hubContext.Clients.All.SendAsync("RadioStateUpdate",
                new { property = "DxSpot", value = spot });
        }

        private async Task BroadcastAlert(DxSpot spot)
        {
            await _hubContext.Clients.All.SendAsync("RadioStateUpdate",
                new { property = "DxAlert", value = spot });
        }

        /// <summary>
        /// Case-insensitive match of a spot callsign against the user's
        /// watched-callsigns list. Each non-comment line is either an exact
        /// callsign or a prefix ending in "*".
        /// </summary>
        internal static bool MatchesWatchList(string callsign, string watchedRaw)
        {
            if (string.IsNullOrWhiteSpace(callsign) || string.IsNullOrWhiteSpace(watchedRaw))
                return false;
            var call = callsign.ToUpperInvariant();
            foreach (var raw in watchedRaw.Split('\n'))
            {
                var p = raw.Trim().ToUpperInvariant();
                if (p.Length == 0 || p.StartsWith("#")) continue;
                if (p.EndsWith("*"))
                {
                    var prefix = p[..^1];
                    if (prefix.Length > 0 && call.StartsWith(prefix)) return true;
                }
                else if (call == p)
                {
                    return true;
                }
            }
            return false;
        }

        // Update the cached status fields and broadcast over SignalR so the
        // UI can show a live badge. Suppresses repeat broadcasts when the
        // state has not actually changed (avoids hammering SignalR every
        // 15 s while idle).
        private async Task SetStatus(string status, string detail)
        {
            if (Status == status && LastError == detail) return;
            Status = status;
            LastError = detail ?? "";
            _logger.LogInformation("[DxCluster] Status: {Status} ({Detail})", status, detail);
            await _hubContext.Clients.All.SendAsync("RadioStateUpdate",
                new { property = "DxClusterStatus", value = new { status, detail } });
        }

        private static async Task SafeDelay(int seconds, CancellationToken token)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); }
            catch (OperationCanceledException) { }
        }
    }
}
