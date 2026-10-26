using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// One part of the RTTY Contest view in a window of its own, for another
    /// monitor: /rtty-contest/window?p=spectrum|waterfall|both|tuner. Opened
    /// by the POP OUT keys on /rtty-contest; DOCK puts the part back there.
    /// </summary>
    public class RttyContestWindowModel : RttyContestModel
    {
        private static readonly string[] Parts = { "spectrum", "waterfall", "both", "tuner" };

        [BindProperty(SupportsGet = true, Name = "p")]
        public string? Part { get; set; }

        public string PartName => Parts.Contains(Part) ? Part! : "both";
        public bool IsTuner => PartName == "tuner";

        public RttyContestWindowModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
