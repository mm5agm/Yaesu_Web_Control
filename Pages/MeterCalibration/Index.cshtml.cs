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
        }
    }
}
