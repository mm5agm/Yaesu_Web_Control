using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;

namespace Yaesu_Web_Control.Services
{
    public class RadioStatePersistenceService : BackgroundService
    {
        private readonly string _filePath;
        private readonly ILogger<RadioStatePersistenceService> _logger;
        private static readonly object _fileLock = new();

        private RadioState? _pendingState;
        private volatile bool _dirty;

        public RadioStatePersistenceService(
            ILogger<RadioStatePersistenceService> logger,
            IWebHostEnvironment env)
        {
            _logger = logger;

            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var stateDir = Path.Combine(appDataPath, "MM5AGM", "Yaesu Web Control");
            Directory.CreateDirectory(stateDir);
            _filePath = Path.Combine(stateDir, "radio_state.json");
        }

        public RadioState Load()
        {
            try
            {
                lock (_fileLock)
                {
                    if (!File.Exists(_filePath))
                    {
                        _logger.LogInformation("Radio state file not found. Creating default state.");
                        var defaultState = CreateDefaultState();
                        FlushState(defaultState);
                        return defaultState;
                    }

                    var json = File.ReadAllText(_filePath);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = null // Use PascalCase (default)
                    };
                    var state = JsonSerializer.Deserialize<RadioState>(json, options) ?? CreateDefaultState();
                    _logger.LogInformation("Radio state loaded from {FilePath}", _filePath);
                    _logger.LogInformation("Loaded state: ModeA={ModeA}, ModeB={ModeB}, Power={Power}, AntennaA={AntennaA}, AntennaB={AntennaB}, MicGain={MicGain}",
                        state.ModeA, state.ModeB, state.Power, state.AntennaA, state.AntennaB, state.MicGain);
                    return state;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading radio state. Using defaults.");
                return CreateDefaultState();
            }
        }

        public void MarkDirty(RadioState state)
        {
            lock (_fileLock)
            {
                _pendingState = state;
                _dirty = true;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (_dirty)
                    await FlushAsync();
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await FlushAsync();
            await base.StopAsync(cancellationToken);
        }

        private Task FlushAsync()
        {
            RadioState? state;
            lock (_fileLock)
            {
                if (!_dirty || _pendingState == null) return Task.CompletedTask;
                state = _pendingState;
                _dirty = false;
            }
            FlushState(state);
            return Task.CompletedTask;
        }

        private void FlushState(RadioState state)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = null, // Use PascalCase (default)
                    WriteIndented = true
                };
                var json = JsonSerializer.Serialize(state, options);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
                _logger.LogDebug("Radio state saved to {FilePath}: MicGain={MicGain}, Power={Power}",
                    _filePath, state.MicGain, state.Power);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving radio state to {FilePath}", _filePath);
            }
        }

        private RadioState CreateDefaultState()
        {
            return new RadioState
            {
                FrequencyA = 14074000, // 14.074 MHz (FT8)
                BandA = "20m",
                ModeA = "USB",
                AntennaA = "1",
                FrequencyB = 14074000,
                BandB = "20m",
                ModeB = "USB",
                AntennaB = "1"
            };
        }
    }
}
