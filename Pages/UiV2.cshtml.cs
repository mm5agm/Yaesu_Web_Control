using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;
using System.Threading.Tasks;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// UI v2 is an alternative layout route. Phase A is a visual PoC; Phase B
    /// wires the same stable element IDs to the existing CAT/SignalR stack.
    /// </summary>
    public class UiV2Model : IndexModel
    {
        public UiV2Model(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }

        // Keep the classic state load so the PoC can render with realistic
        // values, even though Phase A controls are disabled.
        public override async Task<IActionResult> OnGetAsync()
        {
            return await base.OnGetAsync();
        }
    }
}

