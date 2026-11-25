using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Yaesu_Web_Control.Pages
{
    // Spot Map: who hears the operator, and who the operator hears, live from
    // PSK Reporter, on a map and in a list. Made for a second monitor. All the
    // work is in core/js/spotmap; the page reads the callsign and grid from
    // GET /api/wsjtx/station (WSJT-X/WS Status over UDP). Receive only: the
    // page never touches the radio.
    public class SpotMapModel : PageModel
    {
        public void OnGet()
        {
        }
    }
}
