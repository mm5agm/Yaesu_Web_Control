using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/diagnostics")]
    public class DiagnosticsController : ControllerBase
    {
        private readonly ILogger<DiagnosticsController> _logger;

        // Written verbatim into the Serilog file by MarkFreshLog and searched for
        // by DownloadTodayLog. Serilog holds today's log open with a write lock,
        // so we can't delete/truncate it to "start fresh" (and the shared mode
        // that would allow it caused the #73 startup hang) — instead we drop a
        // marker and let the download return only the lines after the last one.
        private const string Marker = "===== NEW TEST CAPTURE";

        public DiagnosticsController(ILogger<DiagnosticsController> logger) => _logger = logger;

        private static string? TodayLogPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logDir = Path.Combine(appData, "MM5AGM", "Yaesu Web Control", "logs");
            if (!Directory.Exists(logDir)) return null;

            // When Detailed logging is on, that file is the one worth sending —
            // it is a superset of the normal log, so there is no reason to hand
            // back the trimmed version. Falls through to the normal log if the
            // detail file doesn't exist yet (switched on seconds ago).
            if (Services.LogLevelController.IsDetailed)
            {
                var detailPath = Newest(logDir, "ywc-detail-", $"ywc-detail-{DateTime.Now:yyyyMMdd}.log");
                if (detailPath != null) return detailPath;
            }

            return Newest(logDir, "ywc-", $"ywc-{DateTime.Now:yyyyMMdd}.log");
        }

        // Today's file, falling back to the newest match if the app started on a
        // previous day and hasn't rolled over to today's file yet.
        private static string? Newest(string logDir, string prefix, string todayName)
        {
            var todayPath = Path.Combine(logDir, todayName);
            if (System.IO.File.Exists(todayPath)) return todayPath;

            return new DirectoryInfo(logDir).GetFiles($"{prefix}*.log")
                // "ywc-*" would otherwise also match "ywc-detail-*".
                .Where(f => prefix != "ywc-" || !f.Name.StartsWith("ywc-detail-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }

        // Drops a marker line into the log so the tester can start a clean capture
        // without the huge accumulated file. "Start fresh test log" on the
        // Diagnostics page.
        [HttpPost("log/mark")]
        public IActionResult MarkFreshLog()
        {
            _logger.LogWarning("{Marker} {Time} =====", Marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            return Ok(new { marked = true });
        }

        // Serves today's log as a download. By default returns only the lines
        // after the last "start fresh" marker (a small, focused capture); pass
        // ?full=1 for the whole file. Lets a tester send a log with one click and
        // a drag into a GitHub discussion instead of navigating %APPDATA%.
        [HttpGet("log")]
        public IActionResult DownloadTodayLog([FromQuery] bool full = false)
        {
            var sourcePath = TodayLogPath();
            if (sourcePath == null) return NotFound("No log files found yet.");

            // Serilog keeps the file open for writing, so read it with a shared
            // read/write/delete handle rather than File(path, ...), which would
            // fail with a sharing violation on the live log.
            string content;
            using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
            {
                content = reader.ReadToEnd();
            }

            if (!full)
            {
                int idx = content.LastIndexOf(Marker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    // Start at the beginning of the marker's own line.
                    int lineStart = content.LastIndexOf('\n', idx);
                    content = lineStart >= 0 ? content[(lineStart + 1)..] : content[idx..];
                }
            }

            var bytes = Encoding.UTF8.GetBytes(content);
            return File(bytes, "text/plain", Path.GetFileName(sourcePath));
        }
    }
}
