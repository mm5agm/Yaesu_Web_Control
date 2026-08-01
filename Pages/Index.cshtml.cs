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
        public bool ShowApp5Button { get; set; } = false;
        public string App1Name { get; set; } = "WSJT-X";
        public string App2Name { get; set; } = "JTAlert";
        public string App3Name { get; set; } = "Log4OM";
        public string App4Name { get; set; } = "GridTracker";
        public string App5Name { get; set; } = "Fldigi";

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

        // Per-VFO spectrum DSP knobs — initial values for the Low/High/Zoom
        // sliders on each spectrum panel. Persisted server-side; see
        // ApplicationSettings.SdrSpectrum* and Controllers/SdrController dsp endpoint.
        public float SdrSpectrumGainA   { get; set; } = 1.0f;
        public float SdrSpectrumGainB   { get; set; } = 1.0f;
        public float SdrSpectrumLowDbA  { get; set; } = -120f;
        public float SdrSpectrumLowDbB  { get; set; } = -120f;
        public float SdrSpectrumHighDbA { get; set; } = 0f;
        public float SdrSpectrumHighDbB { get; set; } = 0f;

        public string BandPlan { get; set; } = "Region1";
        public string RadioModel { get; set; } = "FTdx101MP";
        public List<string> InstalledRoofingFilters { get; set; } = new() { "6", "7", "8", "9", "A" };

        // Accessibility — show ▲/▼ arrow buttons beside each VFO's frequency
        // display for users who can't use a scroll wheel (Yuri W4YSW, etc.).
        // Default false; user enables via Settings > Accessibility.
        public bool ShowFrequencyArrowButtons { get; set; } = false;

        // Optional browser key that toggles TX. Empty = disabled.
        public string TxToggleKey { get; set; } = string.Empty;

        // Voice control nudge step defaults for each VFO's mic button
        // dropdown, server-rendered so voice-control.js has a starting
        // value before it can fetch/receive anything.
        public long VoiceNudgeStepHzA { get; set; } = 10_000;
        public long VoiceNudgeStepHzB { get; set; } = 10_000;

        public RadioStateService RadioState => _radioStateService;

        public RadioStateViewModel State { get; set; } = new RadioStateViewModel();

        public async Task<IActionResult> OnGetAsync()
        {
            // Deliberately no server-side redirect to Settings on
            // InitializationStatus == "error" here. If radio initialization
            // keeps failing (e.g. an unhandled exception deep in the init
            // sequence — see Issue #65, FTDX3000), that status flag can get
            // stuck at "error" indefinitely, since nothing resets it except
            // a fully successful re-init. A hard redirect here trapped users
            // on the Settings page with no way back to Home even after they
            // proved the radio was reachable (Test Connection succeeding).
            // The client-side initOverlay/pollInitStatus in site.js already
            // surfaces the error with a link to Settings without forcing
            // navigation, which is the correct, non-trapping UX.

            // Load app button visibility and names
            var settings = await _settingsService.GetSettingsAsync();
            ShowApp1Button = settings.ShowWsjtxButton;
            ShowApp2Button = settings.ShowJtalertButton;
            ShowApp3Button = settings.ShowLog4omButton;
            ShowApp4Button = settings.ShowGridtrackerButton;
            ShowApp5Button = settings.ShowFldigiButton;
            ShowFrequencyArrowButtons = settings.ShowFrequencyArrowButtons;
            TxToggleKey = settings.TxToggleKey == " " ? "Space" : (settings.TxToggleKey ?? string.Empty);
            App1Name = settings.App1Name;
            App2Name = settings.App2Name;
            App3Name = settings.App3Name;
            App4Name = settings.App4Name;
            App5Name = settings.App5Name;
            SdrSampleRateHz  = settings.SdrSampleRateHzA;     // legacy single value, used by old code paths
            SdrSampleRateHzA = settings.SdrSampleRateHzA;
            SdrSampleRateHzB = settings.SdrSampleRateHzB;
            SdrSpectrumGainA   = settings.SdrSpectrumGainA;
            SdrSpectrumGainB   = settings.SdrSpectrumGainB;
            SdrSpectrumLowDbA  = settings.SdrSpectrumLowDbA;
            SdrSpectrumLowDbB  = settings.SdrSpectrumLowDbB;
            SdrSpectrumHighDbA = settings.SdrSpectrumHighDbA;
            SdrSpectrumHighDbB = settings.SdrSpectrumHighDbB;
            VoiceNudgeStepHzA = settings.VoiceNudgeStepHzA;
            VoiceNudgeStepHzB = settings.VoiceNudgeStepHzB;
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