using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.RadioMenu;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// Saves the radio's own menu settings to a file, like the radio's
    /// MENU SAVE to SD card but readable in Notepad, and loads them back.
    /// Save only reads. Load is two steps: preview reads the radio and shows
    /// what would change, writing nothing; apply writes only the items the
    /// user ticked, each checked again against the table and read back.
    /// </summary>
    [ApiController]
    [Route("api/radiomenu")]
    public class RadioMenuController : ControllerBase
    {
        private const string ClientId = "RadioMenu";
        private const int TimeoutMs = 300;
        private const int Attempts = 3;
        private const int SetTimeoutMs = 50;
        private const int MaxFileChars = 256 * 1024;

        /// <summary>Where YWC keeps a copy of every save, so the user can find it again.</summary>
        public static string SavesFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "Radio menus");

        /// <summary>One load at a time, so two browsers cannot interleave writes.</summary>
        private static readonly SemaphoreSlim LoadGate = new(1, 1);

        private readonly ICatClient _cat;
        private readonly ISettingsService _settings;
        private readonly ILogger<RadioMenuController> _logger;

        public RadioMenuController(ICatClient cat, ISettingsService settings, ILogger<RadioMenuController> logger)
        {
            _cat = cat;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// GET /api/radiomenu/export — reads every menu item and returns an .ini file.
        /// About 200 reads on an FTdx101, a few seconds at 38400 bps.
        /// </summary>
        [HttpGet("export")]
        public async Task<IActionResult> Export(CancellationToken ct)
        {
            var model = _settings.GetCachedSettings().RadioModel;
            var table = RadioMenuCat.TableFor(model);
            if (table is null)
                return BadRequest(new { error = $"Saving the radio menu is not available for the {model} yet." });
            if (!_cat.IsConnected)
                return BadRequest(new { error = "The radio is not connected." });

            var readings = new List<RadioMenuReading>(table.Count);
            foreach (var item in table)
                readings.Add(new RadioMenuReading(item, await ReadAsync(item.Address, ct)));

            int missing = readings.Count(r => r.Raw is null);
            _logger.LogInformation("Radio menu save: {Read} of {Total} items read from the {Model}",
                readings.Count - missing, readings.Count, model);
            if (missing == readings.Count)
                return StatusCode(504, new { error = "The radio did not answer any menu reads." });

            var now = DateTime.Now;
            var ini = RadioMenuCat.ToIni(model, now, readings);
            var name = $"YWC radio menu {model} {now:yyyy-MM-dd HHmm}.ini";
            Response.Headers["X-Menu-Missing"] = missing.ToString();
            // UTF-8 with BOM so older Notepad opens it as UTF-8.
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(ini)).ToArray();
            // The browser decides where its download goes and does not say,
            // so keep a copy where the page can tell the user it is.
            try
            {
                Directory.CreateDirectory(SavesFolder);
                var copy = Path.Combine(SavesFolder, name);
                await System.IO.File.WriteAllBytesAsync(copy, bytes, ct);
                Response.Headers["X-Menu-Copy"] = Uri.EscapeDataString(copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Radio menu save: could not keep a copy in {Folder}", SavesFolder);
            }
            return File(bytes, "text/plain", name);
        }

        /// <summary>GET /api/radiomenu/files — the copies YWC kept, newest first.</summary>
        [HttpGet("files")]
        public IActionResult Files()
        {
            var dir = new DirectoryInfo(SavesFolder);
            var files = dir.Exists
                ? dir.GetFiles("*.ini").OrderByDescending(f => f.LastWriteTime)
                    .Select(f => new { name = f.Name, modified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") }).ToList<object>()
                : new List<object>();
            return Ok(new { folder = SavesFolder, files });
        }

        /// <summary>GET /api/radiomenu/files/{name} — one kept copy, as text, for Load.</summary>
        [HttpGet("files/{name}")]
        public async Task<IActionResult> ReadFile(string name, CancellationToken ct)
        {
            // A bare file name in the folder, nothing that could climb out of it.
            if (name != Path.GetFileName(name) || !name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Not a radio menu file name." });
            var path = Path.Combine(SavesFolder, name);
            if (!System.IO.File.Exists(path))
                return NotFound(new { error = $"{name} is not in {SavesFolder}." });
            return Ok(new { name, text = await System.IO.File.ReadAllTextAsync(path, ct) });
        }

        /// <summary>
        /// POST /api/radiomenu/open-folder — opens the folder in Explorer on the PC
        /// YWC runs on. Created if missing so the user lands somewhere real.
        /// </summary>
        [HttpPost("open-folder")]
        public IActionResult OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(SavesFolder);
                Process.Start(new ProcessStartInfo { FileName = SavesFolder, UseShellExecute = true });
                return Ok(new { path = SavesFolder });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Radio menu: could not open {Folder}", SavesFolder);
                return StatusCode(500, new { error = ex.Message, path = SavesFolder });
            }
        }

        public sealed record PreviewRequest(string? Text);

        /// <summary>
        /// POST /api/radiomenu/load/preview — reads the radio and compares it with the file.
        /// Writes nothing. Each row says what would be sent, or why it cannot be.
        /// </summary>
        [HttpPost("load/preview")]
        public async Task<IActionResult> Preview([FromBody] PreviewRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return BadRequest(new { error = "The file is empty." });
            if (request.Text.Length > MaxFileChars)
                return BadRequest(new { error = "That file is too big to be a radio menu file." });
            var model = _settings.GetCachedSettings().RadioModel;
            var table = RadioMenuCat.TableFor(model);
            if (table is null)
                return BadRequest(new { error = $"Loading the radio menu is not available for the {model} yet." });
            if (!_cat.IsConnected)
                return BadRequest(new { error = "The radio is not connected." });

            var file = RadioMenuLoad.Parse(request.Text);
            if (file.Items.Count == 0)
                return BadRequest(new { error = "No menu items found. Is this a radio menu file saved by YWC?" });

            var rows = new List<object>();
            var seen = new HashSet<string>();
            int changes = 0;
            foreach (var line in file.Items)
            {
                var item = RadioMenuLoad.Find(table, line);
                if (item is null)
                {
                    rows.Add(Row(null, line, "unknown", null, null, $"Line {line.Line}: not an item on the {model}."));
                    continue;
                }
                if (!seen.Add(item.Address))
                {
                    rows.Add(Row(item, line, "unknown", null, null, $"Line {line.Line}: this item is already in the file; the first one is used."));
                    continue;
                }
                if (line.Value.Equals(RadioMenuLoad.NoAnswer, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(Row(item, line, "skipped", null, null, "The radio did not answer this item when the file was saved."));
                    continue;
                }
                if (item.Link)
                {
                    rows.Add(Row(item, line, "blocked", null, null, "This sets the CAT link itself, so Load leaves it alone. Change it on the radio."));
                    continue;
                }
                var current = await ReadAsync(item.Address, ct);
                if (current is null)
                {
                    rows.Add(Row(item, line, "noanswer", null, null, "The radio did not answer, so this cannot be checked."));
                    continue;
                }
                var raw = RadioMenuLoad.Encode(item, line.Value, line.SavedRaw, current, out var error);
                if (raw is null)
                    rows.Add(Row(item, line, "error", current, null, error));
                else if (RadioMenuLoad.ReadBackMatches(item, raw, current))
                    rows.Add(Row(item, line, "same", current, raw, null));
                else
                {
                    rows.Add(Row(item, line, "change", current, raw, null));
                    changes++;
                }
            }

            string? warning = file.Radio is not null && !string.Equals(file.Radio, model, StringComparison.OrdinalIgnoreCase)
                ? $"This file was saved from a {file.Radio}; the radio now is a {model}. Check each change."
                : null;
            _logger.LogInformation("Radio menu load preview: {Rows} lines, {Changes} changes", rows.Count, changes);
            return Ok(new { model, fileRadio = file.Radio, warning, rows });
        }

        public sealed record ApplyItem(string Address, string Raw);
        public sealed record ApplyRequest(List<ApplyItem>? Items);

        /// <summary>
        /// POST /api/radiomenu/load/apply — writes the ticked items. Each is checked
        /// again against the table and the radio's current answer, sent, and read back.
        /// </summary>
        [HttpPost("load/apply")]
        public async Task<IActionResult> Apply([FromBody] ApplyRequest request, CancellationToken ct)
        {
            var model = _settings.GetCachedSettings().RadioModel;
            var table = RadioMenuCat.TableFor(model);
            if (table is null)
                return BadRequest(new { error = $"Loading the radio menu is not available for the {model} yet." });
            if (!_cat.IsConnected)
                return BadRequest(new { error = "The radio is not connected." });
            if (request.Items is null || request.Items.Count == 0)
                return BadRequest(new { error = "Nothing was ticked." });
            if (!await LoadGate.WaitAsync(0, ct))
                return Conflict(new { error = "A radio menu load is already running." });
            try
            {
                var results = new List<object>();
                foreach (var want in request.Items.DistinctBy(i => i.Address))
                {
                    var item = table.FirstOrDefault(i => i.Address == want.Address);
                    if (item is null || string.IsNullOrEmpty(want.Raw))
                    {
                        results.Add(new { address = want.Address, function = (string?)null, now = (string?)null,
                            status = "error", message = "Not an item on this radio." });
                        continue;
                    }
                    var current = await ReadAsync(item.Address, ct);
                    if (current is null)
                    {
                        results.Add(Result(item, "error", null, "The radio did not answer, so nothing was sent."));
                        continue;
                    }
                    var raw = RadioMenuLoad.CheckRaw(item, want.Raw, current, out var error);
                    if (raw is null)
                    {
                        results.Add(Result(item, "error", current, error));
                        continue;
                    }
                    if (RadioMenuLoad.ReadBackMatches(item, raw, current))
                    {
                        results.Add(Result(item, "same", current, null));
                        continue;
                    }
                    _logger.LogInformation("Radio menu load: EX{Address} {Function} {From} -> {To}",
                        item.Address, item.Function, current, raw);
                    await _cat.SendCommandAsync(RadioMenuCat.SetCommand(item.Address, raw), ClientId, ct, SetTimeoutMs);
                    var after = await ReadAsync(item.Address, ct);
                    results.Add(RadioMenuLoad.ReadBackMatches(item, raw, after)
                        ? Result(item, "ok", after, null)
                        : Result(item, "mismatch", after, after is null
                            ? "Sent, but the radio did not answer the read back."
                            : $"Sent, but the radio now shows {item.Decode(after)}."));
                }
                return Ok(new { results });
            }
            finally
            {
                LoadGate.Release();
            }
        }

        private static object Row(RadioMenuItem? item, RadioMenuFileItem line, string status, string? current, string? raw, string? message) => new
        {
            address = item?.Address ?? line.Address,
            section = item is null ? line.Section : $"{item.Section} > {item.Group}",
            function = item?.Function ?? line.Name,
            current = current is null || item is null ? null : item.Decode(current),
            wanted = raw is null || item is null ? line.Value : item.Decode(raw),
            raw,
            status,
            message,
            tx = item?.Tx ?? false,
        };

        private static object Result(RadioMenuItem item, string status, string? now, string? message) => new
        {
            address = item.Address,
            function = (string?)item.Function,
            now = now is null ? null : item.Decode(now),
            status,
            message,
        };

        private async Task<string?> ReadAsync(string address, CancellationToken ct)
        {
            var command = RadioMenuCat.ReadCommand(address);
            for (int i = 0; i < Attempts; i++)
            {
                var value = RadioMenuCat.ParseAnswer(address,
                    await _cat.SendCommandAsync(command, ClientId, ct, TimeoutMs));
                if (value is not null) return value;
            }
            _logger.LogWarning("Radio menu: no answer for EX{Address}", address);
            return null;
        }
    }
}
