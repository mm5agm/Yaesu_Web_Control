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
    string GetSavePath();
    bool IsDevelopmentMode { get; }
    CalibrationImportResult ImportEmailedCalibrationIntoDefault(string? emailText);
}

public class CalibrationService : ICalibrationService
{
    private readonly CalibrationStorage _storage;

    public CalibrationFile Current { get; private set; }

    public CalibrationService(CalibrationStorage storage)
    {
        _storage = storage;
        Current = _storage.Load();
    }

    public bool IsDevelopmentMode => _storage.IsDevelopmentMode;

    public string GetSavePath() => _storage.GetActivePath();

    public CalibrationImportResult ImportEmailedCalibrationIntoDefault(string? emailText) =>
        _storage.ImportEmailedCalibrationIntoDefault(emailText);

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
    }

    public void ResetToDefault()
    {
        Current = _storage.LoadDefault();
        _storage.Save(Current);
    }
}

