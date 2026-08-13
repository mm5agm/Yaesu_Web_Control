using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Per-VFO bindings for the shared Flex <c>_Vfo</c> partial (A or B).
    /// </summary>
    public sealed class FlexVfoPanelModel
    {
        public FlexModel Page { get; }
        public char Vfo { get; }

        private FlexVfoPanelModel(FlexModel page, char vfo)
        {
            Page = page;
            Vfo = char.ToUpperInvariant(vfo);
        }

        public static FlexVfoPanelModel A(FlexModel page) => new(page, 'A');
        public static FlexVfoPanelModel B(FlexModel page) => new(page, 'B');

        public bool IsA => Vfo == 'A';
        public bool IsB => Vfo == 'B';

        public string Id => Vfo.ToString();
        public string IdLower => char.ToLowerInvariant(Vfo).ToString();

        public string ColId => IsA ? "vfoACol" : "vfoBCol";
        public string CssModifier => IsA ? "ywc-vfo-a" : "ywc-vfo-b";
        public string StatusCss => IsA ? "ywc-vfo-status-a" : "ywc-vfo-status-b";
        public string StatusLineId => IsA ? "vfoAStatusLine" : "vfoBStatusLine";
        public string HeadingId => IsA ? "receiverAHeading" : "receiverBHeading";
        public string Label => IsA ? "VFO A" : "VFO B";

        public string FreqA11yKey => IsA ? "vfo.a.frequency" : "vfo.b.frequency";
        public string KeyboardA11yKey => IsA ? "keyboard.openA" : "keyboard.openB";
        public string FilterScopeA11yKey => IsA ? "filter.scopeA" : "filter.scopeB";
        public string FilterScopePanel => IsA ? "filterScopePanelA" : "filterScopePanelB";

        public FlexViewState View => Page.View;
        public RadioStateService Radio => Page.RadioState;

        public string SelectedBand => IsA ? Page.SelectedBandA : Page.SelectedBandB;
        public int AfGain => IsA ? Page.AfGainA : Page.AfGainB;
        public string IfWidthDefault => IsA ? View.IfWidthDefaultA : View.IfWidthDefaultB;

        public bool ShowVcTune => IsA ? View.HasVcTune : View.HasVcTuneSub;
        public bool VcTuneStartsHidden => IsB;
        public string VcTuneRole => IsA ? "MAIN" : "SUB";

        public bool ShowQmb => IsA && View.HasQmb;
        public bool ShowVoice => IsA || !View.IsSingleReceiver;

        public string Mode => IsA ? (Radio.ModeA ?? "") : (Radio.ModeB ?? "");
        public string Antenna => IsA ? (Radio.AntennaA ?? "") : (Radio.AntennaB ?? "");
        public string Agc => IsA ? Radio.AgcA : Radio.AgcB;
        public string Ipo => IsA ? Radio.IpoA : Radio.IpoB;
        public string Att => IsA ? Radio.AttA : Radio.AttB;
        public string Nr => IsA ? Radio.NrA : Radio.NrB;
        public int NrLevel => IsA ? Radio.NrLevelA : Radio.NrLevelB;
        public string Nb => IsA ? Radio.NbA : Radio.NbB;
        public int NbLevel => IsA ? Radio.NbLevelA : Radio.NbLevelB;
        public string AutoNotch => IsA ? Radio.AutoNotchA : Radio.AutoNotchB;
        public string ManualNotch => IsA ? Radio.ManualNotchA : Radio.ManualNotchB;
        public int ManualNotchFreq => IsA ? Radio.ManualNotchFreqA : Radio.ManualNotchFreqB;
        public int RfGain => IsA ? Radio.RfGainA : Radio.RfGainB;
        public int Squelch => IsA ? Radio.SquelchA : Radio.SquelchB;
        public bool ContourOn => IsA ? Radio.ContourOnA : Radio.ContourOnB;
        public int ContourFreq => IsA ? Radio.ContourFreqA : Radio.ContourFreqB;
        public bool ApfOn => IsA ? Radio.ApfOnA : Radio.ApfOnB;
        public int ApfFreq => IsA ? Radio.ApfFreqA : Radio.ApfFreqB;
        public string RoofingFilter => IsA ? Radio.RoofingFilterA : Radio.RoofingFilterB;
        public string IfWidth => IsA ? Radio.IfWidthA : Radio.IfWidthB;
        public int IfShift => IsA ? Radio.IfShiftA : Radio.IfShiftB;
    }
}
