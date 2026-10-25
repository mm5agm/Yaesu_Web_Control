using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// One of the radio panel's windows (Meters, RX Setup, TX Setup, Scope,
    /// Memories) in a browser window of its own, for a second screen:
    /// /RadioWindow?g=meters. Drawn by core/js/radio-panel/window-popout.js and
    /// filled by wwwroot/js/ui/radio-window-yaesu.js. Opened from the
    /// ⇱ WINDOW key in the window's title bar on /radio.
    /// </summary>
    public class RadioWindowModel : IndexModel
    {
        // A pop-out, not a view: leaves the remembered view alone.
        protected override string? ViewKey => null;

        public bool IsDual => RadioCapabilities.IsDualReceiver(RadioModel);
        public int MaxPower => RadioCapabilities.MaxPowerWatts(RadioModel);
        public long MaxHz => RadioCapabilities.FrequencyRangeHz(RadioModel).MaxHz;

        public RadioWindowModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
