using RadioWebControl.Core.Services.TxAlert;
using Yaesu_Web_Control.Models.Calibration;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Watches SWR and ALC while transmitting and keeps the alert that every
    /// page shows (Discussion #257, f5gkw: a pop-up you can't miss while
    /// N1MM or another program has the radio). The decision is the core's
    /// <see cref="TxAlertMonitor"/>; this side reads the meters MeterPollingService
    /// already keeps in RadioStateService, calibrates SWR with the same table the
    /// gauges use, and serves the result at GET /api/txalert. It sends nothing
    /// to the radio.
    ///
    /// It looks every 100 ms at the cached state rather than on change,
    /// because SWRMeter is only broadcast on change: a steady bad SWR is one
    /// change and then silence, and the monitor needs to see it held.
    /// </summary>
    public sealed class TxAlertService : BackgroundService
    {
        private const int TickMs = 100;
        private const int ReloadMs = 5000;

        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly CalibrationStorage _calibration;
        private readonly ILogger<TxAlertService> _logger;
        private readonly TxAlertMonitor _monitor = new();

        private (double Raw, double Value)[] _swrTable = DefaultSwrTable;
        private long _loadedAt = long.MinValue;
        private TxAlertSnapshot _logged = new(false, null, null);

        /// <summary>A new value each time the app starts, so a page's dismissals from before a restart don't hide new alerts.</summary>
        public string Run { get; } = Guid.NewGuid().ToString("N")[..8];

        public TxAlertService(RadioStateService state, ISettingsService settings, CalibrationStorage calibration, ILogger<TxAlertService> logger)
        {
            _state = state;
            _settings = settings;
            _calibration = calibration;
            _logger = logger;
        }

        public TxAlertSnapshot Snapshot => _monitor.Snapshot;

        // The shipped FTdx101 table, for when the calibration file can't be read.
        internal static readonly (double Raw, double Value)[] DefaultSwrTable =
            { (0, 1.0), (51, 1.5), (77, 2.0), (128, 3.0), (173, 5.0), (242, 9.9) };

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try { Tick(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "[TxAlert] tick failed"); }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void Tick()
        {
            long now = Environment.TickCount64;
            if (now - _loadedAt >= ReloadMs)
            {
                _loadedAt = now;
                _swrTable = ReadSwrTable(() => _calibration.Load()) ?? DefaultSwrTable;
            }

            var s = _settings.GetCachedSettings();
            var limits = new TxAlertLimits(s.TxAlertEnabled, s.TxAlertSwrLimit, s.TxAlertAlcEnabled, s.TxAlertAlcPercent);
            bool tx = _state.IsTransmitting;
            int swrRaw = _state.SWRMeter ?? 0;
            int alcRaw = _state.ALCMeter ?? 0;
            string? mode = _state.TxVfo == 1 ? _state.ModeB : _state.ModeA;

            var reading = new TxAlertReading(tx, Interpolate(_swrTable, swrRaw), alcRaw / 255.0 * 100.0, mode);
            Log(_monitor.Update(reading, limits));
        }

        // One line when an alert starts and one when it ends, so a log shows them.
        private void Log(TxAlertSnapshot snap)
        {
            LogKind("SWR", _logged.Swr, snap.Swr, v => $"{v:0.0}:1");
            LogKind("ALC", _logged.Alc, snap.Alc, v => $"{v:0}%");
            _logged = snap;
        }

        private void LogKind(string name, TxAlertEvent? was, TxAlertEvent? now, Func<double, string> fmt)
        {
            if (now is { Active: true } && (was is null || was.Id != now.Id || !was.Active))
                _logger.LogWarning("[TxAlert] High {Name} while transmitting in {Mode}: {Value} (limit {Limit})",
                    name, now.Mode, fmt(now.Peak), fmt(now.Limit));
            else if (was is { Active: true } && now is { Active: false } && now.Id == was.Id)
                _logger.LogWarning("[TxAlert] High {Name} ended, peak {Peak}", name, fmt(now.Peak));
        }

        /// <summary>The SWR rows of the active calibration file, or null if it has none worth using.</summary>
        internal static (double Raw, double Value)[]? ReadSwrTable(Func<CalibrationFile> load)
        {
            try
            {
                var meter = load().Meters.FirstOrDefault(m => string.Equals(m.Name, "SWR", StringComparison.OrdinalIgnoreCase));
                if (meter is null) return null;
                var rows = meter.Points
                    .Select(p => (p.Raw, Value: p.TryGetRadioNumeric()))
                    .Where(p => p.Value is double v && double.IsFinite(v) && double.IsFinite(p.Raw))
                    .Select(p => (p.Raw, Value: p.Value!.Value))
                    .OrderBy(p => p.Raw)
                    .ToArray();
                return rows.Length >= 2 ? rows : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Straight lines between the rows, held at the ends: calibration-engine.js's interpolate.</summary>
        internal static double Interpolate((double Raw, double Value)[] table, double raw)
        {
            if (raw <= table[0].Raw) return table[0].Value;
            if (raw >= table[^1].Raw) return table[^1].Value;
            for (int i = 1; i < table.Length; i++)
            {
                if (raw <= table[i].Raw)
                {
                    var (r0, v0) = table[i - 1];
                    var (r1, v1) = table[i];
                    return r1 == r0 ? v1 : v0 + (raw - r0) / (r1 - r0) * (v1 - v0);
                }
            }
            return table[^1].Value;
        }
    }
}
