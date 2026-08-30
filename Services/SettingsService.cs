using System.Text.Json;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services
{
    public class SettingsService : ISettingsService, IDisposable
    {
        private readonly string _settingsFilePath;
        private readonly ILogger<SettingsService> _logger;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private volatile ApplicationSettings? _cachedSettings;
        private readonly FileSystemWatcher _watcher;

        public SettingsService(IWebHostEnvironment environment, ILogger<SettingsService> logger)
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MM5AGM", "Yaesu Web Control");
            MigrateAppDataIfNeeded(appData);
            Directory.CreateDirectory(appData);
            _settingsFilePath = Path.Combine(appData, "appsettings.user.json");
            _logger = logger;
            _logger.LogInformation("SettingsService initialized. File path: {Path}", _settingsFilePath);

            var watcher = new FileSystemWatcher(Path.GetDirectoryName(_settingsFilePath)!, Path.GetFileName(_settingsFilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, _) => InvalidateCache();
            watcher.Created += (_, _) => InvalidateCache();
            watcher.Renamed += (_, _) => InvalidateCache();
            _watcher = watcher;
        }

        public void Dispose() => _watcher.Dispose();

        public ApplicationSettings GetCachedSettings() =>
            _cachedSettings ?? new ApplicationSettings();

        public async Task<ApplicationSettings> GetSettingsAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_cachedSettings != null) return _cachedSettings;

                // These fire on every meter-poll cycle (GetSettingsAsync is
                // called ~2 Hz). At Information level — and especially dumping
                // the entire settings JSON — they were a major contributor to
                // the synchronous-logging flood that starved the thread pool
                // during startup (issue #73). Kept at Debug so they're still
                // available when explicitly troubleshooting settings.
                _logger.LogDebug("GetSettingsAsync called. File exists: {Exists}", File.Exists(_settingsFilePath));

                if (File.Exists(_settingsFilePath))
                {
                    var json = await File.ReadAllTextAsync(_settingsFilePath);
                    _logger.LogDebug("Raw JSON read: {Json}", json);

                    _cachedSettings = JsonSerializer.Deserialize<ApplicationSettings>(json) ?? new ApplicationSettings();

                    _logger.LogDebug("Settings deserialized: SerialPort={SerialPort}, BaudRate={BaudRate}, WebAddress={WebAddress}, HttpPort={HttpPort}",
                        _cachedSettings.SerialPort, _cachedSettings.BaudRate, _cachedSettings.WebAddress, _cachedSettings.HttpPort);

                    MigrateSdrDeviceKey(_cachedSettings);
                    MigrateSdrSampleRate(_cachedSettings);
                    AutoQuoteCommandLinePaths(_cachedSettings);
                }
                else
                {
                    _cachedSettings = new ApplicationSettings();
                    ApplyContainerDefaults(_cachedSettings);
                    MigrateSdrSampleRate(_cachedSettings);   // fills A/B from defaults when file is brand new
                    _logger.LogWarning("Settings file does not exist at {Path}. Using defaults: SerialPort={SerialPort}, WebAddress={WebAddress}, HttpPort={HttpPort}",
                        _settingsFilePath, _cachedSettings.SerialPort, _cachedSettings.WebAddress, _cachedSettings.HttpPort);
                }

                return _cachedSettings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading settings from {Path}", _settingsFilePath);
                return new ApplicationSettings();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task SaveSettingsAsync(ApplicationSettings settings)
        {
            await _semaphore.WaitAsync();
            try
            {
                _logger.LogInformation("SaveSettingsAsync called with: SerialPort={SerialPort}, BaudRate={BaudRate}, WebAddress={WebAddress}, HttpPort={HttpPort}",
                    settings.SerialPort, settings.BaudRate, settings.WebAddress, settings.HttpPort);

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                var json = JsonSerializer.Serialize(settings, options);
                // Debug: this is the entire settings file, including the user's
                // callsign and local paths. It belongs in a log the user opted
                // into for a bug report, not in every log by default.
                _logger.LogDebug("Serialized to JSON: {Json}", json);

                await File.WriteAllTextAsync(_settingsFilePath, json);
                _cachedSettings = settings;

                _logger.LogInformation("Settings saved successfully to {Path}", _settingsFilePath);

                // Verify
                if (File.Exists(_settingsFilePath))
                {
                    var verify = await File.ReadAllTextAsync(_settingsFilePath);
                    _logger.LogDebug("Verification: File content after save: {Content}", verify);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving settings to {Path}", _settingsFilePath);
                throw;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public string GetSettingsFilePath() => _settingsFilePath;

        public void InvalidateCache()
        {
            _semaphore.Wait();
            try { _cachedSettings = null; }
            finally { _semaphore.Release(); }
        }

        // v2.2.x → v2.3.0 migration: the SDR settings split from a single
        // SdrDeviceKey into per-VFO SdrDeviceKeyA / SdrDeviceKeyB. On read,
        // if the legacy field has a value and SdrDeviceKeyA does not, promote
        // the legacy value into A. The legacy field is then cleared on the
        // next save so the file gradually converges on the new shape.
        // See docs/decisions/0001-dual-sdr-architecture.md.
        private static void MigrateSdrDeviceKey(ApplicationSettings s)
        {
            if (!string.IsNullOrWhiteSpace(s.SdrDeviceKey) &&
                string.IsNullOrWhiteSpace(s.SdrDeviceKeyA))
            {
                s.SdrDeviceKeyA = s.SdrDeviceKey;
                s.SdrDeviceKey  = string.Empty;
            }
        }

        // v2.3.0+ per-VFO sample rate. The model property defaults are 0
        // (sentinel for "field not in JSON"), so we can distinguish between
        // an absent field on disk vs an explicit 0 saved by the user.
        // Rules:
        //   - If legacy SdrSampleRateHz has a value and either A or B is 0,
        //     copy legacy → the missing slot(s). Clear legacy.
        //   - If A or B is still 0 after that, fall back to the current
        //     default so a brand-new settings file or one missing all three
        //     fields still gets sane defaults.
        //   - Map any rate retired by the low-IF change onto its nearest
        //     survivor (see below).
        private const double DefaultSampleRateHz = 2_000_000;

        // Spans retired when the spectrum moved to low-IF. Only a handful of
        // sample rates satisfy the SDRplay API's low-IF conditions, so the
        // span list shrank to the six rates those conditions allow. A settings
        // file written before that still names one of the old rates; left
        // alone it would be rejected by /api/sdr/span and leave the main page
        // with no span button lit, which reads as a broken UI rather than as
        // an out-of-date setting.
        private static readonly Dictionary<double, double> RetiredSampleRates = new()
        {
            [1_024_000] = 1_000_000,   // same span, now reached as 8 MHz ÷ 8
            [2_048_000] = 2_000_000,   // same span, now reached as 8 MHz ÷ 4
            [2_500_000] = 2_000_000,   // no low-IF combination reaches these,
            [3_200_000] = 2_000_000,   // so they fall back to the widest span
        };

        private static void MigrateSdrSampleRate(ApplicationSettings s)
        {
            if (s.SdrSampleRateHz > 0)
            {
                if (s.SdrSampleRateHzA == 0) s.SdrSampleRateHzA = s.SdrSampleRateHz;
                if (s.SdrSampleRateHzB == 0) s.SdrSampleRateHzB = s.SdrSampleRateHz;
                s.SdrSampleRateHz = 0;
            }
            if (s.SdrSampleRateHzA == 0) s.SdrSampleRateHzA = DefaultSampleRateHz;
            if (s.SdrSampleRateHzB == 0) s.SdrSampleRateHzB = DefaultSampleRateHz;

            if (RetiredSampleRates.TryGetValue(s.SdrSampleRateHzA, out double newA))
                s.SdrSampleRateHzA = newA;
            if (RetiredSampleRates.TryGetValue(s.SdrSampleRateHzB, out double newB))
                s.SdrSampleRateHzB = newB;
        }

        // Backward-compat for users whose *CommandLine settings were saved before
        // the strict-quoting rule was introduced. Auto-quote any unquoted path
        // whose entire value is an existing file (no command-line arguments
        // included). Paths with arguments require the user to add quotes
        // themselves — there is no reliable heuristic that distinguishes
        // "path-with-spaces" from "path args-with-spaces".
        private static void AutoQuoteCommandLinePaths(ApplicationSettings s)
        {
            s.WsjtxCommandLine       = AutoQuote(s.WsjtxCommandLine);
            s.JtalertCommandLine     = AutoQuote(s.JtalertCommandLine);
            s.Log4omCommandLine      = AutoQuote(s.Log4omCommandLine);
            s.GridtrackerCommandLine = AutoQuote(s.GridtrackerCommandLine);
            s.FldigiCommandLine      = AutoQuote(s.FldigiCommandLine);
        }

        private static string AutoQuote(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0) return trimmed;
            if (trimmed.StartsWith('"')) return trimmed;
            if (!trimmed.Contains(' ')) return trimmed;
            return System.IO.File.Exists(trimmed) ? $"\"{trimmed}\"" : trimmed;
        }

        private static void MigrateAppDataIfNeeded(string newFolder)
        {
            if (Directory.Exists(newFolder)) return;
            var oldFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MM5AGM", "FTdx101 WebApp");
            if (!Directory.Exists(oldFolder)) return;
            Directory.CreateDirectory(newFolder);
            foreach (var file in Directory.GetFiles(oldFolder))
                File.Copy(file, Path.Combine(newFolder, Path.GetFileName(file)), overwrite: false);
        }

        /// <summary>
        /// First-run defaults when hosted in Docker / a container: keep the
        /// process alive with no browser tabs, and prefer a common USB-serial
        /// path (override in Settings once the real device is known).
        /// </summary>
        private static void ApplyContainerDefaults(ApplicationSettings s)
        {
            if (!HostRuntime.IsContainer) return;
            s.AutoShutdownWhenNoBrowsers = false;
            if (string.Equals(s.SerialPort, "COM3", StringComparison.OrdinalIgnoreCase))
                s.SerialPort = "/dev/ttyUSB0";
        }
    }
}