using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yaesu_Web_Control.Models.Calibration;
using Microsoft.Extensions.Hosting;

namespace Yaesu_Web_Control.Services;

// Result of folding a user-emailed calibration into a shipped default file.
// Purely a developer convenience (dev build only) — see ImportEmailedCalibrationIntoDefault.
public class CalibrationImportResult
{
    public bool Ok { get; init; }
    public bool Changed { get; init; }
    public string? Model { get; init; }
    public string? FileName { get; init; }
    public List<string> Updated { get; init; } = new();
    public List<string> Structural { get; init; } = new();
    public string Message { get; init; } = "";

    // ── Contributions store (see CalibrationContributionsStore) ──
    // Meters in the incoming file that were the shipped placeholders echoed
    // back untouched, so they took no part in the median. Nearly every
    // contribution has some: people calibrate the meters they care about.
    public List<string> Unmeasured { get; init; } = new();

    // Contributed meters thrown out by validation, with the reason. Not
    // failures — the import still applied everything else — but read them.
    public List<string> Refused { get; init; } = new();

    // Where contributors disagreed, worst first. Big spreads are the signal
    // that a contribution is a mis-measurement rather than a real difference.
    public List<PointSpread> Spread { get; init; } = new();

    // How many contributions fed at least one meter of this result.
    public int Contributors { get; init; }

    // One-line dump of the raw values actually parsed out of the pasted text.
    // Logged on every import so a stale clipboard is obvious: if this doesn't
    // match what the user just saved, the paste is old, not the diff broken.
    public string IncomingSummary { get; set; } = "";

    public static CalibrationImportResult Fail(string msg) => new() { Ok = false, Message = msg };
}

