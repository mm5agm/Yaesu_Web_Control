using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    public class RadioDisplayModel : PageModel
    {
        private readonly ISettingsService _settingsService;

        public RadioDisplayModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        public string RadioModel { get; set; } = "FTdx101MP";

        public async Task OnGetAsync()
        {
            var settings = await _settingsService.GetSettingsAsync();
            RadioModel = settings.RadioModel ?? RadioModel;
        }
    }
}
