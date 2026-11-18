using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Yaesu_Web_Control.Pages
{
    // The radio audio waterfall on a page of its own, for every radio. The
    // AUDIO group in the scope controls is only on the radios with a CAT scope
    // (FTdx101MP/D, FTdx10); this needs nothing but the radio's USB audio.
    // A click on a signal tunes it onto the target line. Everything is done in
    // the browser and AudioWaterfallController; the page model has nothing to load.
    public class AudioWaterfallModel : PageModel
    {
        public void OnGet() { }
    }
}
