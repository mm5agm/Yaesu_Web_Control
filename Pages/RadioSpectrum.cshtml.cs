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
        public bool IsDual => RadioCapabilities.IsDualReceiver(RadioModel);
        /// <summary>The radio's top frequency, so the dial stepper can tune above 75 MHz on the FT-991A.</summary>
        public long MaxHz => RadioCapabilities.FrequencyRangeHz(RadioModel).MaxHz;

        public RadioSpectrumModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
