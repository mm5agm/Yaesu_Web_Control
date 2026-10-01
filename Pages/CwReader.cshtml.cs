using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    // The CW reader in a window of its own, so it can go on a second monitor.
    // The decoder is server state, so this page and the main page's dialog
    // read the same copy; all this needs from the server is the mode MAIN is
    // in when the window opens. Changes after that arrive over SignalR.
    public class CwReaderModel : PageModel
    {
        private readonly RadioStateService _radioState;

        public CwReaderModel(RadioStateService radioState)
        {
            _radioState = radioState;
        }

        public string ModeA => _radioState.ModeA ?? "";

        public void OnGet()
        {
        }
    }
}