public class CalibrationStorage
{
    private readonly bool _isDevelopment;
    private readonly ISettingsService _settings;
    private readonly CalibrationContributionsStore _contributions;
    private readonly string _wwwrootPath;
    private readonly string _userPath;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public CalibrationStorage(
        IHostEnvironment hostEnvironment,
        ISettingsService settings,
        CalibrationContributionsStore contributions)
    {
        _isDevelopment = hostEnvironment.IsDevelopment();
        _settings = settings;
        _contributions = contributions;
        _wwwrootPath = Path.Combine(hostEnvironment.ContentRootPath, "wwwroot");
        _userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM",
            "Yaesu Web Control",
            "calibration.user.json");
    }

    public bool IsDevelopmentMode => _isDevelopment;

    // Per-model default calibration path. When the user has an FTdx10
    // configured, we look for calibration.default.FTdx10.json first; if
    // it doesn't exist we fall back to the generic calibration.default.json
    // (currently a copy of the FTdx101MP-calibrated table — the only model
    // with measured data so far). This lets us ship per-model placeholders
    // that improve over time as users send in calibration measurements,
    // without forcing every install to re-calibrate from scratch.
    private string GetDefaultPath()
    {
        var radioModel = _settings.GetSettingsAsync().GetAwaiter().GetResult().RadioModel ?? "";
        // Sanitise: only letters/digits/hyphen allowed (model names use these).
        // Prevents path-traversal even though the value comes from a dropdown.
        if (Regex.IsMatch(radioModel, @"^[A-Za-z0-9\-]+$"))
        {
            var modelSpecific = Path.Combine(_wwwrootPath, $"calibration.default.{radioModel}.json");
            if (File.Exists(modelSpecific)) return modelSpecific;
        }
        return Path.Combine(_wwwrootPath, "calibration.default.json");
    }

    // Both Load and Save target the user APPDATA file regardless of whether
    // ASPNETCORE_ENVIRONMENT is Development. The earlier behaviour pointed
    // dev-mode at wwwroot/calibration.default.<model>.json, which meant
    // anyone running YWC via `dotnet run` from source would silently overwrite
    // the SHIPPED defaults with their own calibration edits. Colin caught this
    // on 2026-06-12 after a few rounds of bench-testing for Jacek's #29 — the
    // FTdx101MP shipped defaults were full of his test mutations. Developers
    // who genuinely want to edit shipped defaults should use a text editor on
    // the source file directly, not the in-app Meter Calibration page.
    public string GetActivePath() => _userPath;

    // ── Developer-only: fold a user-emailed calibration into a shipped default ──
    //
    // A user runs the app, calibrates their radio, and sends Colin the resulting
    // calibration JSON (pasted in an email). This reads that pasted text and
    // edits only the individual raw values that changed, in place, in
    // wwwroot/calibration.default.<Model>.json — the same minimal-diff surgery a
    // hand edit would make, so the change shows up as a tiny, reviewable git diff
    // on the source file. Gated to the development build; a git diff is still the
    // safety net before committing. Ported from IWC to keep the two in step.

    private static readonly Regex RawRe =
        new(@"(""raw""\s*:\s*)(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private const string DefaultPre = "calibration.default.", DefaultSuf = ".json";

    // Shipped per-model default files (excluding the generic fallback), and the
    // model names derived from their filenames.
    private (List<string> files, List<string> models) KnownDefaults()
    {
        var files = Directory.GetFiles(_wwwrootPath, "calibration.default.*.json")
            .Where(f => !Path.GetFileName(f).Equals("calibration.default.json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var models = files
            .Select(p => Path.GetFileName(p))
            .Select(n => n.Substring(DefaultPre.Length, n.Length - DefaultPre.Length - DefaultSuf.Length))
            .ToList();
        return (files, models);
    }

    // Fold the calibration YWC currently has loaded straight into the shipped
    // default for the configured radio — no email, no clipboard. This is the
    // path Colin actually uses (calibrate his own radio, then promote it to the
    // shipped table); routing that through mailto: + clipboard was needless, and
    // the clipboard hop turned out to be the fragile part: a failed copy silently
    // re-imported an older calibration. The emailed-text path below stays for
    // calibrations that genuinely arrive from other users.
    public CalibrationImportResult ImportCalibrationIntoDefault(
        CalibrationFile? incoming, ContributionMeta? meta = null)
    {
        if (!_isDevelopment)
            return CalibrationImportResult.Fail("Importing to shipped defaults is only available in the development build.");
        if (incoming is null || incoming.Meters.Count == 0)
            return CalibrationImportResult.Fail("Nothing to import — the current calibration has no meters.");

        var (files, models) = KnownDefaults();
        var configured = _settings.GetSettingsAsync().GetAwaiter().GetResult().RadioModel ?? "";
        var model = models.FirstOrDefault(m => m.Equals(configured, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return CalibrationImportResult.Fail(
                $"No shipped default file for the configured radio ('{configured}'). " +
                "Check the Radio Model in Settings.");

        foreach (var m in incoming.Meters) m.Normalize();
        return ApplyIntoDefault(incoming, model, files, meta);
    }

    public CalibrationImportResult ImportEmailedCalibrationIntoDefault(
        string? emailText, ContributionMeta? meta = null)
    {
        if (!_isDevelopment)
            return CalibrationImportResult.Fail("Importing to shipped defaults is only available in the development build.");
        if (string.IsNullOrWhiteSpace(emailText))
            return CalibrationImportResult.Fail("Nothing to import — the clipboard was empty.");

        // Which models do we have shipped default files for?
        var (defaultFiles, models) = KnownDefaults();

        // Detect the radio from the email text (its body/subject names the model);
        // longest match first so "FTdx10" doesn't win inside "FTdx101MP".
        var lower = emailText.ToLowerInvariant();
        var model = models
            .Where(m => lower.Contains(m.ToLowerInvariant()))
            .OrderByDescending(m => m.Length)
            .FirstOrDefault();
        // Fall back to the currently-configured radio if the text didn't name one.
        if (model is null)
        {
            var configured = _settings.GetSettingsAsync().GetAwaiter().GetResult().RadioModel ?? "";
            model = models.FirstOrDefault(m => m.Equals(configured, StringComparison.OrdinalIgnoreCase));
        }
        if (model is null)
            return CalibrationImportResult.Fail(
                "Couldn't tell which radio this is for. Copy the whole email (its text names the radio), " +
                "or select that radio in Settings first.");

        // Pull the JSON object out of the surrounding email text.
        int s = emailText.IndexOf('{'), e = emailText.LastIndexOf('}');
        if (s < 0 || e <= s)
            return CalibrationImportResult.Fail(
                "No calibration JSON found. Make sure you copied the whole email including the { ... } block.");
        CalibrationFile incoming;
        try
        {
            incoming = JsonSerializer.Deserialize<CalibrationFile>(emailText.Substring(s, e - s + 1), ReadOptions) ?? new();
        }
        catch (Exception ex)
        {
            return CalibrationImportResult.Fail("The calibration JSON couldn't be parsed: " + ex.Message);
        }
        foreach (var m in incoming.Meters) m.Normalize();   // fold legacy "label" into "Radio"
        if (incoming.Meters.Count == 0)
            return CalibrationImportResult.Fail("The pasted data has no meters.");

        return ApplyIntoDefault(incoming, model, defaultFiles, meta);
    }

    // Re-derive a shipped default from the contributions already on disk, with
    // nothing new coming in. This is how a bad contribution gets undone: set
    // "excluded": true on it in calibration-contributions/<Model>.json, run
    // this, and the shipped numbers go back to the median of what's left. It is
    // also the way to pick up a hand edit to the store.
    public CalibrationImportResult RecomputeIntoDefault(string? requestedModel = null)
    {
        if (!_isDevelopment)
            return CalibrationImportResult.Fail("Recomputing shipped defaults is only available in the development build.");

        var (files, models) = KnownDefaults();
        var wanted = string.IsNullOrWhiteSpace(requestedModel)
            ? _settings.GetSettingsAsync().GetAwaiter().GetResult().RadioModel ?? ""
            : requestedModel;
        var model = models.FirstOrDefault(m => m.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return CalibrationImportResult.Fail(
                $"No shipped default file for '{wanted}'. Check the Radio Model in Settings.");

        return ApplyIntoDefault(null, model, files, null);
    }

    // The shared half: record `incoming` as a contribution, work out what the
    // shipped default should now hold across ALL contributions, and apply that
    // by minimal-diff surgery into the default file for `model`.
    //
    // Note what does NOT happen here any more: `incoming`'s values are not
    // written straight to the file. They are one vote. The numbers that land in
    // the file come out of CalibrationContributionsStore.Recompute, which is why
    // a second contributor no longer erases the first. The surgery below is
    // unchanged — same regex, same span, same byte-accurate rewrite — it is only
    // being fed from a different place.
    //
    // A null `incoming` means "recompute from what's already in the store" —
    // the path used after excluding a bad contribution or editing the file by
    // hand. Nothing is recorded then; the store is only read.
    private CalibrationImportResult ApplyIntoDefault(
        CalibrationFile? incoming, string model, List<string> defaultFiles, ContributionMeta? meta)
    {
        const string pre = DefaultPre, suf = DefaultSuf;
        var incomingSummary = incoming is null ? "" : CalibrationService.Summarise(incoming);

        var targetPath = defaultFiles.First(f =>
            Path.GetFileName(f).Equals($"{pre}{model}{suf}", StringComparison.OrdinalIgnoreCase));

        // Read byte-accurate so we preserve the file's BOM (FTdx10) and CRLF endings.
        var rawBytes = File.ReadAllBytes(targetPath);
        bool hasBom = rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF;
        var text = hasBom ? Encoding.UTF8.GetString(rawBytes, 3, rawBytes.Length - 3)
                          : Encoding.UTF8.GetString(rawBytes);

        // Parse the current file WITHOUT the sorting loader so point order matches the text.
        var current = JsonSerializer.Deserialize<CalibrationFile>(text, ReadOptions) ?? new();
        foreach (var m in current.Meters) m.Normalize();

        // Record the contribution, then ask the store what the file should hold.
        // RememberPlaceholders runs FIRST and against the file as it stands, so
        // a contributor who left a meter at the shipped value is recognised as
        // not having measured it — including when that shipped value is one we
        // derived ourselves from earlier contributions.
        var store = _contributions.Load(model);
        CalibrationContributionsStore.RememberPlaceholders(store, current);
        var contribution = incoming is null
            ? null
            : CalibrationContributionsStore.Record(store, incoming, meta, AppVersion.Current);
        var agg = CalibrationContributionsStore.Recompute(store, current);

        var updated = new List<string>();
        var structural = new List<string>(agg.Structural);

        foreach (var cur in current.Meters)
        {
            // Absent from Values means nothing usable was contributed for this
            // meter — keep the shipped placeholder rather than invent a number.
            if (!agg.Values.TryGetValue(cur.Name, out var vals)) continue;
            if (vals.Count != cur.Points.Count) { structural.Add(cur.Name); continue; }

            var changes = new Dictionary<int, double>();
            for (int j = 0; j < cur.Points.Count; j++)
                if (cur.Points[j].Raw != vals[j])
                    changes[j] = vals[j];
            if (changes.Count == 0) continue;

            var span = FindPointsSpan(text, cur.Name);
            if (span is null) { structural.Add(cur.Name); continue; }
            var (start, end) = span.Value;

            int idx = 0;
            var region = RawRe.Replace(text.Substring(start, end - start), mm =>
            {
                int i = idx++;
                return changes.TryGetValue(i, out var v) ? mm.Groups[1].Value + FormatRaw(v) : mm.Value;
            });
            text = text[..start] + region + text[end..];
            updated.Add($"{cur.Name} ({changes.Count})");
        }

        if (updated.Count == 0)
        {
            // The contribution is still worth keeping even when it moved
            // nothing: it is a second radio agreeing with the shipped numbers,
            // which is exactly what a median needs to become trustworthy.
            _contributions.Save(store);

            return new CalibrationImportResult
            {
                Ok = true,
                Changed = false,
                Model = model,
                FileName = Path.GetFileName(targetPath),
                Structural = structural,
                Unmeasured = contribution?.Unmeasured ?? new(),
                Refused = agg.Refused,
                Spread = agg.Spread,
                Contributors = agg.Contributors,
                IncomingSummary = incomingSummary,
                Message = structural.Count > 0
                    ? $"No value changes for {model}. Some meters differ structurally (point labels/count) and need a hand edit."
                    : contribution is null
                        ? $"Recomputed {model} from {agg.Contributors} contribution(s) — the shipped default already holds those numbers."
                        : $"Recorded for {model}, but the shipped default doesn't change — the median across " +
                          $"{agg.Contributors} contribution(s) is what it already holds. " +
                          "Commit calibration-contributions/ anyway; the record is the point."
            };
        }

        var outBytes = Encoding.UTF8.GetBytes(text);
        if (hasBom) outBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(outBytes).ToArray();
        File.WriteAllBytes(targetPath, outBytes);

        // The numbers just written are the placeholders the NEXT contributor
        // will send back untouched for every meter they didn't calibrate.
        // Registered from the file we just wrote, not from `agg`, so the record
        // is of what actually shipped.
        CalibrationContributionsStore.RememberPlaceholders(
            store, JsonSerializer.Deserialize<CalibrationFile>(text, ReadOptions) ?? new());
        _contributions.Save(store);

        return new CalibrationImportResult
        {
            Ok = true,
            Changed = true,
            Model = model,
            FileName = Path.GetFileName(targetPath),
            Updated = updated,
            Structural = structural,
            Unmeasured = contribution?.Unmeasured ?? new(),
            Refused = agg.Refused,
            Spread = agg.Spread,
            Contributors = agg.Contributors,
            IncomingSummary = incomingSummary,
            Message = $"Updated {Path.GetFileName(targetPath)} from the median of {agg.Contributors} " +
                      "contribution(s). Review the git diff — both the default file and " +
                      "calibration-contributions/ — then commit."
        };
    }

    // Offsets of the [...] of the named meter's "points" array within the raw file text.
    private static (int start, int end)? FindPointsSpan(string text, string meterName)
    {
        int nameIdx = text.IndexOf($"\"name\": \"{meterName}\"", StringComparison.Ordinal);
        if (nameIdx < 0) nameIdx = text.IndexOf($"\"name\":\"{meterName}\"", StringComparison.Ordinal);
        if (nameIdx < 0) return null;
        int ptsIdx = text.IndexOf("\"points\"", nameIdx, StringComparison.Ordinal);
        if (ptsIdx < 0) return null;
        int open = text.IndexOf('[', ptsIdx);
        if (open < 0) return null;
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '[') depth++;
            else if (text[i] == ']') { depth--; if (depth == 0) return (open, i + 1); }
        }
        return null;
    }

    private static string FormatRaw(double v) =>
        v == Math.Truncate(v)
            ? ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public CalibrationFile Load()
    {
        EnsureUserCalibrationExists();
        return LoadFromPath(_userPath);
    }

    public CalibrationFile LoadDefault()
    {
        return LoadFromPath(GetDefaultPath());
    }

    public void Save(CalibrationFile file)
    {
        var targetPath = GetActivePath();
        var directory = Path.GetDirectoryName(targetPath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(file, WriteOptions);
        File.WriteAllText(targetPath, json);
    }

    private void EnsureUserCalibrationExists()
    {
        var userDir = Path.GetDirectoryName(_userPath);
        if (!string.IsNullOrWhiteSpace(userDir))
        {
            Directory.CreateDirectory(userDir);
        }

        if (!File.Exists(_userPath))
        {
            // First run — copy the model-specific default wholesale so a
            // brand-new FTdx10 user starts with FTdx10 placeholders rather
            // than the legacy FTdx101MP table.
            File.Copy(GetDefaultPath(), _userPath, overwrite: false);
            return;
        }

        // File already exists — merge any meters that are in the default but missing from the user file.
        // This handles the case where new meters are added to calibration after the user's first install.
        MergeDefaultsIntoUserFile();
    }

    private void MergeDefaultsIntoUserFile()
    {
        try
        {
            var userFile    = LoadFromPath(_userPath);
            var defaultFile = LoadFromPath(GetDefaultPath());

            var changed = false;
            foreach (var defaultMeter in defaultFile.Meters)
            {
                if (!userFile.Meters.Any(m => m.Name == defaultMeter.Name))
                {
                    userFile.Meters.Add(defaultMeter);
                    changed = true;
                }
            }

            if (changed)
            {
                Save(userFile);
            }
        }
        catch
        {
            // If the merge fails for any reason, leave the user file as-is.
        }
    }

    private static CalibrationFile LoadFromPath(string path)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<CalibrationFile>(json, ReadOptions) ?? new CalibrationFile();

        foreach (var meter in file.Meters)
        {
            meter.Normalize();
            meter.Points = meter.Points.OrderBy(p => p.Raw).ToList();
        }

        return file;
    }
}
