using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages.MeterCalibration
{
    public class IndexModel : PageModel
    {
        private readonly ISettingsService _settings;
        private readonly ICalibrationService _calibration;

        // Exposed so the "Email calibration to developer" button can put the
        // radio model in the email subject line.
        public string RadioModel { get; private set; } = "";

        // The preview gauges draw the same dials as the main page: the rated
        // output for Power, and the per-model VDD dial (#155) so a 13.8 V
        // radio's VDD preview is not a 40–55 V face that never moves.
        public int    MaxPowerWatts { get; private set; } = 200;
        public int    VddMin        { get; private set; } = 40;
        public int    VddMax        { get; private set; } = 55;
        public string PaSupplyVolts { get; private set; } = "50.0";

        // Gates the developer-only "import emailed calibration into the shipped
        // default" button — true only in the dev build (never for installed users).
        public bool IsDevelopmentMode => _calibration.IsDevelopmentMode;

        public IndexModel(ISettingsService settings, ICalibrationService calibration)
        {
            _settings = settings;
            _calibration = calibration;
        }

        public async Task OnGet()
        {
            var settings = await _settings.GetSettingsAsync();
            RadioModel = settings.RadioModel ?? "";
            MaxPowerWatts = RadioCapabilities.MaxPowerWatts(RadioModel);
            (VddMin, VddMax) = RadioCapabilities.VddGaugeRange(RadioModel);
            PaSupplyVolts = RadioCapabilities.PaSupplyVolts(RadioModel)
                .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
