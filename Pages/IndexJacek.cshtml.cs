using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Code-behind for the Jacek SP3L alternative GUI layout (#48). Inherits
    /// IndexModel so the entire state-loading pipeline (app button names,
    /// MicGain, Proc settings, CW memories, FM repeater, CTCSS, band plan,
    /// roofing filters, ATU state, voice etc.) is identical -- only the
    /// .cshtml view differs. The setting that routes /  here is
    /// ApplicationSettings.LayoutTemplate = "Jacek" (see IndexModel.OnGetAsync
    /// for the redirect, and Pages/Settings.cshtml for the user-facing picker).
    ///
    /// Reverse-redirect: if the user lands on /IndexJacek but their setting
    /// says "Default", bounce them back to /Index. Stops the layouts ever
    /// being out of sync with the setting (e.g. shared URL pasted from a
    /// user on the other layout, or a leftover bookmark).
    /// </summary>
    public class IndexJacekModel : IndexModel
    {
        private readonly ISettingsService _settings;

        public IndexJacekModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
            _settings = settingsService;
        }

        // `override` not `new` -- using `new` makes Razor Pages' handler
        // selector see two OnGetAsync methods on this class and throw
        // "Multiple handlers matched" at request time. `override` replaces
        // the inherited method so there's only one handler.
        public override async Task<IActionResult> OnGetAsync()
        {
            var settings = await _settings.GetSettingsAsync();
            if (!string.Equals(settings.LayoutTemplate, "Jacek", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToPage("/Index");
            }
            // base.OnGetAsync does the full state-load. It also has a
            // forward-redirect to /IndexJacek when LayoutTemplate=Jacek, but
            // the GetType() check there suppresses it for this subclass so
            // we don't bounce in a redirect loop.
            return await base.OnGetAsync();
        }
    }
}
