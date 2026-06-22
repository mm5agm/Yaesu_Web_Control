using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;

namespace Yaesu_Web_Control.Pages
{
    // Add this at the top or in a suitable namespace
    public class RadioStateViewModel
    {
        public VfoViewModel vfoA { get; set; } = new VfoViewModel();
        public VfoViewModel vfoB { get; set; } = new VfoViewModel();
    }

    public class VfoViewModel
    {
        public long frequency { get; set; }
        public string band { get; set; } = string.Empty;
        public int sMeter { get; set; }
        public int power { get; set; } // <-- KEEP this property for VFO A usage
        public string mode { get; set; } = string.Empty;
        public string antenna { get; set; } = string.Empty;
    }

    public class IndexModel : PageModel
    {
        private readonly RadioStateService _radioStateService;
        private readonly ISettingsService _settingsService;

        public IndexModel(RadioStateService radioStateService, ISettingsService settingsService)
        {
            _radioStateService = radioStateService;
            _settingsService = settingsService;
        }

        public string SelectedBandA { get; set; } = string.Empty;
        public string SelectedBandB { get; set; } = string.Empty;

        // External app button visibility and names
        public bool ShowApp1Button { get; set; } = true;
        public bool ShowApp2Button { get; set; } = true;
        public bool ShowApp3Button { get; set; } = true;
        public bool ShowApp4Button { get; set; } = false;
        public string App1Name { get; set; } = "WSJT-X";
        public string App2Name { get; set; } = "JTAlert";
        public string App3Name { get; set; } = "Log4OM";
        public string App4Name { get; set; } = "GridTracker";

        // MIC Gain persisted value
        public int MicGain { get; set; } = 50;

        public bool ProcEnabled { get; set; } = false;
        public int ProcLevel { get; set; } = 50;

        public bool AtuEnabled { get; set; } = false;
        public int AfGainA { get; set; } = 0;
        public int AfGainB { get; set; } = 0;
        public bool MonitorOn { get; set; } = false;
        public int MonitorLevelA { get; set; } = 0;
        public bool VoxOn { get; set; } = false;
        public int VoxGain { get; set; } = 50;
        public int VoxDelay { get; set; } = 50;
        public int AntiVoxGain { get; set; } = 50;
        public int CwSpeed { get; set; } = 20;
        public string CwBreakIn { get; set; } = "0";
        public int CwBreakInDelay { get; set; } = 200;
        public int CwPitch { get; set; } = 30;
        public List<string> CwMessages { get; set; } = new() { "CQ CQ DE {CALL}", "TU 73", "QRZ?", "UR 5NN", "DE {CALL}" };
        public string FmShiftDir { get; set; } = "0";
        public int FmOffsetHz { get; set; } = 600000;
        public string CtcssMode { get; set; } = "00";
        public string CtcssTone { get; set; } = "01";

        public double SdrSampleRateHz  { get; set; } = 2_048_000;
        public double SdrSampleRateHzA { get; set; } = 2_048_000;
        public double SdrSampleRateHzB { get; set; } = 2_048_000;
        public string BandPlan { get; set; } = "Region1";
        public string RadioModel { get; set; } = "FTdx101MP";
        public List<string> InstalledRoofingFilters { get; set; } = new() { "6", "7", "8", "9", "A" };

        // Accessibility — show ▲/▼ arrow buttons beside each VFO's frequency
        // display for users who can't use a scroll wheel (Yuri W4YSW, etc.).
        // Default false; user enables via Settings > Accessibility.
        public bool ShowFrequencyArrowButtons { get; set; } = false;

        public RadioStateService RadioState => _radioStateService;

        public RadioStateViewModel State { get; set; } = new RadioStateViewModel();

        public async Task<IActionResult> OnGetAsync()
        {
            if (Yaesu_Web_Control.Services.AppStatus.InitializationStatus == "error")
            {
                return RedirectToPage("/Settings");
            }

            // Load app button visibility and names
            var settings = await _settingsService.GetSettingsAsync();

            // Alternative-layout routing (#48). If the user picked the Jacek
            // dashboard layout in Settings, send them to /IndexJacek which
            // renders the same data through a different Razor page. Anything
            // unrecognised falls back to "Default" via the Settings save path.
            if (string.Equals(settings.LayoutTemplate, "Jacek", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToPage("/IndexJacek");
            }
            ShowApp1Button = settings.ShowWsjtxButton;
            ShowApp2Button = settings.ShowJtalertButton;
            ShowApp3Button = settings.ShowLog4omButton;
            ShowApp4Button = settings.ShowGridtrackerButton;
            ShowFrequencyArrowButtons = settings.ShowFrequencyArrowButtons;
            App1Name = settings.App1Name;
            App2Name = settings.App2Name;
            App3Name = settings.App3Name;
            App4Name = settings.App4Name;
            SdrSampleRateHz  = settings.SdrSampleRateHzA;     // legacy single value, used by old code paths
            SdrSampleRateHzA = settings.SdrSampleRateHzA;
            SdrSampleRateHzB = settings.SdrSampleRateHzB;
            BandPlan = settings.BandPlan switch { "UK" => "Region1", "USA" => "Region2", var v => v };
            RadioModel = settings.RadioModel;
            InstalledRoofingFilters = settings.InstalledRoofingFilters;

            // Load persisted MIC Gain, PROC, and other TX controls
            MicGain = _radioStateService.MicGain;
            ProcEnabled = _radioStateService.ProcEnabled;
            ProcLevel = _radioStateService.ProcLevel;
            AtuEnabled = _radioStateService.AtuEnabled;
            AfGainA = _radioStateService.AfGainA;
            AfGainB = _radioStateService.AfGainB;
            MonitorOn = _radioStateService.MonitorOn;
            MonitorLevelA = _radioStateService.MonitorLevelA;
            VoxOn = _radioStateService.VoxOn;
            VoxGain = _radioStateService.VoxGain;
            VoxDelay = _radioStateService.VoxDelay;
            AntiVoxGain = _radioStateService.AntiVoxGain;
            CwSpeed = _radioStateService.CwSpeed;
            CwBreakIn = _radioStateService.CwBreakIn;
            CwBreakInDelay = _radioStateService.CwBreakInDelay;
            CwPitch = _radioStateService.CwPitch;
            CwMessages = settings.CwMessages;
            FmShiftDir = _radioStateService.FmShiftDir;
            FmOffsetHz = _radioStateService.FmOffsetHz;
            CtcssMode = _radioStateService.CtcssMode;
            CtcssTone = _radioStateService.CtcssTone;

            // VFO A (keep as is)
            State.vfoA.frequency = _radioStateService.FrequencyA;
            State.vfoA.band = _radioStateService.BandA;
            State.vfoA.sMeter = _radioStateService.SMeterA ?? 0;
            State.vfoA.power = _radioStateService.Power;
            State.vfoA.mode = _radioStateService.ModeA ?? "";
            State.vfoA.antenna = _radioStateService.AntennaA ?? "";

            // VFO B (remove power assignment)
            State.vfoB.frequency = _radioStateService.FrequencyB;
            State.vfoB.band = _radioStateService.BandB;
            State.vfoB.sMeter = _radioStateService.SMeterB ?? 0;
            State.vfoB.mode = _radioStateService.ModeB ?? "";
            State.vfoB.antenna = _radioStateService.AntennaB ?? "";

            return Page();
        }
    }
}