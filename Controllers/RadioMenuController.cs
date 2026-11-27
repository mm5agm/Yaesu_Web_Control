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
    /// The file also holds the front-panel settings (MIC GAIN, RF GAIN, AGC
    /// and so on, see <see cref="RadioKnobs"/>). Reading or setting those the
    /// radio keeps per mode changes the main band's mode, one mode at a time,
    /// and puts it back afterwards; never while the radio transmits.
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
        private readonly RadioMenuSessionWatch _watch;
        private readonly ILogger<RadioMenuController> _logger;

        public RadioMenuController(ICatClient cat, ISettingsService settings, RadioStateService radio,
            RadioMenuSessionWatch watch, ILogger<RadioMenuController> logger)
        {
            _cat = cat;
            _settings = settings;
            _radio = radio;
            _watch = watch;
            _logger = logger;
        }

        /// <summary>A save or load pass is over: level reports stop being YWC's own.</summary>
        private void EndPass()
        {
            _watch.Changes.PassEnded(DateTime.Now);
            LoadGate.Release();
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
        /// After a pass, read the settings again through the dispatcher, so the
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
            _watch.Changes.PassStarted();
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
                    // Per-band settings are saved for the band the radio is on now.
                    var band = await pass.ReadBandAsync(ct);
                    if (band is null)
                        _logger.LogWarning("Radio menu save: the radio did not say its frequency, so the per-band settings are not saved");
                    foreach (var knob in knobs.Where(k => !k.PerMode && (band is not null || !k.PerBand)))
                        knobReadings.Add(new RadioKnobReading(null, knob, await pass.ReadAsync(knob, ct), knob.PerBand ? band : null));
                    await pass.RunAsync(RadioKnobs.ModesFor(model), async mode =>
                    {
                        foreach (var knob in knobs.Where(k => k.PerMode && k.AppliesIn(mode) && (band is not null || !k.PerBand)))
                            knobReadings.Add(new RadioKnobReading(mode, knob, await pass.ReadAsync(knob, ct), knob.PerBand ? band : null));
                    }, ct);
                    knobsStopped = pass.Stopped;
                    if (knobsStopped is not null)
                        _logger.LogWarning("Radio menu save: front panel settings: {Why}", knobsStopped);
                    await RefreshKnobsAsync(knobs, ct);
                }
            }
            finally
            {
                EndPass();
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
            if (knobsStopped is null) _watch.Changes.Saved();
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
        /// settings (those kept per mode, in that mode), never a mode change. The banner on every page
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
                string? mode = null, band = null;
                var live = new Dictionary<string, int?>();
                var knobs = RadioKnobs.TableFor(model);
                if (newest is not null && knobs is not null)
                {
                    if (await ReadLevelsNowAsync(model, knobs, ct) is not { } read)
                        return Ok(new { supported = true, pending = true });
                    (mode, band, live) = read;
                }
                result = RadioMenuReminder.Check(newest, DateTime.Now, model, mode, band, live);
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
        /// The main band's mode and band, and the front-panel settings as they
        /// are now (those kept per mode, in that mode). Reads only. Null if a
        /// save or load is running (the gate is busy).
        /// </summary>
        private async Task<(string? Mode, string? Band, Dictionary<string, int?> Live)?> ReadLevelsNowAsync(
            string model, IReadOnlyList<RadioKnob> knobs, CancellationToken ct)
        {
            if (!await LoadGate.WaitAsync(0, ct)) return null;
            try
            {
                var pass = NewKnobPass();
                var mode = await pass.ReadModeAsync(ct);
                var band = await pass.ReadBandAsync(ct);
                var inMode = RadioKnobs.ModesFor(model).FirstOrDefault(m => m.Code == mode);
                var live = new Dictionary<string, int?>();
                foreach (var knob in knobs.Where(k => !k.PerMode || (inMode is not null && k.AppliesIn(inMode))))
                    live[knob.Command] = await pass.ReadAsync(knob, ct);
                return (mode, band, live);
            }
            finally
            {
                LoadGate.Release();
            }
        }

        /// <summary>
        /// GET /api/radiomenu/exit-check — closing YWC (the Close YWC page or
        /// the tray's Exit): should it ask to save the radio menu first? Asks
        /// when a front-panel level differs from the newest file, in the mode
        /// read now or in any mode the user changed one in while YWC ran.
        /// Reads only; if the radio cannot be read, the session's record is used.
        /// </summary>
        [HttpGet("exit-check")]
        public async Task<IActionResult> ExitCheck(CancellationToken ct)
        {
            var model = _settings.GetCachedSettings().RadioModel;
            var knobs = RadioKnobs.TableFor(model);
            if (RadioMenuCat.TableFor(model) is null || knobs is null)
                return Ok(new { ask = false });

            string? mode = null, band = null;
            IReadOnlyDictionary<string, int?> live = new Dictionary<string, int?>();
            if (_cat.IsConnected)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(4));
                    if (await ReadLevelsNowAsync(model, knobs, cts.Token) is { } read)
                        (mode, band, live) = read;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Slow radio: go on with what the session saw.
                }
            }

            var (ask, message, changes) = RadioMenuReminder.ExitCheck(
                NewestSave(model), DateTime.Now, model, _watch.Changes.Touched(), mode, band, live);
            return Ok(new { ask, message, changes, connected = _cat.IsConnected });
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
            _watch.Changes.PassStarted();
            try
            {
                return await PreviewAsync(file, model, table, ct);
            }
            finally
            {
                EndPass();
            }
        }

        private static bool IsKnobLine(RadioMenuFileItem line) =>
            RadioKnobs.IsKnobAddress(line.Address)
            || RadioKnobs.IsKnobSection(line.Section);

        private async Task<IActionResult> PreviewAsync(RadioMenuFile file, string model, IReadOnlyList<RadioMenuItem> table, CancellationToken ct)
        {
            var rows = new List<object>();
            var seen = new HashSet<string>();
            int changes = 0;
            bool sameMenu = RadioMenuCat.SameMenu(file.Radio, model);
            foreach (var line in file.Items.Where(l => !IsKnobLine(l)))
            {
                if (!sameMenu)
                {
                    rows.Add(Row(null, line, "unknown", null, null, $"Line {line.Line}: saved from a {file.Radio}, whose menu is different from the {model}'s, so it is not loaded."));
                    continue;
                }
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

            string? warning = file.Radio is null || string.Equals(file.Radio, model, StringComparison.OrdinalIgnoreCase)
                ? null
                : sameMenu
                    ? $"This file was saved from a {file.Radio}; the radio now is a {model}. Check each change."
                    : $"This file was saved from a {file.Radio}; the radio now is a {model}. The two menus are different, so no menu items are loaded. Front panel settings that suit the {model} still load; check each change.";
            if (knobsStopped is not null)
                warning = warning is null ? $"Front panel settings: {knobsStopped}" : $"{warning} Front panel settings: {knobsStopped}";
            // An older file still loads; it just has no front panel settings to put back.
            if (RadioKnobs.TableFor(model) is not null && !file.Items.Any(IsKnobLine))
            {
                const string old = "This file was saved before YWC saved the front panel settings (MIC GAIN, RF GAIN, AGC and so on), so only the menu is loaded. Save a new file to include them.";
                warning = warning is null ? old : $"{warning} {old}";
            }
            _logger.LogInformation("Radio menu load preview: {Rows} lines, {Changes} changes", rows.Count, changes);
            return Ok(new { model, fileRadio = file.Radio, warning, rows });
        }

        /// <summary>
        /// The front-panel lines of a preview. Checks every line first, then
        /// reads the radio: the settings not kept per mode straight away, the
        /// others in each mode the file has lines for. A per-band line saved
        /// on another band is shown but not loaded.
        /// </summary>
        private async Task<(int Changes, string? Stopped)> PreviewKnobsAsync(
            List<RadioMenuFileItem> lines, string model, List<object> rows, HashSet<string> seen, CancellationToken ct)
        {
            if (lines.Count == 0) return (0, null);
            var knobs = RadioKnobs.TableFor(model);
            var band = lines.Any(l => l.Address?.Contains('@') == true) ? await NewKnobPass().ReadBandAsync(ct) : null;
            var wanted = new List<(RadioMenuFileItem Line, RadioKnobSlot Slot, int Value)>();
            foreach (var line in lines)
            {
                if (knobs is null || !RadioKnobs.TryParseAddress(line.Address, model, out var slot))
                {
                    rows.Add(KnobRow(null, line, "unknown", null, null, $"Line {line.Line}: not a front panel setting YWC loads on the {model}."));
                    continue;
                }
                if (!seen.Add(line.Address!))
                {
                    rows.Add(KnobRow(slot, line, "unknown", null, null, $"Line {line.Line}: this setting is already in the file; the first one is used."));
                    continue;
                }
                if (line.Value.Equals(RadioMenuLoad.NoAnswer, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(KnobRow(slot, line, "skipped", null, null, "The radio did not answer this setting when the file was saved."));
                    continue;
                }
                var value = RadioKnobs.Encode(slot.Knob, line.Value, out var error);
                if (value is null)
                {
                    rows.Add(KnobRow(slot, line, "error", null, null, error));
                    continue;
                }
                if (WrongBand(slot, band) is { } wrong)
                {
                    rows.Add(KnobRow(slot, line, "skipped", null, value, wrong));
                    continue;
                }
                wanted.Add((line, slot, value.Value));
            }
            if (wanted.Count == 0) return (0, null);

            var now = new Dictionary<string, int?>();
            var pass = await RunSlotsAsync(wanted, w => w.Slot, model, async (w, p) =>
                now[w.Line.Address!] = await p.ReadAsync(w.Slot.Knob, ct), ct);
            if (knobs is not null)
                await RefreshKnobsAsync(knobs, ct);

            int changes = 0;
            foreach (var w in wanted)
            {
                if (!now.TryGetValue(w.Line.Address!, out var current))
                    rows.Add(KnobRow(w.Slot, w.Line, "noanswer", null, null, pass.Stopped ?? "Not read."));
                else if (current is null)
                    rows.Add(KnobRow(w.Slot, w.Line, "noanswer", null, null, "The radio did not answer, so this cannot be checked."));
                else if (current == w.Value)
                    rows.Add(KnobRow(w.Slot, w.Line, "same", current, w.Value, null));
                else
                {
                    rows.Add(KnobRow(w.Slot, w.Line, "change", current, w.Value, null));
                    changes++;
                }
            }
            return (changes, pass.Stopped);
        }

        /// <summary>Why a per-band line is not loaded: it was saved on another band. Null if it can be.</summary>
        private static string? WrongBand(RadioKnobSlot slot, string? band) =>
            slot.Band is null || slot.Band == band ? null
            : band is null ? $"Saved for {slot.Band}; the radio did not say which band it is on, so this is not loaded."
            : $"Saved for {slot.Band}; the radio is on {band} now, so this is not loaded. Change to {slot.Band} and load again to set it.";

        /// <summary>
        /// Runs <paramref name="each"/> for every slot, in the table's order
        /// (NAR before WIDTH): those not kept per mode straight away, then the
        /// others one mode at a time, putting the mode back afterwards.
        /// </summary>
        private async Task<RadioKnobPass> RunSlotsAsync<T>(List<T> wanted, Func<T, RadioKnobSlot> slotOf, string model,
            Func<T, RadioKnobPass, Task> each, CancellationToken ct)
        {
            var order = (RadioKnobs.TableFor(model) ?? []).Select((k, i) => (k.Command, i)).ToDictionary(x => x.Command, x => x.i);
            var sorted = wanted.OrderBy(w => order.GetValueOrDefault(slotOf(w).Knob.Command)).ToList();
            var pass = NewKnobPass();
            foreach (var w in sorted.Where(w => slotOf(w).Mode is null))
                await each(w, pass);
            var modes = RadioKnobs.ModesFor(model).Where(m => sorted.Any(w => slotOf(w).Mode == m)).ToList();
            if (modes.Count > 0)
                await pass.RunAsync(modes, async mode =>
                {
                    foreach (var w in sorted.Where(w => slotOf(w).Mode == mode))
                        await each(w, pass);
                }, ct);
            return pass;
        }

        private static object KnobRow(RadioKnobSlot? slot, RadioMenuFileItem line, string status, int? current, int? wanted, string? message) => new
        {
            address = slot?.Address ?? line.Address,
            section = line.Section,
            function = slot?.Label ?? line.Name,
            current = current is int c && slot is not null ? slot.Knob.Show(c) : null,
            wanted = wanted is int v && slot is not null ? slot.Knob.Show(v) : line.Value,
            raw = wanted?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            status,
            message,
            tx = slot?.Knob.Tx ?? true,
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
            _watch.Changes.PassStarted();
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
                return Ok(new { results, warning = stopped is null ? null : $"Front panel settings: {stopped}" });
            }
            finally
            {
                EndPass();
            }
        }

        /// <summary>
        /// Sets the ticked front-panel settings: each is read, set if it
        /// differs, and read back. Those kept per mode are set one mode at a
        /// time, then the mode is put back. A per-band one is set only on the
        /// band it was saved for.
        /// </summary>
        private async Task<string?> ApplyKnobsAsync(List<ApplyItem> items, string model, List<object> results, CancellationToken ct)
        {
            if (items.Count == 0) return null;
            var knobs = RadioKnobs.TableFor(model);
            var band = items.Any(i => i.Address.Contains('@')) ? await NewKnobPass().ReadBandAsync(ct) : null;
            var wanted = new List<(string Address, RadioKnobSlot Slot, int Value)>();
            foreach (var want in items)
            {
                if (knobs is null || !RadioKnobs.TryParseAddress(want.Address, model, out var slot))
                {
                    results.Add(new { address = want.Address, function = (string?)null, now = (string?)null,
                        status = "error", message = "Not a front panel setting on this radio." });
                    continue;
                }
                var knob = slot.Knob;
                if (!int.TryParse(want.Raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
                    || value < knob.Min || value > knob.Max)
                {
                    results.Add(KnobResult(want.Address, slot, "error", null, $"{knob.Name} takes {knob.Min} to {knob.Max}."));
                    continue;
                }
                if (WrongBand(slot, band) is { } wrong)
                {
                    results.Add(KnobResult(want.Address, slot, "error", null, wrong));
                    continue;
                }
                wanted.Add((want.Address, slot, value));
            }
            if (wanted.Count == 0) return null;

            var done = new HashSet<string>();
            var pass = await RunSlotsAsync(wanted, w => w.Slot, model, async (w, p) =>
            {
                done.Add(w.Address);
                var current = await p.ReadAsync(w.Slot.Knob, ct);
                if (current is null)
                {
                    results.Add(KnobResult(w.Address, w.Slot, "error", null, "The radio did not answer, so nothing was sent."));
                    return;
                }
                if (current == w.Value)
                {
                    results.Add(KnobResult(w.Address, w.Slot, "same", current, null));
                    return;
                }
                _logger.LogInformation("Radio menu load: {Knob} {From} -> {To}", w.Slot.Label, current, w.Value);
                await _cat.SendWithoutReplyAsync(RadioKnobs.SetCommand(w.Slot.Knob, w.Value), ClientId, ct);
                var after = await p.ReadAsync(w.Slot.Knob, ct);
                results.Add(after == w.Value
                    ? KnobResult(w.Address, w.Slot, "ok", after, null)
                    : KnobResult(w.Address, w.Slot, "mismatch", after, after is null
                        ? "Sent, but the radio did not answer the read back."
                        : $"Sent, but the radio now shows {w.Slot.Knob.Show(after.Value)}."));
            }, ct);
            foreach (var w in wanted.Where(w => !done.Contains(w.Address)))
                results.Add(KnobResult(w.Address, w.Slot, "error", null, pass.Stopped ?? "Not sent."));
            if (knobs is not null)
                await RefreshKnobsAsync(knobs, ct);
            return pass.Stopped;
        }

        private static object KnobResult(string address, RadioKnobSlot slot, string status, int? now, string? message) => new
        {
            address,
            function = (string?)slot.Label,
            now = now is int v ? slot.Knob.Show(v) : null,
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
