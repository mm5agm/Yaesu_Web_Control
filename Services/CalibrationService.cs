using Yaesu_Web_Control.Models.Calibration;

namespace Yaesu_Web_Control.Services;

public interface ICalibrationService
{
    Dictionary<string, List<CalibrationPoint>> GetAllCalibrationTables();
    CalibrationFile Current { get; }
    double CalibrateNumeric(string meterName, double raw);
    string CalibrateSMeterLabel(double raw);
    void Save(CalibrationFile file);
    void ResetToDefault();

    // Re-read the user calibration file from disk into Current. Returns false
    // (keeping the previous Current) if the file can't be read or parsed.
    bool Reload();
    string GetSavePath();
    bool IsDevelopmentMode { get; }

    // Development-only: record a user-emailed calibration as a contribution and
    // re-derive the shipped default for its radio from every contribution held.
    // Callers must gate on IsDevelopmentMode first.
    CalibrationImportResult ImportEmailedCalibrationIntoDefault(string? emailText, ContributionMeta? meta = null);

    // Same, but sourced from the calibration this instance already has loaded —
    // no email body, no clipboard.
    CalibrationImportResult ImportCurrentCalibrationIntoDefault(ContributionMeta? meta = null);

    // Re-derive a shipped default from the contributions already on disk,
    // recording nothing new. Used after excluding a bad contribution.
    CalibrationImportResult RecomputeDefaultFromContributions(string? model = null);
}

public class CalibrationService : ICalibrationService
{
    private readonly CalibrationStorage _storage;
    private readonly ILogger<CalibrationService>? _log;

    public CalibrationFile Current { get; private set; }

    public CalibrationService(CalibrationStorage storage, ILogger<CalibrationService>? log = null)
    {
        _storage = storage;
        _log = log;
        Current = _storage.Load();
    }

    // Current is loaded once at construction, so anything that changes the file
    // underneath us — a second YWC instance sharing the same %APPDATA% file, a
    // hand edit, the dev import — used to go unnoticed until the next restart.
    // "Reload From File" on the Meter Calibration page then reloaded from this
    // in-memory copy rather than the file, so it silently re-showed the values
    // already in memory. Colin hit exactly that on 2026-08-01: reverting a test
    // edit appeared to work, but the revert was never what got saved.
    public bool Reload()
    {
        try
        {
            Current = _storage.Load();
            _log?.LogInformation("[cal] reloaded from {Path}: {Summary}",
                _storage.GetActivePath(), Summarise(Current));
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "[cal] reload failed; keeping the calibration already in memory");
            return false;
        }
    }

    // Compact one-line dump of every meter's raw values — enough to tell two
    // calibrations apart in the log without printing the whole JSON.
    internal static string Summarise(CalibrationFile file) =>
        string.Join("; ", file.Meters.Select(m =>
            $"{m.Name}=[{string.Join(",", m.Points.Select(p => p.Raw))}]"));

    public bool IsDevelopmentMode => _storage.IsDevelopmentMode;

    public string GetSavePath() => _storage.GetActivePath();

    public Dictionary<string, List<CalibrationPoint>> GetAllCalibrationTables()
    {
        return Current.Meters
            .GroupBy(m => m.Name)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(m => m.Points).ToList()
            );
    }

    public double CalibrateNumeric(string meterName, double raw)
    {
        var meter = Current.Meters
            .FirstOrDefault(m => m.Name == meterName && m.Type == "numeric");

        if (meter == null || meter.Points.Count == 0)
        {
            return raw;
        }

        var points = meter.Points.OrderBy(p => p.Raw).ToList();

        if (raw <= points.First().Raw)
        {
            return points.First().TryGetRadioNumeric() ?? raw;
        }

        if (raw >= points.Last().Raw)
        {
            return points.Last().TryGetRadioNumeric() ?? raw;
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];

            if (raw >= a.Raw && raw <= b.Raw)
            {
                var t = (raw - a.Raw) / (b.Raw - a.Raw);
                var ra = a.TryGetRadioNumeric() ?? a.Raw;
                var rb = b.TryGetRadioNumeric() ?? b.Raw;
                return ra + t * (rb - ra);
            }
        }

        return raw;
    }

    public string CalibrateSMeterLabel(double raw)
    {
        var meter = Current.Meters
            .FirstOrDefault(m => m.Name == "S-Meter" && m.Type == "s_meter");

        if (meter == null || meter.Points.Count == 0)
        {
            return raw.ToString("F0");
        }

        var points = meter.Points.OrderBy(p => p.Raw).ToList();
        var nearest = points.OrderBy(p => Math.Abs(p.Raw - raw)).First();
        return nearest.Radio ?? raw.ToString("F0");
    }

    public void Save(CalibrationFile file)
    {
        foreach (var meter in file.Meters)
        {
            meter.Normalize();
            meter.Points = meter.Points.OrderBy(p => p.Raw).ToList();
        }

        Current = file;
        _storage.Save(file);
        _log?.LogInformation("[cal] saved to {Path}: {Summary}",
            _storage.GetActivePath(), Summarise(file));
    }

    public void ResetToDefault()
    {
        Current = _storage.LoadDefault();
        _storage.Save(Current);
        _log?.LogInformation("[cal] reset to shipped defaults, written to {Path}: {Summary}",
            _storage.GetActivePath(), Summarise(Current));
    }

    public CalibrationImportResult ImportEmailedCalibrationIntoDefault(
        string? emailText, ContributionMeta? meta = null) =>
        _storage.ImportEmailedCalibrationIntoDefault(emailText, meta);

    // Re-read from disk first so we promote what is actually saved, not a stale
    // in-memory copy.
    public CalibrationImportResult ImportCurrentCalibrationIntoDefault(ContributionMeta? meta = null)
    {
        Reload();
        return _storage.ImportCalibrationIntoDefault(Current, meta);
    }

    public CalibrationImportResult RecomputeDefaultFromContributions(string? model = null) =>
        _storage.RecomputeIntoDefault(model);
}
