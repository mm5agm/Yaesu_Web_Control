using System.ComponentModel;

namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>
/// Feeds <see cref="RadioMenuSessionChanges"/> from the live radio state, so
/// closing YWC (the Close YWC page or the tray's Exit) can ask to save the
/// radio menu when a front-panel level changed while YWC ran. Only listens:
/// it sends nothing to the radio.
/// </summary>
public sealed class RadioMenuSessionWatch : IHostedService
{
    private readonly RadioStateService _radio;
    private readonly ICatClient _cat;
    private readonly ISettingsService _settings;
    private bool _wasConnected;

    public RadioMenuSessionChanges Changes { get; } = new();

    public RadioMenuSessionWatch(RadioStateService radio, ICatClient cat, ISettingsService settings)
    {
        _radio = radio;
        _cat = cat;
        _settings = settings;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Changes.Settle(DateTime.Now + RadioMenuSessionChanges.ConnectSettle);
        Changes.OnMode(ModeCode(_settings.GetCachedSettings().RadioModel), DateTime.Now);
        _radio.PropertyChanged += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _radio.PropertyChanged -= OnChanged;
        return Task.CompletedTask;
    }

    /// <summary>The main band's mode now, as its MD0 code, or null if it is not one the file has.</summary>
    private string? ModeCode(string? model) => RadioKnobs.ModesFor(model)
        .FirstOrDefault(m => CatCommands.ModeFromCode(m.Code[0], model) == _radio.ModeA)?.Code;

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            var model = _settings.GetCachedSettings().RadioModel;
            if (RadioKnobs.TableFor(model) is null) return;
            var now = DateTime.Now;
            bool connected = _cat.IsConnected;
            if (connected && !_wasConnected)
                Changes.Settle(now + RadioMenuSessionChanges.ConnectSettle);
            _wasConnected = connected;

            switch (e.PropertyName)
            {
                case nameof(RadioStateService.ModeA):
                    Changes.OnMode(ModeCode(model), now);
                    break;
                case nameof(RadioStateService.MicGain): Changes.OnLevel("MG", _radio.MicGain, now); break;
                case nameof(RadioStateService.AmcLevel):
                    if (_radio.AmcLevel is int amc) Changes.OnLevel("AO", amc, now);
                    break;
                case nameof(RadioStateService.ProcEnabled): Changes.OnLevel("PR0", _radio.ProcEnabled ? 1 : 0, now); break;
                case nameof(RadioStateService.ProcLevel): Changes.OnLevel("PL", _radio.ProcLevel, now); break;
                case nameof(RadioStateService.Power): Changes.OnLevel("PC", _radio.Power, now); break;
                case nameof(RadioStateService.VoxGain): Changes.OnLevel("VG", _radio.VoxGain, now); break;
            }
        }
        catch
        {
            // Never let the reminder disturb the radio state updates.
        }
    }
}
