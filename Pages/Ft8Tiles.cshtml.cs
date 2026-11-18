using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Yaesu_Web_Control.Pages
{
    // FT8 Tiles (issue #332): a page of large tiles for an operator who uses a
    // switch, an eye-gaze tablet or Windows Voice Access instead of a mouse and
    // keyboard. Every tile is a single click. The stations come from WSJT-X or
    // WSJT-Z decodes (GET /api/wsjtx/calls, polled once a second), and a click
    // asks WSJT-Z to answer that station, as a double-click in WSJT-Z would.
    // Everything is done in the browser; the page model has nothing to load.
    public class Ft8TilesModel : PageModel
    {
        public void OnGet() { }
    }
}
