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

        public RadioPanelModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
