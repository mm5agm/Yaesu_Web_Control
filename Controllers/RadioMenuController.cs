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
    ///
    /// The file also holds the front-panel levels (MIC GAIN, AMC, PROC and
    /// so on, see <see cref="RadioKnobs"/>) for each mode. Reading or setting
    /// those changes the main band's mode, one mode at a time, and puts it
    /// back afterwards; never while the radio transmits.
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

        /// <summary>Where saves go: with the rest of YWC's files, so the user can find them.</summary>
        public static string SavesFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "Radio menus");

        /// <summary>One load at a time, so two browsers cannot interleave writes.</summary>
        private static readonly SemaphoreSlim LoadGate = new(1, 1);

        private readonly ICatClient _cat;
        private readonly ISettingsService _settings;
        private readonly RadioStateService _radio;
        private readonly ILogger<RadioMenuController> _logger;

        public RadioMenuController(ICatClient cat, ISettingsService settings, RadioStateService radio, ILogger<RadioMenuController> logger)
        {
            _cat = cat;
            _settings = settings;
            _radio = radio;
            _logger = logger;
        }

        /// <summary>The knob pass's way to the radio: reads wait for the answer, sets do not.</summary>
        private sealed class KnobLink(ICatClient cat, RadioStateService radio) : IRadioKnobLink
        {
            public async Task<string?> AskAsync(string command, CancellationToken ct)
            {
                var answer = await cat.SendCommandAsync(command, ClientId, ct, TimeoutMs);
                return string.IsNullOrEmpty(answer) ? null : answer;
            }

            public Task TellAsync(string command, CancellationToken ct) => cat.SendWithoutReplyAsync(command, ClientId, ct);

            public bool Transmitting => radio.IsTransmitting;
        }

        private RadioKnobPass NewKnobPass() => new(new KnobLink(_cat, _radio));

        /// <summary>
        /// After a pass, read the levels again through the dispatcher, so the
        /// pages show the values for the mode the radio is back in.
        /// </summary>
        private async Task RefreshKnobsAsync(IReadOnlyList<RadioKnob> knobs, CancellationToken ct)
        {
            foreach (var knob in knobs)
                await _cat.SendCommandAndDispatchAsync(RadioKnobs.ReadCommand(knob), ClientId, ct);
        }

        /// <summary>
        /// POST /api/radiomenu/save — reads every menu item and writes an .ini file
        /// into <see cref="SavesFolder"/>, with YWC's other files, rather than
        /// downloading it: a page cannot choose where a browser's download goes,
        /// so the user could not be told where to find it.
        /// About 200 reads on an FTdx101, a few seconds at 38400 bps.
        /// </summary>
        [HttpPost("save")]
        public async Task<IActionResult> Save(CancellationToken ct)
        {
            var model = _settings.GetCachedSettings().RadioModel;
            var table = RadioMenuCat.TableFor(model);
            if (table is null)
                return BadRequest(new { error = $"Saving the radio menu is not available for the {model} yet." });
            if (!_cat.IsConnected)
                return BadRequest(new { error = "The radio is not connected." });

            if (!await LoadGate.WaitAsync(0, ct))
                return Conflict(new { error = "A radio menu save or load is already running." });
            var readings = new List<RadioMenuReading>(table.Count);
            var knobReadings = new List<RadioKnobReading>();
            string? knobsStopped = null;
            try
            {
                foreach (var item in table)
                    readings.Add(new RadioMenuReading(item, await ReadAsync(item.Address, ct)));

                var knobs = RadioKnobs.TableFor(model);
                if (knobs is not null)
                {
                    var pass = NewKnobPass();
                    await pass.RunAsync(RadioKnobs.ModesFor(model), async mode =>
                    {
                        foreach (var knob in knobs)
                            knobReadings.Add(new RadioKnobReading(mode, knob, await pass.ReadAsync(knob, ct)));
                    }, ct);
                    knobsStopped = pass.Stopped;
                    if (knobsStopped is not null)
                        _logger.LogWarning("Radio menu save: front panel levels: {Why}", knobsStopped);
                    await RefreshKnobsAsync(knobs, ct);
                }
            }
            finally
            {
                LoadGate.Release();
            }

            int missing = readings.Count(r => r.Raw is null);
            var unanswered = readings.Where(r => r.Raw is null).Select(r => r.Item.Function).ToList();
            _logger.LogInformation("Radio menu save: {Read} of {Total} items read from the {Model}",
                readings.Count - missing, readings.Count, model);
            if (missing == readings.Count)
                return StatusCode(504, new { error = "The radio did not answer any menu reads." });

            var now = DateTime.Now;
            var ini = RadioMenuCat.ToIni(model, now, readings) + RadioKnobs.ToIni(knobReadings);
            var name = $"YWC radio menu {model} {now:yyyy-MM-dd HHmm}.ini";
            // UTF-8 with BOM so older Notepad opens it as UTF-8.
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(ini)).ToArray();
            var path = Path.Combine(SavesFolder, name);
            try
            {
                Directory.CreateDirectory(SavesFolder);
                await System.IO.File.WriteAllBytesAsync(path, bytes, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Radio menu save: could not write {Path}", path);
                return StatusCode(500, new { error = $"Could not write {path}: {ex.Message}" });
            }
            _reminderCache = null;
            return Ok(new { name, path, missing, unanswered, knobs = knobReadings.Count(k => k.Value is not null), knobsStopped });
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

        /// <summary>
        /// GET /api/radiomenu/files/{name} — one saved file, as text, for Load.
        /// With ?download=true, as a download, for a browser on another PC.
        /// </summary>
        [HttpGet("files/{name}")]
        public async Task<IActionResult> ReadFile(string name, [FromQuery] bool download, CancellationToken ct)
        {
            // A bare file name in the folder, nothing that could climb out of it.
            if (name != Path.GetFileName(name) || !name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Not a radio menu file name." });
            var path = Path.Combine(SavesFolder, name);
            if (!System.IO.File.Exists(path))
                return NotFound(new { error = $"{name} is not in {SavesFolder}." });
            if (download)
                return PhysicalFile(path, "text/plain", name);
            return Ok(new { name, text = await System.IO.File.ReadAllTextAsync(path, ct) });
        }

        /// <summary>"Not now" on the reminder: hides it until YWC starts again.</summary>
        private static bool _reminderDismissedThisRun;

        /// <summary>The last check, so a page change does not read the radio again.</summary>
        private static (DateTime At, RadioMenuReminderResult Result)? _reminderCache;

        private static readonly TimeSpan ReminderCacheFor = TimeSpan.FromSeconds(60);

        /// <summary>"Not now" on an old or changed reminder also keeps it away for this long.</summary>
        public const int ReminderSnoozeDays = 7;

        private static string SnoozeFile => Path.Combine(SavesFolder, "reminder-snooze.txt");

        /// <summary>The newest file YWC saved for this radio model, or null.</summary>
        private static RadioMenuSaveInfo? NewestSave(string model)
        {
            var dir = new DirectoryInfo(SavesFolder);
            if (!dir.Exists) return null;
            // Newest first; a few only, so a folder of hundreds stays quick.
            foreach (var f in dir.GetFiles("*.ini").OrderByDescending(f => f.LastWriteTime).Take(20))
            {
                string text;
                try { text = System.IO.File.ReadAllText(f.FullName); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (string.Equals(RadioMenuLoad.Parse(text).Radio, model, StringComparison.OrdinalIgnoreCase))
                    return new RadioMenuSaveInfo(f.Name, f.LastWriteTime, text);
            }
            return null;
        }

        private static DateTime? SnoozedUntil()
        {
            try
            {
                return System.IO.File.Exists(SnoozeFile)
                    && DateTime.TryParse(System.IO.File.ReadAllText(SnoozeFile).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var until)
                    ? until : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// GET /api/radiomenu/reminder — should YWC remind the user to save the
        /// radio menu? Reads only: the main band's mode and the front-panel
        /// levels in that mode, never a mode change. The banner on every page
        /// asks when YWC starts; the Settings page shows the same line.
        /// pending means ask again shortly (radio not connected, or a save or
        /// load is running).
        /// </summary>
        [HttpGet("reminder")]
        public async Task<IActionResult> Reminder(CancellationToken ct)
        {
            var model = _settings.GetCachedSettings().RadioModel;
            if (RadioMenuCat.TableFor(model) is null)
                return Ok(new { supported = false });
            if (!_cat.IsConnected)
                return Ok(new { supported = true, pending = true });

            RadioMenuReminderResult result;
            if (_reminderCache is { } cached && DateTime.Now - cached.At < ReminderCacheFor)
                result = cached.Result;
            else
            {
                var newest = NewestSave(model);
                string? mode = null;
                var live = new Dictionary<string, int?>();
                var knobs = RadioKnobs.TableFor(model);
                if (newest is not null && knobs is not null)
                {
                    if (!await LoadGate.WaitAsync(0, ct))
                        return Ok(new { supported = true, pending = true });
                    try
                    {
                        var link = new KnobLink(_cat, _radio);
                        mode = RadioKnobs.ParseModeAnswer(await link.AskAsync("MD0;", ct));
                        var pass = NewKnobPass();
                        foreach (var knob in knobs)
                            live[knob.Command] = await pass.ReadAsync(knob, ct);
                    }
                    finally
                    {
                        LoadGate.Release();
                    }
                }
                result = RadioMenuReminder.Check(newest, DateTime.Now, model, mode, live);
                _reminderCache = (DateTime.Now, result);
            }

            // No save at all is asked at every start; the others wait out a "Not now".
            bool snoozed = _reminderDismissedThisRun
                || (result.Kind != "none" && SnoozedUntil() is DateTime until && until > DateTime.Now);
            return Ok(new
            {
                supported = true,
                pending = false,
                kind = result.Kind,
                show = result.Kind != "ok" && !snoozed,
                message = RadioMenuReminder.Message(result),
                days = result.Days,
                file = result.File,
                changes = result.Changes,
            });
        }

        /// <summary>
        /// POST /api/radiomenu/reminder/later — "Not now": no banner until YWC
        /// starts again, and no old/changed reminder for a week. A missing save
        /// is still asked at the next start.
        /// </summary>
        [HttpPost("reminder/later")]
        public IActionResult ReminderLater()
        {
            _reminderDismissedThisRun = true;
            try
            {
                Directory.CreateDirectory(SavesFolder);
                System.IO.File.WriteAllText(SnoozeFile, DateTime.Now.AddDays(ReminderSnoozeDays).ToString("o"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Radio menu reminder: could not write {Path}", SnoozeFile);
            }
            return Ok(new { snoozeDays = ReminderSnoozeDays });
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

        /// <summary>
        /// GET /api/radiomenu/current — reads every item for the Change radio
        /// settings editor: its value now and, for each item that can be
        /// changed, the choices the radio offers. Writes nothing.
        /// </summary>
        [HttpGet("current")]
        public async Task<IActionResult> Current(CancellationToken ct)
        {
            var model = _settings.GetCachedSettings().RadioModel;
            var table = RadioMenuCat.TableFor(model);
            if (table is null)
                return BadRequest(new { error = $"Changing the radio menu is not available for the {model} yet." });
            if (!_cat.IsConnected)
                return BadRequest(new { error = "The radio is not connected." });

            var rows = new List<object>(table.Count);
            int missing = 0;
            var unanswered = new List<string>();
            foreach (var item in table)
            {
                var raw = await ReadAsync(item.Address, ct);
                if (raw is null)
                {
                    missing++;
                    unanswered.Add(item.Function);
                }
                var choices = raw is null || item.Link ? null : RadioMenuLoad.Choices(item, raw);
                rows.Add(new
                {
                    address = item.Address,
                    section = $"{item.Section} > {item.Group}",
                    function = item.Function,
                    raw,
                    shown = raw is null ? null : item.Decode(raw),
                    tx = item.Tx,
                    link = item.Link,
                    textMax = raw is null || item.Link ? 0 : item.TextMax,
                    choices = choices?.Select(c => new { raw = c, label = item.Decode(c) }),
                });
            }
            if (missing == table.Count)
                return StatusCode(504, new { error = "The radio did not answer any menu reads." });
            return Ok(new { model, missing, unanswered, rows });
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
            if (!await LoadGate.WaitAsync(0, ct))
                return Conflict(new { error = "A radio menu save or load is already running." });
            try
            {
                return await PreviewAsync(file, model, table, ct);
            }
            finally
            {
                LoadGate.Release();
            }
        }

        private static bool IsKnobLine(RadioMenuFileItem line) =>
            RadioKnobs.IsKnobAddress(line.Address)
            || line.Section.StartsWith(RadioKnobs.SectionPrefix, StringComparison.OrdinalIgnoreCase);

        private async Task<IActionResult> PreviewAsync(RadioMenuFile file, string model, IReadOnlyList<RadioMenuItem> table, CancellationToken ct)
        {
            var rows = new List<object>();
            var seen = new HashSet<string>();
            int changes = 0;
            foreach (var line in file.Items.Where(l => !IsKnobLine(l)))
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
                // Already what the radio shows: same, even where Load could not send it (a blank MY CALL).
                if (item.Decode(current) == line.Value.Trim())
                {
                    rows.Add(Row(item, line, "same", current, null, null));
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

            var (knobChanges, knobsStopped) = await PreviewKnobsAsync(file.Items.Where(IsKnobLine).ToList(), model, rows, seen, ct);
            changes += knobChanges;

            string? warning = file.Radio is not null && !string.Equals(file.Radio, model, StringComparison.OrdinalIgnoreCase)
                ? $"This file was saved from a {file.Radio}; the radio now is a {model}. Check each change."
                : null;
            if (knobsStopped is not null)
                warning = warning is null ? $"Front panel levels: {knobsStopped}" : $"{warning} Front panel levels: {knobsStopped}";
            // An older file still loads; it just has no levels to put back.
            if (RadioKnobs.TableFor(model) is not null && !file.Items.Any(IsKnobLine))
            {
                const string old = "This file was saved before YWC saved the front panel levels (MIC GAIN, AMC, PROC, power), so only the menu is loaded. Save a new file to include them.";
                warning = warning is null ? old : $"{warning} {old}";
            }
            _logger.LogInformation("Radio menu load preview: {Rows} lines, {Changes} changes", rows.Count, changes);
            return Ok(new { model, fileRadio = file.Radio, warning, rows });
        }

        /// <summary>
        /// The front-panel lines of a preview. Checks every line first, then
        /// reads the radio in each mode the file has lines for.
        /// </summary>
        private async Task<(int Changes, string? Stopped)> PreviewKnobsAsync(
            List<RadioMenuFileItem> lines, string model, List<object> rows, HashSet<string> seen, CancellationToken ct)
        {
            if (lines.Count == 0) return (0, null);
            var knobs = RadioKnobs.TableFor(model);
            var wanted = new List<(RadioMenuFileItem Line, RadioKnobMode Mode, RadioKnob Knob, int Value)>();
            foreach (var line in lines)
            {
                if (knobs is null || !RadioKnobs.TryParseAddress(line.Address, model, out var mode, out var knob))
                {
                    rows.Add(KnobRow(null, null, line, "unknown", null, null, $"Line {line.Line}: not a front panel level YWC loads on the {model}."));
                    continue;
                }
                if (!seen.Add(line.Address!))
                {
                    rows.Add(KnobRow(mode, knob, line, "unknown", null, null, $"Line {line.Line}: this level is already in the file; the first one is used."));
                    continue;
                }
                if (line.Value.Equals(RadioMenuLoad.NoAnswer, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(KnobRow(mode, knob, line, "skipped", null, null, "The radio did not answer this level when the file was saved."));
                    continue;
                }
                var value = RadioKnobs.Encode(knob, line.Value, out var error);
                if (value is null)
                {
                    rows.Add(KnobRow(mode, knob, line, "error", null, null, error));
                    continue;
                }
                wanted.Add((line, mode, knob, value.Value));
            }
            if (wanted.Count == 0) return (0, null);

            var now = new Dictionary<string, int?>();
            var pass = NewKnobPass();
            var modes = RadioKnobs.ModesFor(model).Where(m => wanted.Any(w => w.Mode == m)).ToList();
            await pass.RunAsync(modes, async mode =>
            {
                foreach (var w in wanted.Where(w => w.Mode == mode))
                    now[w.Line.Address!] = await pass.ReadAsync(w.Knob, ct);
            }, ct);
            if (knobs is not null)
                await RefreshKnobsAsync(knobs, ct);

            int changes = 0;
            foreach (var w in wanted)
            {
                if (!now.TryGetValue(w.Line.Address!, out var current))
                    rows.Add(KnobRow(w.Mode, w.Knob, w.Line, "noanswer", null, null, pass.Stopped ?? "Not read."));
                else if (current is null)
                    rows.Add(KnobRow(w.Mode, w.Knob, w.Line, "noanswer", null, null, "The radio did not answer, so this cannot be checked."));
                else if (current == w.Value)
                    rows.Add(KnobRow(w.Mode, w.Knob, w.Line, "same", current, w.Value, null));
                else
                {
                    rows.Add(KnobRow(w.Mode, w.Knob, w.Line, "change", current, w.Value, null));
                    changes++;
                }
            }
            return (changes, pass.Stopped);
        }

        private static object KnobRow(RadioKnobMode? mode, RadioKnob? knob, RadioMenuFileItem line, string status, int? current, int? wanted, string? message) => new
        {
            address = mode is null || knob is null ? line.Address : RadioKnobs.Address(mode, knob),
            section = line.Section,
            function = mode is null || knob is null ? line.Name : $"{knob.Name} ({mode.Name})",
            current = current is int c && knob is not null ? knob.Show(c) : null,
            wanted = wanted is int v && knob is not null ? knob.Show(v) : line.Value,
            raw = wanted?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            status,
            message,
            tx = true,
        };

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
            _reminderCache = null;
            try
            {
                var results = new List<object>();
                var items = request.Items.DistinctBy(i => i.Address).ToList();
                foreach (var want in items.Where(i => !RadioKnobs.IsKnobAddress(i.Address)))
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
                var stopped = await ApplyKnobsAsync(items.Where(i => RadioKnobs.IsKnobAddress(i.Address)).ToList(), model, results, ct);
                return Ok(new { results, warning = stopped is null ? null : $"Front panel levels: {stopped}" });
            }
            finally
            {
                LoadGate.Release();
            }
        }

        /// <summary>
        /// Sets the ticked front-panel levels, one mode at a time: each is read,
        /// set if it differs, and read back, then the mode is put back.
        /// </summary>
        private async Task<string?> ApplyKnobsAsync(List<ApplyItem> items, string model, List<object> results, CancellationToken ct)
        {
            if (items.Count == 0) return null;
            var knobs = RadioKnobs.TableFor(model);
            var wanted = new List<(string Address, RadioKnobMode Mode, RadioKnob Knob, int Value)>();
            foreach (var want in items)
            {
                if (knobs is null || !RadioKnobs.TryParseAddress(want.Address, model, out var mode, out var knob))
                {
                    results.Add(new { address = want.Address, function = (string?)null, now = (string?)null,
                        status = "error", message = "Not a front panel level on this radio." });
                    continue;
                }
                if (!int.TryParse(want.Raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
                    || value < knob.Min || value > knob.Max)
                {
                    results.Add(KnobResult(want.Address, mode, knob, "error", null, $"{knob.Name} takes {knob.Min} to {knob.Max}."));
                    continue;
                }
                wanted.Add((want.Address, mode, knob, value));
            }
            if (wanted.Count == 0) return null;

            var done = new HashSet<string>();
            var pass = NewKnobPass();
            var modes = RadioKnobs.ModesFor(model).Where(m => wanted.Any(w => w.Mode == m)).ToList();
            await pass.RunAsync(modes, async mode =>
            {
                foreach (var w in wanted.Where(w => w.Mode == mode))
                {
                    done.Add(w.Address);
                    var current = await pass.ReadAsync(w.Knob, ct);
                    if (current is null)
                    {
                        results.Add(KnobResult(w.Address, w.Mode, w.Knob, "error", null, "The radio did not answer, so nothing was sent."));
                        continue;
                    }
                    if (current == w.Value)
                    {
                        results.Add(KnobResult(w.Address, w.Mode, w.Knob, "same", current, null));
                        continue;
                    }
                    _logger.LogInformation("Radio menu load: {Mode} {Knob} {From} -> {To}", w.Mode.Name, w.Knob.Name, current, w.Value);
                    await _cat.SendWithoutReplyAsync(RadioKnobs.SetCommand(w.Knob, w.Value), ClientId, ct);
                    var after = await pass.ReadAsync(w.Knob, ct);
                    results.Add(after == w.Value
                        ? KnobResult(w.Address, w.Mode, w.Knob, "ok", after, null)
                        : KnobResult(w.Address, w.Mode, w.Knob, "mismatch", after, after is null
                            ? "Sent, but the radio did not answer the read back."
                            : $"Sent, but the radio now shows {w.Knob.Show(after.Value)}."));
                }
            }, ct);
            foreach (var w in wanted.Where(w => !done.Contains(w.Address)))
                results.Add(KnobResult(w.Address, w.Mode, w.Knob, "error", null, pass.Stopped ?? "Not sent."));
            if (knobs is not null)
                await RefreshKnobsAsync(knobs, ct);
            return pass.Stopped;
        }

        private static object KnobResult(string address, RadioKnobMode mode, RadioKnob knob, string status, int? now, string? message) => new
        {
            address,
            function = (string?)$"{knob.Name} ({mode.Name})",
            now = now is int v ? knob.Show(v) : null,
            status,
            message,
        };

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
