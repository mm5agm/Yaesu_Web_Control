using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    public class ApplicationSetupModel : PageModel
    {
        private readonly ISettingsService _settingsService;

        public ApplicationSetupModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        // App 1 (default: WSJT-X)
        [BindProperty]
        public bool ShowApp1Button { get; set; }

        [BindProperty]
        public string App1Name { get; set; } = "WSJT-X";

        [BindProperty]
        public string App1CommandLine { get; set; } = string.Empty;

        // App 2 (default: JTAlert)
        [BindProperty]
        public bool ShowApp2Button { get; set; }

        [BindProperty]
        public string App2Name { get; set; } = "JTAlert";

        [BindProperty]
        public string App2CommandLine { get; set; } = string.Empty;

        // App 3 (default: Log4OM)
        [BindProperty]
        public bool ShowApp3Button { get; set; }

        [BindProperty]
        public string App3Name { get; set; } = "Log4OM";

        [BindProperty]
        public string App3CommandLine { get; set; } = string.Empty;

        // App 4 (default: GridTracker)
        [BindProperty]
        public bool ShowApp4Button { get; set; }

        [BindProperty]
        public string App4Name { get; set; } = "GridTracker";

        [BindProperty]
        public string App4CommandLine { get; set; } = string.Empty;

        // App 5 (default: Fldigi)
        [BindProperty]
        public bool ShowApp5Button { get; set; }

        [BindProperty]
        public string App5Name { get; set; } = "Fldigi";

        [BindProperty]
        public string App5CommandLine { get; set; } = string.Empty;

        // WSJT-X UDP Settings
        [BindProperty]
        public string WsjtxUdpAddress { get; set; } = "127.0.0.1";

        [BindProperty]
        public int WsjtxUdpPort { get; set; } = 2237;

        [BindProperty]
        public bool WsjtxIntegrationEnabled { get; set; } = true;

        public async Task OnGetAsync()
        {
            var settings = await _settingsService.GetSettingsAsync();

            // App 1
            ShowApp1Button = settings.ShowWsjtxButton;
            App1Name = settings.App1Name;
            App1CommandLine = settings.WsjtxCommandLine;

            // App 2
            ShowApp2Button = settings.ShowJtalertButton;
            App2Name = settings.App2Name;
            App2CommandLine = settings.JtalertCommandLine;

            // App 3
            ShowApp3Button = settings.ShowLog4omButton;
            App3Name = settings.App3Name;
            App3CommandLine = settings.Log4omCommandLine;

            // App 4
            ShowApp4Button = settings.ShowGridtrackerButton;
            App4Name = settings.App4Name;
            App4CommandLine = settings.GridtrackerCommandLine;

            // App 5
            ShowApp5Button = settings.ShowFldigiButton;
            App5Name = settings.App5Name;
            App5CommandLine = settings.FldigiCommandLine;

            // UDP Settings
            WsjtxUdpAddress = settings.WsjtxUdpAddress;
            WsjtxUdpPort = settings.WsjtxUdpPort;
            WsjtxIntegrationEnabled = settings.WsjtxIntegrationEnabled;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var settings = await _settingsService.GetSettingsAsync();

            // App 1
            settings.ShowWsjtxButton = ShowApp1Button;
            settings.App1Name = App1Name;
            settings.WsjtxCommandLine = App1CommandLine;

            // App 2
            settings.ShowJtalertButton = ShowApp2Button;
            settings.App2Name = App2Name;
            settings.JtalertCommandLine = App2CommandLine;

            // App 3
            settings.ShowLog4omButton = ShowApp3Button;
            settings.App3Name = App3Name;
            settings.Log4omCommandLine = App3CommandLine;

            // App 4
            settings.ShowGridtrackerButton = ShowApp4Button;
            settings.App4Name = App4Name;
            settings.GridtrackerCommandLine = App4CommandLine;

            // App 5
            settings.ShowFldigiButton = ShowApp5Button;
            settings.App5Name = App5Name;
            settings.FldigiCommandLine = App5CommandLine;

            // UDP Settings
            settings.WsjtxUdpAddress = WsjtxUdpAddress;
            settings.WsjtxUdpPort = WsjtxUdpPort;
            settings.WsjtxIntegrationEnabled = WsjtxIntegrationEnabled;

            await _settingsService.SaveSettingsAsync(settings);

            TempData["Message"] = "Application settings saved successfully.";
            return RedirectToPage();
        }
    }
}
