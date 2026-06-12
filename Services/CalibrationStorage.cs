using System.Text.Json;
using Yaesu_Web_Control.Models.Calibration;
using Microsoft.Extensions.Hosting;

namespace Yaesu_Web_Control.Services;

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
}
