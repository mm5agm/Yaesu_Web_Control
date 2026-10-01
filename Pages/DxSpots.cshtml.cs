using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    // The DX spots list in a window of its own, so it can go on a second
    // monitor. The page starts from the cluster's current list
    // (GET /api/dxcluster/spots) and keeps up over its own SignalR connection.
    // From the server it needs VFO A's frequency, for the band filter, and the
    // operator's choice of whether clicking a spot may change the mode.
    public class DxSpotsModel : PageModel
    {
        private readonly RadioStateService _radioState;
        private readonly ISettingsService _settings;

        public DxSpotsModel(RadioStateService radioState, ISettingsService settings)
        {
            _radioState = radioState;
            _settings = settings;
        }

        public long FrequencyA => _radioState.FrequencyA;

        public bool AutoModeChangeOnTune { get; private set; } = true;

        public async Task OnGetAsync()
        {
            var settings = await _settings.GetSettingsAsync();
            AutoModeChangeOnTune = settings.AutoModeChangeOnTune;
        }

        // GET /DxSpots?handler=AutoMode. The window can stay open on a second
        // monitor while the setting is changed in the main page, so it asks
        // again before each click-to-QSY rather than trusting the value it
        // opened with.
        public async Task<IActionResult> OnGetAutoModeAsync()
        {
            var settings = await _settings.GetSettingsAsync();
            return new JsonResult(new { autoModeChangeOnTune = settings.AutoModeChangeOnTune });
        }
    }
}
