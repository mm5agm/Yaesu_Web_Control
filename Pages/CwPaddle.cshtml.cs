using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Yaesu_Web_Control.Pages
{
    // Paddle keying test page (Discussion #262): a MIDI paddle read in the
    // browser, an iambic keyer and sidetone. Dry run - nothing here talks to
    // the radio, so the page needs nothing from the server. Not in the menus
    // while it is being built.
    public class CwPaddleModel : PageModel
    {
        public void OnGet()
        {
        }
    }
}
