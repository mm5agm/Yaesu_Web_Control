using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// The operator manual is a static NuStreamDocs book at /manual/ (generated
    /// at compile time from user-manual/). This page only exists so old
    /// bookmarks to /UserManual still land there.
    /// </summary>
    public class UserManualModel : PageModel
    {
        public IActionResult OnGet() => RedirectPermanent("/manual/");
    }
}
