using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using RadioWebControl.Core.Models;
using RadioWebControl.Core.Services;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// Export / import everything the operator has customised in YWC as a
    /// single zip — settings, memories, memory banks, calibration, labels.
    /// Replaces the v2.0.0 settings-only backup with a clean one-file PC
    /// migration path.
    ///
    /// Files included (whichever exist in %APPDATA%\MM5AGM\Yaesu Web Control\):
    ///   appsettings.user.json   user settings
    ///   memories.json           memory channels
    ///   memory-banks.json       saved memory banks
    ///   calibration.user.json   meter calibration overrides
    ///   labels.user.json        accessible-label customisations
    ///
    /// Excluded:
    ///   radio_state.json   — live transient state, should reset on a new PC
    ///   *.bak              — recovery copies, not for migration
    ///   *.log / *.adi      — diagnostic / export logs
    /// </summary>
    [ApiController]
    [Route("api/backup")]
    public class BackupController : ControllerBase
    {
        private readonly ISettingsService _settingsService;
        private readonly MemoryService _memoryService;
        private readonly ILogger<BackupController> _logger;

        public BackupController(
            ISettingsService settingsService,
            MemoryService memoryService,
            ILogger<BackupController> logger)
        {
            _settingsService = settingsService;
            _memoryService   = memoryService;
            _logger          = logger;
        }

        // Files allowed into / out of the backup. Anything else in the
        // AppData folder is silently skipped so we don't accidentally ship
        // logs, .bak files, or future formats we don't yet understand.
        private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "appsettings.user.json",
            "memories.json",
            "memory-banks.json",
            "calibration.user.json",
            "labels.user.json",
            "flex-layouts.json",
        };

        [HttpGet("export")]
        public IActionResult Export()
        {
            var appDataDir = Path.GetDirectoryName(_settingsService.GetSettingsFilePath());
            if (string.IsNullOrEmpty(appDataDir) || !Directory.Exists(appDataDir))
                return NotFound(new { error = "User data folder not found yet — has YWC ever saved any settings?" });

            using var ms = new MemoryStream();
            int included = 0;
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in AllowedFiles)
                {
                    var path = Path.Combine(appDataDir, name);
                    if (!System.IO.File.Exists(path)) continue;
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var src  = System.IO.File.OpenRead(path);
                    using var dest = entry.Open();
                    src.CopyTo(dest);
                    included++;
                }

                // README so a user opening the zip later knows what it is.
                var readme = zip.CreateEntry("README.txt", CompressionLevel.Optimal);
                using (var w = new StreamWriter(readme.Open()))
                {
                    w.WriteLine($"YWC user data backup — created {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
                    w.WriteLine($"App version: {AppVersion.Display}");
                    w.WriteLine();
                    w.WriteLine("To restore: open YWC → Settings → Import full backup, and pick this zip.");
                    w.WriteLine("Files included: " + included);
                }
            }
            ms.Position = 0;

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            return new FileContentResult(ms.ToArray(), "application/zip")
            {
                FileDownloadName = $"ywc-backup-{stamp}.zip"
            };
        }

        [HttpPost("import")]
        public async Task<IActionResult> Import(IFormFile? file)
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { error = "No file uploaded." });

            var appDataDir = Path.GetDirectoryName(_settingsService.GetSettingsFilePath());
            if (string.IsNullOrEmpty(appDataDir))
                return StatusCode(500, new { error = "Cannot resolve user data folder." });
            Directory.CreateDirectory(appDataDir);

            // Read the upload into memory and validate the zip end-to-end
            // before touching any existing files. If anything goes wrong we
            // either reject the import or roll back via .bak copies.
            byte[] uploadedBytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                uploadedBytes = ms.ToArray();
            }

            using var verifyStream = new MemoryStream(uploadedBytes, writable: false);
            ZipArchive verifyZip;
            try
            {
                verifyZip = new ZipArchive(verifyStream, ZipArchiveMode.Read, leaveOpen: false);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"Not a valid zip file: {ex.Message}" });
            }

            // Snapshot what we're about to overwrite, in case any single
            // entry fails partway and we need to roll back.
            var backedUp = new Dictionary<string, string>();
            try
            {
                int restored = 0, skipped = 0;
                foreach (var entry in verifyZip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;          // directory entries
                    if (!AllowedFiles.Contains(entry.Name))
                    {
                        skipped++;
                        continue;
                    }
                    var target = Path.Combine(appDataDir, entry.Name);
                    if (System.IO.File.Exists(target))
                    {
                        var bak = target + ".bak";
                        System.IO.File.Copy(target, bak, overwrite: true);
                        backedUp[target] = bak;
                    }
                    using var src  = entry.Open();
                    using var dest = System.IO.File.Create(target);
                    await src.CopyToAsync(dest);
                    restored++;
                }

                // Tell the in-memory caches to reload from the new files.
                _settingsService.InvalidateCache();
                _memoryService.ReloadFromDisk();

                _logger.LogInformation("Backup import: restored {Restored} files, skipped {Skipped}.", restored, skipped);
                return Ok(new
                {
                    restored,
                    skipped,
                    restartNeeded = true,
                    message = $"Imported {restored} file{(restored == 1 ? "" : "s")}. " +
                              "Restart YWC for the radio-connection, DX-cluster, SDR and rigctld services to pick up the new values."
                });
            }
            catch (Exception ex)
            {
                // Roll back every file we already overwrote.
                foreach (var (target, bak) in backedUp)
                {
                    try { System.IO.File.Copy(bak, target, overwrite: true); } catch { }
                }
                _logger.LogError(ex, "Backup import failed; rolled back.");
                return StatusCode(500, new { error = $"Import failed and rolled back: {ex.Message}" });
            }
            finally
            {
                verifyZip.Dispose();
            }
        }
    }
}
