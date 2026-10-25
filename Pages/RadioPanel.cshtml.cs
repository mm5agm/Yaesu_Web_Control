using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Radio-style front panel at /radio - a third view beside Classic and
    /// Experimental. Same server state as Index; the page itself is drawn by
    /// the shared core/js/radio-panel module and fed by
    /// wwwroot/js/ui/radio-panel-yaesu.js.
    /// </summary>
    public class RadioPanelModel : IndexModel
    {
        protected override string? ViewKey => Models.AppViews.Radio;

        public bool IsDual => RadioCapabilities.IsDualReceiver(RadioModel);
        public int MaxPower => RadioCapabilities.MaxPowerWatts(RadioModel);
        // Click-to-tune on the Radio screen needs the radio's scope span (SS).
        public bool HasScopeCat => RadioCapabilities.SupportsSpectrumScopeCat(RadioModel);
        public long MaxHz => RadioCapabilities.FrequencyRangeHz(RadioModel).MaxHz;
        // The same gates as Index's VDD and temperature gauges.
        public bool HasVdd => RadioModel is not ("FTdx10" or "FT-710" or "FTDX5000MP" or "FTDX5000D");
        public bool HasPaTemp => HasVdd && RadioCapabilities.HasPaTemperatureMeter(RadioModel);
        public (int Min, int Max) VddDial => RadioCapabilities.VddGaugeRange(RadioModel);
        public bool HasAntennaSelector => RadioCapabilities.HasAntennaSelector(RadioModel);

        // The roofing filters the VFO boxes offer: Index's lists, the same
        // fitted-filter rule. Empty where the radio has no CAT roofing control.
        public IReadOnlyList<RoofingChoice> RoofingChoices
        {
            get
            {
                if (!RadioCapabilities.HasRoofingFilterCat(RadioModel)) return Array.Empty<RoofingChoice>();
                var fitted = InstalledRoofingFilters;
                (string Code, string Label)[] all;
                Func<string, bool> standard;
                switch (RadioModel)
                {
                    case "FTdx10":
                        all = new[] { ("6", "12 kHz"), ("7", "3 kHz"), ("9", "500 Hz"), ("A", "300 Hz") };
                        standard = c => c is "6" or "7" or "9";
                        break;
                    case "FTDX3000":
                        all = new[] { ("0", "Auto"), ("1", "15 kHz"), ("2", "6 kHz"), ("3", "3 kHz"), ("4", "600 Hz"), ("5", "300 Hz") };
                        standard = c => c is "0" or "1" or "2" or "3";
                        break;
                    default:
                        all = new[] { ("A", "300 Hz"), ("9", "600 Hz"), ("8", "1.2 kHz"), ("7", "3 kHz"), ("6", "12 kHz") };
                        standard = _ => false;
                        break;
                }
                return all.Where(o => standard(o.Code) || fitted.Contains(o.Code))
                          .Select(o => new RoofingChoice(o.Code, o.Label)).ToList();
            }
        }

        public record RoofingChoice(string Code, string Label);

        public RadioPanelModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
