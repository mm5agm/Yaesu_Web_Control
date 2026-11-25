using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    // Exit page: close YWC from any browser, with big buttons, so another
    // program (HRD, for Mike in issue #332) can open the radio's COM port.
    // The tray's Exit does the same on the PC itself.
    public class ExitModel : PageModel
    {
        private readonly ISettingsService _settings;

        public ExitModel(ISettingsService settings) => _settings = settings;

        public bool PowerOffAtExit { get; private set; }
        public bool InContainer { get; private set; }

        public void OnGet()
        {
            PowerOffAtExit = _settings.GetCachedSettings().PowerOffAtExit;
            InContainer = HostRuntime.IsContainer;
        }
    }
}
