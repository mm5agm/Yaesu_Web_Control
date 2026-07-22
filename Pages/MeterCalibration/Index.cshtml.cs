using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages.MeterCalibration
{
    public class IndexModel : PageModel
    {
        private readonly ISettingsService _settings;

        // Exposed so the "Email calibration to developer" button can put the
        // radio model in the email subject line.
        public string RadioModel { get; private set; } = "";

        public IndexModel(ISettingsService settings) => _settings = settings;

        public async Task OnGet()
        {
            var settings = await _settings.GetSettingsAsync();
            RadioModel = settings.RadioModel ?? "";
        }
    }
}
