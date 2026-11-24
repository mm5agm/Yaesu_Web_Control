using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DxClusterController : ControllerBase
    {
        private readonly DxClusterService _dxCluster;
        private readonly ISettingsService _settings;

        public DxClusterController(DxClusterService dxCluster, ISettingsService settings)
        {
            _dxCluster = dxCluster;
            _settings = settings;
        }

        // ── Connection test ────────────────────────────────────────────────
        // Lets the user verify a host/port/callsign combination on the
        // Settings page before saving — connects, sends the callsign, reads
        // ~10 s of output, returns the transcript so the user can see whether
        // they reached the cluster and what it said.

        public class TestRequest
        {
            public string Host { get; set; } = "";
            public int Port { get; set; } = 7300;
            public string LoginCallsign { get; set; } = "";
            public string Password { get; set; } = "";
        }

        public class TestResult
        {
            public bool Success { get; set; }
            public string Status { get; set; } = "";
            public string Transcript { get; set; } = "";
            public int LinesReceived { get; set; }
        }

        [HttpPost("test")]
        public async Task<IActionResult> TestCluster([FromBody] TestRequest req, CancellationToken ct)
        {
            req ??= new TestRequest();
            req.Host = (req.Host ?? "").Trim();
            if (req.Host.Length == 0)
                return Ok(new TestResult { Success = false, Status = "No host given." });
            if (req.Port <= 0 || req.Port > 65535)
                return Ok(new TestResult { Success = false, Status = $"Invalid port {req.Port}." });

            var sb = new StringBuilder();
            int lineCount = 0;

            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overallCts.CancelAfter(TimeSpan.FromSeconds(12));

            using var client = new TcpClient();
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(6));
                    await client.ConnectAsync(req.Host, req.Port, connectCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return Ok(new TestResult
                {
                    Success = false,
                    Status = $"Could not connect to {req.Host}:{req.Port} within 6 seconds. " +
                             "The host may be down, the port wrong, or your firewall blocking the outbound connection.",
                });
            }
            catch (SocketException ex)
            {
                return Ok(new TestResult
                {
                    Success = false,
                    Status = $"Connection refused: {ex.SocketErrorCode}. The host is reachable but nothing is listening on port {req.Port}.",
                });
            }
            catch (Exception ex)
            {
                return Ok(new TestResult
                {
                    Success = false,
                    Status = $"Connection failed: {ex.Message}",
                });
            }

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };

                // Same proactive-login pattern as the real service — many DXSpider
                // nodes prompt with "login: " (no newline) so ReadLineAsync would
                // hang. Send the callsign 1.5 s after connect.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1500, overallCts.Token);
                        if (!string.IsNullOrWhiteSpace(req.LoginCallsign))
                        {
                            await writer.WriteLineAsync(req.LoginCallsign.Trim().ToUpperInvariant());
                            sb.AppendLine($">> {req.LoginCallsign.Trim().ToUpperInvariant()}");
                            if (!string.IsNullOrWhiteSpace(req.Password))
                            {
                                await Task.Delay(1500, overallCts.Token);
                                await writer.WriteLineAsync(req.Password.Trim());
                                sb.AppendLine(">> (password)");
                            }
                        }
                    }
                    catch { /* socket may already be closing — fine */ }
                }, overallCts.Token);

                // Read up to overallCts deadline, accumulating all received bytes.
                var buf = new char[4096];
                while (!overallCts.IsCancellationRequested)
                {
                    int n;
                    try { n = await reader.ReadAsync(buf.AsMemory(), overallCts.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { break; } // socket closed by remote
                    if (n <= 0) break;
                    sb.Append(buf, 0, n);
                    lineCount += new string(buf, 0, n).Count(c => c == '\n');
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"-- read error: {ex.Message} --");
            }

            string transcript = sb.ToString().Trim();
            bool gotAnything = transcript.Length > 0;
            return Ok(new TestResult
            {
                Success = gotAnything,
                Status = gotAnything
                    ? $"Connected to {req.Host}:{req.Port}. Received {transcript.Length} characters / {lineCount} lines in ~10 seconds."
                    : $"Connected to {req.Host}:{req.Port} but no data was received within 10 seconds. The host accepted the TCP connection but isn't speaking DXSpider/AR-Cluster protocol — wrong port, perhaps, or a different service.",
                Transcript = transcript,
                LinesReceived = lineCount,
            });
        }

        // ── Watched callsigns ─────────────────────────────────────────────
        //
        // Persisted in ApplicationSettings.DxClusterWatchedCallsigns as one
        // pattern per line. The popup dialog calls GET to read the list and
        // PUT to replace it. Each pattern is either an exact callsign or a
        // prefix ending in "*" (case-insensitive). Lines starting with "#"
        // are ignored.

        public class WatchedListDto { public List<string> Patterns { get; set; } = new(); }

        [HttpGet("watched")]
        public async Task<IActionResult> GetWatched()
        {
            var s = await _settings.GetSettingsAsync();
            var patterns = (s.DxClusterWatchedCallsigns ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#"))
                .ToList();
            return Ok(new WatchedListDto { Patterns = patterns });
        }

        [HttpPut("watched")]
        public async Task<IActionResult> SaveWatched([FromBody] WatchedListDto dto)
        {
            if (dto == null) return BadRequest();
            var cleaned = (dto.Patterns ?? new List<string>())
                .Select(p => (p ?? "").Trim().ToUpperInvariant())
                .Where(p => p.Length > 0)
                .Distinct()
                .ToList();

            var s = await _settings.GetSettingsAsync();
            s.DxClusterWatchedCallsigns = string.Join("\n", cleaned);
            await _settings.SaveSettingsAsync(s);
            return Ok(new WatchedListDto { Patterns = cleaned });
        }

        /// <summary>
        /// Returns all current (non-aged-off) DX spots, newest first.
        /// Used by the frontend on page load so the spectrum overlay can
        /// render existing spots without waiting for new ones to arrive.
        /// </summary>
        [HttpGet("spots")]
        public IActionResult GetSpots() => Ok(_dxCluster.GetAllSpots());

        /// <summary>
        /// Returns the current cluster connection status — used by the
        /// spectrum-panel badge on page load (before the first SignalR
        /// push) and for ad-hoc diagnostics from a browser.
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetStatus() => Ok(new
        {
            status    = _dxCluster.Status,
            detail    = _dxCluster.LastError,
            spotCount = _dxCluster.SpotCount,
        });

        /// <summary>
        /// Last ~100 raw lines received from the cluster, one per line.
        /// Use this in the browser to see the actual protocol exchange when
        /// diagnosing why spots are not appearing. Plain text response so it
        /// is easy to read directly.
        /// </summary>
        [HttpGet("recent")]
        [Produces("text/plain")]
        public IActionResult GetRecent()
        {
            var lines = _dxCluster.GetRecentLines();
            return Content(lines.Count == 0
                ? "(no lines received yet)"
                : string.Join('\n', lines), "text/plain");
        }
    }
}
