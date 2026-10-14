using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Radio-style front panel at /radio - a third view beside Classic and
    /// Experimental. Same server state as Index; the page itself is drawn by
    /// the shared core/js/radio-panel module and fed by
    /// wwwroot/js/ui/radio-panel-yaesu.js.
    /// </summary>
    public class RadioPanelModel : IndexModel
    {
        public bool IsDual => RadioCapabilities.IsDualReceiver(RadioModel);
        public int MaxPower => RadioCapabilities.MaxPowerWatts(RadioModel);
        // Click-to-tune on the Radio screen needs the radio's scope span (SS).
        public bool HasScopeCat => RadioCapabilities.SupportsSpectrumScopeCat(RadioModel);
        public long MaxHz => RadioCapabilities.FrequencyRangeHz(RadioModel).MaxHz;
        // The same gates as Index's VDD and temperature gauges.
        public bool HasVdd => RadioModel is not ("FTdx10" or "FT-710" or "FTDX5000MP" or "FTDX5000D");
        public bool HasPaTemp => HasVdd && RadioCapabilities.HasPaTemperatureMeter(RadioModel);
        public (int Min, int Max) VddDial => RadioCapabilities.VddGaugeRange(RadioModel);

        public RadioPanelModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
