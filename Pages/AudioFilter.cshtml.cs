using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Yaesu_Web_Control.Pages
{
    // One VFO's Audio Filter in a window of its own, so it can go on a second
    // monitor. The radio holds the four settings, so this page and the main
    // page's dialog read the same values over /api/cat/audiofilter/{vfo};
    // mode changes, which change which settings apply, arrive over SignalR.
    public class AudioFilterModel : PageModel
    {
        [BindProperty(SupportsGet = true, Name = "vfo")]
        public string? VfoQuery { get; set; }

        public string Vfo => string.Equals(VfoQuery, "B", System.StringComparison.OrdinalIgnoreCase) ? "B" : "A";

        public void OnGet()
        {
        }
    }
}
