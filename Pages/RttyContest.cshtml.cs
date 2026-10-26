using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// The RTTY Contest view at /rtty-contest - a fourth view beside Classic,
    /// Experimental and Radio, with only what a contest RTTY operator uses:
    /// the spectrum over the waterfall, the RTTY tuner and a small strip.
    /// Drawn by core/js/rtty-contest/contest-view.js and fed by
    /// wwwroot/js/ui/rtty-contest-yaesu.js. Plan:
    /// docs/design/rtty-contest-gui-plan.md.
    /// </summary>
    public class RttyContestModel : IndexModel
    {
        public bool IsDual => RadioCapabilities.IsDualReceiver(RadioModel);
        public int MaxPower => RadioCapabilities.MaxPowerWatts(RadioModel);
        public long MinHz => RadioCapabilities.FrequencyRangeHz(RadioModel).MinHz;
        public long MaxHz => RadioCapabilities.FrequencyRangeHz(RadioModel).MaxHz;

        public RttyContestModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
