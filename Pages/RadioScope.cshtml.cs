using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    // The radio's own scope controls (the SS command) on a page of their own,
    // so the Radio panel's SCOPE window can show them in a frame. Same partial
    // and the same RadioScopeControl as the main page and Radio Display.
    public class RadioScopeModel : PageModel
    {
        private readonly ISettingsService _settingsService;

        public RadioScopeModel(ISettingsService settingsService)
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
