using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Compact pop-out host for the Remote Audio WebSocket session so Index
    /// navigation (e.g. Settings) does not tear down RX/TX audio.
    /// </summary>
    public class RemoteAudioModel : PageModel
    {
        private readonly ISettingsService _settingsService;

        public RemoteAudioModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        /// <summary>Same TX shortcut as Home (<c>window.ywcTxToggleKey</c>). Empty = disabled.</summary>
        public string TxToggleKey { get; set; } = string.Empty;

        public async Task OnGetAsync()
        {
            var settings = await _settingsService.GetSettingsAsync();
            // Match Index: HTML cannot round-trip a lone space — Settings stores Space as "Space".
            TxToggleKey = settings.TxToggleKey == " " ? "Space" : (settings.TxToggleKey ?? string.Empty);
        }
    }
}
