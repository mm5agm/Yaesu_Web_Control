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

    public static CalibrationImportResult Fail(string msg) => new() { Ok = false, Message = msg };
}

public class CalibrationStorage
{
    private readonly bool _isDevelopment;
    private readonly ISettingsService _settings;
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

    public CalibrationStorage(IHostEnvironment hostEnvironment, ISettingsService settings)
    {
        _isDevelopment = hostEnvironment.IsDevelopment();
        _settings = settings;
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
        if (System.Text.RegularExpressions.Regex.IsMatch(radioModel, @"^[A-Za-z0-9\-]+$"))
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

    // ---- Developer-only: fold a user-emailed calibration into a SHIPPED default ----
    //
    // The in-app Save deliberately never writes shipped defaults (see the note on
    // GetActivePath — Colin polluted them once by saving live values). This is the
    // opposite, safe case: it takes an EXPLICIT paste of a user's emailed calibration
    // and edits only the individual raw values that changed, in place, in
    // wwwroot/calibration.default.<Model>.json — the same minimal-diff surgery as
    // scripts/merge-calibration.py, so the change shows up as a tiny, reviewable git
    // diff on the source file. Gated to the development build; a git diff is still the
    // safety net before committing. Mirrors the Python tool so the two stay in step.

    private static readonly Regex RawRe = new(@"(""raw""\s*:\s*)(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    public CalibrationImportResult ImportEmailedCalibrationIntoDefault(string? emailText)
    {
        if (!_isDevelopment)
            return CalibrationImportResult.Fail("Importing to shipped defaults is only available in the development build.");
        if (string.IsNullOrWhiteSpace(emailText))
            return CalibrationImportResult.Fail("Nothing to import — the clipboard was empty.");

        // Which models do we have shipped default files for?
        var defaultFiles = Directory.GetFiles(_wwwrootPath, "calibration.default.*.json")
            .Where(f => !Path.GetFileName(f).Equals("calibration.default.json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        const string pre = "calibration.default.", suf = ".json";
        string ModelOf(string path)
        {
            var n = Path.GetFileName(path);
            return n.Substring(pre.Length, n.Length - pre.Length - suf.Length);
        }
        var models = defaultFiles.Select(ModelOf).ToList();

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

        var targetPath = defaultFiles.First(f =>
            Path.GetFileName(f).Equals($"{pre}{model}{suf}", StringComparison.OrdinalIgnoreCase));

        // Read byte-accurate so we preserve the file's BOM (FtdX10) and CRLF endings.
        var rawBytes = File.ReadAllBytes(targetPath);
        bool hasBom = rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF;
        var text = hasBom ? Encoding.UTF8.GetString(rawBytes, 3, rawBytes.Length - 3)
                          : Encoding.UTF8.GetString(rawBytes);

        // Parse the current file WITHOUT the sorting loader so point order matches the text.
        var current = JsonSerializer.Deserialize<CalibrationFile>(text, ReadOptions) ?? new();
        foreach (var m in current.Meters) m.Normalize();

        var incByName = incoming.Meters
            .GroupBy(m => m.Name)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var updated = new List<string>();
        var structural = new List<string>();

        foreach (var cur in current.Meters)
        {
            if (!incByName.TryGetValue(cur.Name, out var inc)) continue;

            var curLabels = cur.Points.Select(p => p.Radio).ToList();
            var incLabels = inc.Points.Select(p => p.Radio).ToList();
            if (!curLabels.SequenceEqual(incLabels))
            {
                // Different point labels/count — a structural change; report, don't guess.
                structural.Add(cur.Name);
                continue;
            }

            var changes = new Dictionary<int, double>();
            for (int j = 0; j < cur.Points.Count; j++)
                if (cur.Points[j].Raw != inc.Points[j].Raw)
                    changes[j] = inc.Points[j].Raw;
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
            return new CalibrationImportResult
            {
                Ok = true,
                Changed = false,
                Model = model,
                FileName = Path.GetFileName(targetPath),
                Structural = structural,
                Message = structural.Count == 0
                    ? $"Nothing to apply for {model} — this calibration already matches the shipped default."
                    : $"No value changes for {model}. Some meters differ structurally (point labels/count) and need a hand edit."
            };
        }

        var outBytes = Encoding.UTF8.GetBytes(text);
        if (hasBom) outBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(outBytes).ToArray();
        File.WriteAllBytes(targetPath, outBytes);

        return new CalibrationImportResult
        {
            Ok = true,
            Changed = true,
            Model = model,
            FileName = Path.GetFileName(targetPath),
            Updated = updated,
            Structural = structural,
            Message = $"Updated {Path.GetFileName(targetPath)}. Review the git diff, then commit."
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
}
