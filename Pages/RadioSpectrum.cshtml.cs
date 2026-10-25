using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// The radio panel's spectrum in a window of its own, for a second screen:
    /// one pane per SDR, drawn by core/js/radio-panel/spectrum-popout.js and
    /// fed by wwwroot/js/ui/radio-spectrum-yaesu.js. Opened from the ⧉ key on
    /// /radio's spectrum.
    /// </summary>
    public class RadioSpectrumModel : IndexModel
    {
        // A pop-out, not a view: leaves the remembered view alone.
        protected override string? ViewKey => null;

        public bool IsDual => RadioCapabilities.IsDualReceiver(RadioModel);

        public RadioSpectrumModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
