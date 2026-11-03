using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    // The RTTY tuner in a window of its own, so it can go on a second monitor.
    // The filters are server state, so this page and the main page's dialog
    // draw the same figure; all this needs from the server is the mode MAIN is
    // in when the window opens. Changes after that arrive over SignalR.
    //
    // The RTTY Contest view (/rtty-contest) pops its tuner out to this same
    // page with ?contest=true: the Tune section only, whatever Settings says,
    // under a window name of its own, and DOCK goes back to /rtty-contest.
    // With &decode=true as well it is the view's decoded-text window instead:
    // the Decode section only, and no tuner running.
    public class RttyTunerModel : PageModel
    {
        private readonly RadioStateService _radioState;

        public RttyTunerModel(RadioStateService radioState)
        {
            _radioState = radioState;
        }

        public string ModeA => _radioState.ModeA ?? "";

        [BindProperty(SupportsGet = true, Name = "contest")]
        public bool Contest { get; set; }

        [BindProperty(SupportsGet = true, Name = "decode")]
        public bool Decode { get; set; }

        /// <summary>The RTTY Contest view's decoded-text window.</summary>
        public bool DecodeOnly => Contest && Decode;

        public void OnGet()
        {
        }
    }
}
