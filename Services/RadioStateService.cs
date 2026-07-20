using Yaesu_Web_Control.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Yaesu_Web_Control.Services
{
    public class RadioStateService : INotifyPropertyChanged, IRadioStateService
    {
        private readonly ILogger<RadioStateService> _logger;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly RadioStatePersistenceService _statePersistence;

        public bool IsInitialized { get; set; } = false;

        // True when the configured radio model has a single physical receiver
        // (FTdx10 / FT-710 / FTDX3000 / FT-991A). Set by RadioInitialization-
        // Service at startup from RadioCapabilities. The dispatcher uses this
        // to route P1=0 ("Fixed" on single-receiver radios) responses to
        // whichever VFO is currently active (per VS), instead of always
        // writing to *A state — see #34 R2 controls-bleed-across-panels fix.
        public bool IsSingleReceiver { get; set; } = false;

        // Configured radio model (e.g. "FTDX3000"). Set by RadioInitialization-
        // Service at startup. The dispatcher uses this to normalise model-
        // specific CAT read codes — notably the FTDX3000 roofing-filter answer,
        // whose read code space differs from its set code space.
        public string RadioModel { get; set; } = "";

        private RadioState _initialState;

        public RadioStateService(
            ILogger<RadioStateService> logger,
            RadioStatePersistenceService statePersistence,
            IHubContext<RadioHub> hubContext)
        {
            _logger = logger;
            _statePersistence = statePersistence;
            _hubContext = hubContext;
            _initialState = _statePersistence.Load();

            _logger.LogInformation("RadioStateService constructed with IHubContext: {HubContextAvailable}", hubContext != null);

            // ADD THIS LOG:
            _logger.LogInformation("RadioStateService constructed with initial state: ModeA={ModeA}, ModeB={ModeB}, Power={Power}, AntennaA={AntennaA}, AntennaB={AntennaB}, MicGain={MicGain}",
                _initialState.ModeA, _initialState.ModeB, _initialState.Power, _initialState.AntennaA, _initialState.AntennaB, _initialState.MicGain);

            // Initialize properties from _initialState
            FrequencyA = _initialState.FrequencyA;
            FrequencyB = _initialState.FrequencyB;
            BandA = _initialState.BandA;
            BandB = _initialState.BandB;
            ModeA = _initialState.ModeA ?? "";
            ModeB = _initialState.ModeB ?? "";
            AntennaA = _initialState.AntennaA ?? "";
            AntennaB = _initialState.AntennaB ?? "";
            RoofingFilterA = _initialState.RoofingFilterA ?? "";
            RoofingFilterB = _initialState.RoofingFilterB ?? "";
            Power = _initialState.Power;
            MicGain = _initialState.MicGain;
            ProcEnabled = _initialState.ProcEnabled;
            ProcLevel = _initialState.ProcLevel;
            IfWidthA = _initialState.IfWidthA ?? "8";
            IfWidthB = _initialState.IfWidthB ?? "8";
            IfShiftA = _initialState.IfShiftA;
            IfShiftB = _initialState.IfShiftB;
            AfGainA = _initialState.AfGainA;
            AfGainB = _initialState.AfGainB;
        }

        public RadioState InitialState => _initialState;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                // Debug, not Information: SetField runs on every CAT response
                // (dozens per second during the init burst and AI streaming).
                // At Information these three lines per change were a primary
                // source of the synchronous-logging flood that starved the
                // thread pool during startup (issue #73).
                _logger.LogDebug("[SetField] Setting {Property} from {OldValue} to {NewValue}", propertyName, field, value);
                field = value;
                OnPropertyChanged(propertyName);
                BroadcastUpdate(propertyName!, value!);

                // Only save after initialization is complete
                if (IsInitialized)
                {
                    _logger.LogDebug("[SetField] Persisting state (IsInitialized=true, {Property}={Value})", propertyName, value);
                    _statePersistence.Save(this.ToRadioState());
                }
                else
                {
                    _logger.LogDebug("[SetField] NOT persisting {Property} (IsInitialized=false)", propertyName);
                }
            }
            else
            {
                // Log when value doesn't change (helpful for debugging band issues)
                if (propertyName == "BandA" || propertyName == "BandB")
                {
                    _logger.LogDebug("[SetField] {Property} unchanged (already {Value})", propertyName, value);
                }
            }
        }

        // Call this after DT0; is received -- marks the fast-init burst phase
        // complete and lets subsequent SetField calls persist to disk.
        //
        // Historically this also called ReloadFromPersistence() with the
        // comment "Load the latest persisted state into memory". That was
        // actively harmful: the fast-init burst (AI1; + InitializationCommands
        // + DT0;) is the FIRST chance to read the radio's actual state, and
        // those responses had already arrived and populated RadioStateService
        // by the time DT0 came back. Reloading from disk at that moment
        // overwrote every fresh radio value with stale last-session values.
        // RadioInitializationService.readQueries then re-read most of them,
        // but anything where the response timed out (150 ms) silently kept
        // the stale value -- which is why Jacek SP3L's #40-#46 still showed
        // wrong values in v2.3.9-pre1 even after all the other fixes:
        // every property in those reports is one where the persisted-state
        // overwrite won the race for at least some users.
        //
        // The radio is the source of truth on connect. Persisted state was
        // already loaded in the constructor as a fallback for properties the
        // radio does not report (or hasn't reported yet). Once init starts,
        // we should never reload it.
        public void CompleteInitialization()
        {
            IsInitialized = true;
            // Do NOT call Save() here!
        }

        private void BroadcastUpdate(string property, object value)
        {
            _logger.LogDebug("[BroadcastUpdate] Broadcasting {Property} = {Value}", property, value);
            if (property == "PowerMeter")
            {
                _logger.LogWarning("[DEBUG][PowerMeter] Broadcasting PowerMeter value: {@Value}", value);
            }
            // Special case: PowerMeter should include isTransmitting for frontend sync
            if (property == "PowerMeter")
            {
                _hubContext.Clients.All.SendAsync("RadioStateUpdate", new { property, value = new { value, isTransmitting = this.IsTransmitting } });
            }
            else
            {
                _hubContext.Clients.All.SendAsync("RadioStateUpdate", new { property, value });
            }
        }

        // --- Properties for all CAT commands in GetInitialValues() ---

        private string _id = "";
        public string Id { get => _id; set => SetField(ref _id, value); }

        private int? _agcMain;
        public int? AGCMain { get => _agcMain; set => SetField(ref _agcMain, value); }
        private int? _agcSub;
        public int? AGCSub { get => _agcSub; set => SetField(ref _agcSub, value); }

        // GT command AGC select: "0"=OFF "1"=FAST "2"=MID "3"=SLOW "4"=AUTO
        // Values 5/6 (AUTO-FAST/MID/SLOW) are read-only radio states, mapped to "4" in the UI.
        private string _agcA = "2";
        public string AgcA { get => _agcA; set => SetField(ref _agcA, value); }
        private string _agcB = "2";
        public string AgcB { get => _agcB; set => SetField(ref _agcB, value); }

        // PA command IPO/AMP: "0"=IPO "1"=AMP1 "2"=AMP2
        private string _ipoA = "0";
        public string IpoA { get => _ipoA; set => SetField(ref _ipoA, value); }
        private string _ipoB = "0";
        public string IpoB { get => _ipoB; set => SetField(ref _ipoB, value); }

        // BC command Auto Notch: "0"=OFF "1"=ON
        private string _autoNotchA = "0";
        public string AutoNotchA { get => _autoNotchA; set => SetField(ref _autoNotchA, value); }
        private string _autoNotchB = "0";
        public string AutoNotchB { get => _autoNotchB; set => SetField(ref _autoNotchB, value); }

        // NR command Noise Reduction: "0"=OFF "1"=NR1 "2"=NR2
        private string _nrA = "0";
        public string NrA { get => _nrA; set => SetField(ref _nrA, value); }
        private string _nrB = "0";
        public string NrB { get => _nrB; set => SetField(ref _nrB, value); }

        // RA command Attenuator: "00"=OFF "06"=6dB "12"=12dB "18"=18dB
        private string _attA = "00";
        public string AttA { get => _attA; set => SetField(ref _attA, value); }
        private string _attB = "00";
        public string AttB { get => _attB; set => SetField(ref _attB, value); }

        // BP command Manual Notch on/off: "0"=OFF "1"=ON
        private string _manualNotchA = "0";
        public string ManualNotchA { get => _manualNotchA; set => SetField(ref _manualNotchA, value); }
        private string _manualNotchB = "0";
        public string ManualNotchB { get => _manualNotchB; set => SetField(ref _manualNotchB, value); }
        // BP command Manual Notch frequency: 10–3200 Hz (CAT value = Hz ÷ 10, 3 digits)
        private int _manualNotchFreqA = 1000;
        public int ManualNotchFreqA { get => _manualNotchFreqA; set => SetField(ref _manualNotchFreqA, value); }
        private int _manualNotchFreqB = 1000;
        public int ManualNotchFreqB { get => _manualNotchFreqB; set => SetField(ref _manualNotchFreqB, value); }

        private int? _rfMain;
        public int? RFMain { get => _rfMain; set => SetField(ref _rfMain, value); }
        private int? _rfSub;
        public int? RFSub { get => _rfSub; set => SetField(ref _rfSub, value); }

        private long _frequencyA;
        public long FrequencyA
        {
            get => _frequencyA;
            set
            {
                SetField(ref _frequencyA, value);
                UpdateBandFromFrequency();
            }
        }

        private long _frequencyB;
        public long FrequencyB
        {
            get => _frequencyB;
            set
            {
                SetField(ref _frequencyB, value);
                UpdateBandFromFrequency();
            }
        }

        // Set by CatController whenever the user changes frequency from the web
        // UI. MeterPollingService's FA/FB backstop poll (single-receiver radios,
        // where auto-info is unreliable — iu1teu #78) checks this and skips a
        // poll cycle briefly after a user write, so the poll's read-back can't
        // fight active tuning. Not touched by dispatcher-driven (radio→state)
        // frequency updates, only by genuine user writes.
        public DateTime LastUserFrequencyWriteUtc { get; private set; } = DateTime.MinValue;
        public void MarkUserFrequencyWrite() => LastUserFrequencyWriteUtc = DateTime.UtcNow;

        private string? _fr;
        public string? FR { get => _fr; set => SetField(ref _fr, value); }
        private string? _ft;
        public string? FT { get => _ft; set => SetField(ref _ft, value); }

        private string? _ss04;
        public string? SS04 { get => _ss04; set => SetField(ref _ss04, value); }
        private string? _ss14;
        public string? SS14 { get => _ss14; set => SetField(ref _ss14, value); }

        private string? _ao;
        public string? AO { get => _ao; set => SetField(ref _ao, value); }
        private string? _mg;
        public string? MG { get => _mg; set => SetField(ref _mg, value); }
        private string? _pl;
        public string? PL { get => _pl; set => SetField(ref _pl, value); }
        private string? _pr0;
        public string? PR0 { get => _pr0; set => SetField(ref _pr0, value); }
        private string? _pr1;
        public string? PR1 { get => _pr1; set => SetField(ref _pr1, value); }
        private string? _md0;
        public string? MD0 { get => _md0; set => SetField(ref _md0, value); }
        private string? _md1;
        public string? MD1 { get => _md1; set => SetField(ref _md1, value); }
        private string? _vs;
        public string? VS { get => _vs; set => SetField(ref _vs, value); }
        private string? _kp;
        public string? KP { get => _kp; set => SetField(ref _kp, value); }
        private string? _pc;
        public string? PC { get => _pc; set => SetField(ref _pc, value); }
        private string? _rl0;
        public string? RL0 { get => _rl0; set => SetField(ref _rl0, value); }
        private string? _rl1;
        public string? RL1 { get => _rl1; set => SetField(ref _rl1, value); }
        private string? _nr0;
        public string? NR0 { get => _nr0; set => SetField(ref _nr0, value); }
        private string? _nr1;
        public string? NR1 { get => _nr1; set => SetField(ref _nr1, value); }
        private string? _nb0;
        public string? NB0 { get => _nb0; set => SetField(ref _nb0, value); }
        private string? _nb1;
        public string? NB1 { get => _nb1; set => SetField(ref _nb1, value); }
        private string? _nl0;
        public string? NL0 { get => _nl0; set => SetField(ref _nl0, value); }

        private string _nbA = "0";
        public string NbA { get => _nbA; set => SetField(ref _nbA, value); }
        private string _nbB = "0";
        public string NbB { get => _nbB; set => SetField(ref _nbB, value); }

        // SH command IF Width: "0"=200Hz "1"=400Hz "2"=600Hz "3"=850Hz "4"=1200Hz
        //                      "5"=1400Hz "6"=1800Hz "7"=2400Hz "8"=3000Hz
        private string _ifWidthA = "8";
        public string IfWidthA { get => _ifWidthA; set => SetField(ref _ifWidthA, value); }
        private string _ifWidthB = "8";
        public string IfWidthB { get => _ifWidthB; set => SetField(ref _ifWidthB, value); }

        // IS command IF Shift: stored in Hz, -1000 to +1000. CAT encodes as 0000-9999 (center=5000=0Hz).
        private int _ifShiftA = 0;
        public int IfShiftA { get => _ifShiftA; set => SetField(ref _ifShiftA, value); }
        private int _ifShiftB = 0;
        public int IfShiftB { get => _ifShiftB; set => SetField(ref _ifShiftB, value); }

        private int _clarifierOffsetA = 0;
        public int ClarifierOffsetA { get => _clarifierOffsetA; set => SetField(ref _clarifierOffsetA, value); }
        private int _clarifierOffsetB = 0;
        public int ClarifierOffsetB { get => _clarifierOffsetB; set => SetField(ref _clarifierOffsetB, value); }
        private bool _rxClarOn = false;
        public bool RxClarOn { get => _rxClarOn; set => SetField(ref _rxClarOn, value); }
        private bool _txClarOn = false;
        public bool TxClarOn { get => _txClarOn; set => SetField(ref _txClarOn, value); }

        private bool _contourOnA = false;
        public bool ContourOnA { get => _contourOnA; set => SetField(ref _contourOnA, value); }
        private bool _contourOnB = false;
        public bool ContourOnB { get => _contourOnB; set => SetField(ref _contourOnB, value); }
        private bool _apfOnA = false;
        public bool ApfOnA { get => _apfOnA; set => SetField(ref _apfOnA, value); }
        private bool _apfOnB = false;
        public bool ApfOnB { get => _apfOnB; set => SetField(ref _apfOnB, value); }
        private int _contourFreqA = 800;
        public int ContourFreqA { get => _contourFreqA; set => SetField(ref _contourFreqA, value); }
        private int _contourFreqB = 800;
        public int ContourFreqB { get => _contourFreqB; set => SetField(ref _contourFreqB, value); }
        private int _apfFreqA = 0;
        public int ApfFreqA { get => _apfFreqA; set => SetField(ref _apfFreqA, value); }
        private int _apfFreqB = 0;
        public int ApfFreqB { get => _apfFreqB; set => SetField(ref _apfFreqB, value); }

        private bool _isConnected = false;
        public bool IsConnected { get => _isConnected; set => SetField(ref _isConnected, value); }

        private string _bandA = "20m";
        public string BandA { get => _bandA; set => SetField(ref _bandA, value); }

        private string _bandB = "20m";
        public string BandB { get => _bandB; set => SetField(ref _bandB, value); }

        public Dictionary<string, object> Controls { get; } = new();

        public void SetBand(string receiver, string band)
        {
            if (receiver == "A")
                BandA = band;
            else if (receiver == "B")
                BandB = band;
        }

        public void SetAntenna(string receiver, string antenna)
        {
            if (receiver == "A")
                AntennaA = antenna;
            else if (receiver == "B")
                AntennaB = antenna;
        }

        private int _power;
        public int Power { get => _power; set => SetField(ref _power, value); }

        private string? _modeA = "";
        public string? ModeA { get => _modeA; set => SetField(ref _modeA, value); }

        private string? _modeB = "";
        public string? ModeB { get => _modeB; set => SetField(ref _modeB, value); }

        private string? _antennaA = "";
        public string? AntennaA { get => _antennaA; set => SetField(ref _antennaA, value); }

        private string? _antennaB = "";
        public string? AntennaB { get => _antennaB; set => SetField(ref _antennaB, value); }

        private string _roofingFilterA = "";
        public string RoofingFilterA { get => _roofingFilterA; set => SetField(ref _roofingFilterA, value); }

        private string _roofingFilterB = "";
        public string RoofingFilterB { get => _roofingFilterB; set => SetField(ref _roofingFilterB, value); }


        private int? _sMeterA;
        public int? SMeterA
        {
            get => _sMeterA;
            set
            {
                if (value == null) return;
                int clamped = Math.Clamp(value.Value, 0, 255);
                // Spike filtering removed for full responsiveness
                SetField(ref _sMeterA, clamped);
            }
        }

        private int? _sMeterB;
        public int? SMeterB
        {
            get => _sMeterB;
            set
            {
                if (value == null) return;
                int clamped = Math.Clamp(value.Value, 0, 255);
                // Spike filtering removed for full responsiveness
                SetField(ref _sMeterB, clamped);
            }
        }

        private int? _powerMeter;
        public int? PowerMeter { get => _powerMeter; set => SetField(ref _powerMeter, value); }

        private int? _compressionMeter;
        public int? CompressionMeter
        {
            get => _compressionMeter;
            set
            {
                if (value == null) return;
                int clamped = Math.Clamp(value.Value, 0, 255);
                SetField(ref _compressionMeter, clamped);
            }
        }

        private int? _alcMeter;
        public int? ALCMeter
        {
            get => _alcMeter;
            set
            {
                if (value == null) return;
                int clamped = Math.Clamp(value.Value, 0, 255);
                SetField(ref _alcMeter, clamped);
            }
        }

        private int? _swrMeter;
        public int? SWRMeter
        {
            get => _swrMeter;
            set
            {
                if (value == null) return;
                int clamped = Math.Clamp(value.Value, 0, 255);
                if (_swrMeter.HasValue && _swrMeter.Value != 0 && clamped != 0 && Math.Abs(clamped - _swrMeter.Value) > 30)
                {
                    _logger.LogWarning("[SWRMeter] Ignored spike: {Old} -> {New}", _swrMeter, clamped);
                    return;
                }
                SetField(ref _swrMeter, clamped);
            }
        }

        // Removed duplicate int? _power and int? Power definitions. Only int _power and int Power property remain.

        private int? _maxPower;
        public int? MaxPower
        {
            get => _maxPower;
            set => SetField(ref _maxPower, value);
        }

        private bool _isTransmitting;
        public bool IsTransmitting { get => _isTransmitting; set => SetField(ref _isTransmitting, value); }

        private int? _iddMeter;
        public int? IDDMeter { get => _iddMeter; set => SetField(ref _iddMeter, value); }

        private int? _vddMeter;
        public int? VDDMeter { get => _vddMeter; set => SetField(ref _vddMeter, value); }

        private int? _temperature;
        public int? Temperature { get => _temperature; set => SetField(ref _temperature, value); }

        private int _afGainA;
        public int AfGainA { get => _afGainA; set => SetField(ref _afGainA, value); }
        private int _afGainB;
        public int AfGainB { get => _afGainB; set => SetField(ref _afGainB, value); }

        private int _micGain = 50;
        public int MicGain { get => _micGain; set => SetField(ref _micGain, value); }

        private bool _procEnabled = false;
        public bool ProcEnabled { get => _procEnabled; set => SetField(ref _procEnabled, value); }

        private int _procLevel = 50;
        public int ProcLevel { get => _procLevel; set => SetField(ref _procLevel, value); }

        // ATU: false = bypass, true = engaged
        private bool _atuEnabled = false;
        public bool AtuEnabled { get => _atuEnabled; set => SetField(ref _atuEnabled, value); }

        // ATU auto-tune cycle currently in progress. Driven by P2 of the AC
        // command's answer/auto-info — radio sets P2=1 while the matching
        // network is being adjusted, P2=0 when finished. The UI uses this to
        // grey/animate the ATU button while a tune is running.
        private bool _atuTuning = false;
        public bool AtuTuning { get => _atuTuning; set => SetField(ref _atuTuning, value); }

        // NB Level per VFO: 1–20
        private int _nbLevelA = 10;
        public int NbLevelA { get => _nbLevelA; set => SetField(ref _nbLevelA, value); }
        private int _nbLevelB = 10;
        public int NbLevelB { get => _nbLevelB; set => SetField(ref _nbLevelB, value); }

        // NR Level (RL command) per VFO: 1–15.
        // On FTdx10 / FT-710 this is the DNR algorithm selector (Jacek
        // SP3L #47 -- the FTdx10 has no NR1/NR2 distinction, only ON/OFF
        // plus this 1–15 algorithm number, semantically like "NB Level").
        // On FTdx101 this is the level that applies to whichever NR type
        // (NR1 or NR2) is currently selected.
        private int _nrLevelA = 1;
        public int NrLevelA { get => _nrLevelA; set => SetField(ref _nrLevelA, value); }
        private int _nrLevelB = 1;
        public int NrLevelB { get => _nrLevelB; set => SetField(ref _nrLevelB, value); }

        // CW Pitch: code 0–75 = 300–1050 Hz in 10 Hz steps (KP command)
        private int _cwPitch = 30; // default code 30 = 600 Hz
        public int CwPitch { get => _cwPitch; set => SetField(ref _cwPitch, value); }

        // RF Gain per VFO: 0–255 (RG command)
        private int _rfGainA = 255;
        public int RfGainA { get => _rfGainA; set => SetField(ref _rfGainA, value); }
        private int _rfGainB = 255;
        public int RfGainB { get => _rfGainB; set => SetField(ref _rfGainB, value); }

        // Squelch per VFO: 0–255 (SQ command)
        private int _squelchA = 0;
        public int SquelchA { get => _squelchA; set => SetField(ref _squelchA, value); }
        private int _squelchB = 0;
        public int SquelchB { get => _squelchB; set => SetField(ref _squelchB, value); }

        // Monitor/sidetone on/off and level (ML command)
        private bool _monitorOn = false;
        public bool MonitorOn { get => _monitorOn; set => SetField(ref _monitorOn, value); }
        private int _monitorLevelA = 0;
        public int MonitorLevelA { get => _monitorLevelA; set => SetField(ref _monitorLevelA, value); }
        private int _monitorLevelB = 0;
        public int MonitorLevelB { get => _monitorLevelB; set => SetField(ref _monitorLevelB, value); }

        // VOX
        private bool _voxOn = false;
        public bool VoxOn { get => _voxOn; set => SetField(ref _voxOn, value); }
        private int _voxGain = 50;
        public int VoxGain { get => _voxGain; set => SetField(ref _voxGain, value); }
        private int _voxDelay = 50;
        public int VoxDelay { get => _voxDelay; set => SetField(ref _voxDelay, value); }
        private int _antiVoxGain = 50;
        public int AntiVoxGain { get => _antiVoxGain; set => SetField(ref _antiVoxGain, value); }

        // FM Repeater
        private string _fmShiftDir = "0";
        public string FmShiftDir { get => _fmShiftDir; set => SetField(ref _fmShiftDir, value); }
        private int _fmOffsetHz = 600000;
        public int FmOffsetHz { get => _fmOffsetHz; set => SetField(ref _fmOffsetHz, value); }
        private string _ctcssMode = "00";
        public string CtcssMode { get => _ctcssMode; set => SetField(ref _ctcssMode, value); }
        private string _ctcssTone = "01";
        public string CtcssTone { get => _ctcssTone; set => SetField(ref _ctcssTone, value); }

        // CW Keyer
        private int _cwSpeed = 20;
        public int CwSpeed { get => _cwSpeed; set => SetField(ref _cwSpeed, value); }
        private string _cwBreakIn = "0";
        public string CwBreakIn { get => _cwBreakIn; set => SetField(ref _cwBreakIn, value); }
        private int _cwBreakInDelay = 200;
        public int CwBreakInDelay { get => _cwBreakInDelay; set => SetField(ref _cwBreakInDelay, value); }

        private bool _radioPowerOn = true; // Assume on when app starts
        public bool RadioPowerOn { get => _radioPowerOn; set => SetField(ref _radioPowerOn, value); }

        // TX VFO: 0 = VFO A is TX, 1 = VFO B is TX
        private int _txVfo = 0;
        public int TxVfo { get => _txVfo; set => SetField(ref _txVfo, value); }

        // VS command — VFO SELECT, indicates which VFO is currently the
        // operating (RX) VFO. Distinct from TxVfo (FT) which only tracks
        // the TX VFO in split mode. On single-receiver radios the front-
        // panel A/B button changes ActiveVfo but does NOT change TxVfo
        // (Jacek SP3L #34 R2 — fixed by switching normal-mode greying
        // from TxVfo to ActiveVfo). 0 = VFO A, 1 = VFO B.
        private int _activeVfo = 0;
        public int ActiveVfo { get => _activeVfo; set => SetField(ref _activeVfo, value); }

        // Split mode: 0 = OFF, 1 = ON (VFO A = RX, VFO B = TX), 2 = ON + Quick Split (+5 kHz)
        private int _splitMode = 0;
        public int SplitMode { get => _splitMode; set => SetField(ref _splitMode, value); }

        // Snapshot of every UI-relevant state property, replayed to each newly
        // connected SignalR client by RadioHub.OnConnectedAsync through the
        // normal RadioStateUpdate envelope. Broadcasts only fire on *change*,
        // so a browser that connects after initialization (second tab, another
        // computer) would otherwise keep the frontend's JS defaults for any
        // property not server-rendered in Index.cshtml — most visibly
        // ActiveVfo/TxVfo/SplitMode, which made VFO A always look active on
        // late-joining clients. Property names must match the handlers in
        // wwwroot/js/ui/site.js. Meters are excluded (they stream at ~10 Hz).
        public IReadOnlyList<KeyValuePair<string, object>> GetClientStateSnapshot()
        {
            return new List<KeyValuePair<string, object>>
            {
                new("IsConnected", IsConnected),
                new("RadioPowerOn", RadioPowerOn),
                new("FrequencyA", FrequencyA),
                new("FrequencyB", FrequencyB),
                new("BandA", BandA),
                new("BandB", BandB),
                new("ModeA", ModeA ?? ""),
                new("ModeB", ModeB ?? ""),
                new("AntennaA", AntennaA ?? ""),
                new("AntennaB", AntennaB ?? ""),
                new("ActiveVfo", ActiveVfo),
                new("TxVfo", TxVfo),
                new("SplitMode", SplitMode),
                new("IsTransmitting", IsTransmitting),
                new("Power", Power),
                new("RoofingFilterA", RoofingFilterA ?? ""),
                new("RoofingFilterB", RoofingFilterB ?? ""),
                new("AgcA", AgcA),
                new("AgcB", AgcB),
                new("IpoA", IpoA),
                new("IpoB", IpoB),
                new("AttA", AttA),
                new("AttB", AttB),
                new("NrA", NrA),
                new("NrB", NrB),
                new("NrLevelA", NrLevelA),
                new("NrLevelB", NrLevelB),
                new("NbA", NbA),
                new("NbB", NbB),
                new("NbLevelA", NbLevelA),
                new("NbLevelB", NbLevelB),
                new("AutoNotchA", AutoNotchA),
                new("AutoNotchB", AutoNotchB),
                new("ManualNotchA", ManualNotchA),
                new("ManualNotchB", ManualNotchB),
                new("ManualNotchFreqA", ManualNotchFreqA),
                new("ManualNotchFreqB", ManualNotchFreqB),
                new("IfWidthA", IfWidthA),
                new("IfWidthB", IfWidthB),
                new("IfShiftA", IfShiftA),
                new("IfShiftB", IfShiftB),
                new("RxClarOn", RxClarOn),
                new("TxClarOn", TxClarOn),
                new("ClarifierOffsetA", ClarifierOffsetA),
                new("ClarifierOffsetB", ClarifierOffsetB),
                new("ContourOnA", ContourOnA),
                new("ContourOnB", ContourOnB),
                new("ContourFreqA", ContourFreqA),
                new("ContourFreqB", ContourFreqB),
                new("ApfOnA", ApfOnA),
                new("ApfOnB", ApfOnB),
                new("ApfFreqA", ApfFreqA),
                new("ApfFreqB", ApfFreqB),
                new("AfGainA", AfGainA),
                new("AfGainB", AfGainB),
                new("RfGainA", RfGainA),
                new("RfGainB", RfGainB),
                new("SquelchA", SquelchA),
                new("SquelchB", SquelchB),
                new("ProcEnabled", ProcEnabled),
                new("ProcLevel", ProcLevel),
                new("AtuEnabled", AtuEnabled),
                new("AtuTuning", AtuTuning),
                new("MonitorOn", MonitorOn),
                new("MonitorLevelA", MonitorLevelA),
                new("MonitorLevelB", MonitorLevelB),
                new("VoxOn", VoxOn),
                new("VoxGain", VoxGain),
                new("VoxDelay", VoxDelay),
                new("CwPitch", CwPitch),
                new("CwSpeed", CwSpeed),
                new("CwBreakIn", CwBreakIn),
                new("CwBreakInDelay", CwBreakInDelay),
                new("FmShiftDir", FmShiftDir),
                new("FmOffsetHz", FmOffsetHz),
                new("CtcssMode", CtcssMode),
                new("CtcssTone", CtcssTone),
            };
        }

        public RadioState GetState()
        {
            return new RadioState
            {
                FrequencyA = FrequencyA,
                FrequencyB = FrequencyB,
                BandA = BandA,
                BandB = BandB,
                ModeA = ModeA ?? "",
                ModeB = ModeB ?? "",
                AntennaA = AntennaA ?? "",
                AntennaB = AntennaB ?? "",
                Power = Power,
                AfGainA = AfGainA,
                AfGainB = AfGainB,
                MicGain = MicGain,
                Controls = Controls
            };
        }

        public void UpdateBandFromFrequency()
        {
            var newBandA = GetBandFromFrequency(FrequencyA);
            var newBandB = GetBandFromFrequency(FrequencyB);

            _logger.LogInformation("[UpdateBandFromFrequency] FreqA={FreqA} -> BandA={OldBandA} -> {NewBandA}, FreqB={FreqB} -> BandB={OldBandB} -> {NewBandB}",
                FrequencyA, BandA, newBandA, FrequencyB, BandB, newBandB);

            BandA = newBandA;
            BandB = newBandB;
        }
        public string GetBandFromFrequency(long freq)
        {
            if (freq >= 1800000 && freq < 2000000) return "160m";
            if (freq >= 3500000 && freq < 4000000) return "80m";
            if (freq >= 5258000 && freq <= 5408000) return "60m";
            if (freq >= 7000000 && freq < 7300000) return "40m";
            if (freq >= 10100000 && freq < 10150000) return "30m";
            if (freq >= 14000000 && freq < 14350000) return "20m";
            if (freq >= 18068000 && freq < 18168000) return "17m";
            if (freq >= 21000000 && freq < 21450000) return "15m";
            if (freq >= 24890000 && freq < 24990000) return "12m";
            if (freq >= 28000000 && freq < 29700000) return "10m";
            if (freq >= 50000000 && freq < 54000000) return "6m";
            if (freq >= 70000000 && freq < 70500000) return "4m";
            return "Unknown";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void UpdateFrequencyB(long freq)
        {
            _frequencyB = freq;
        }

        public RadioState ToRadioState()
        {
            return new RadioState
            {
                FrequencyA = FrequencyA,
                FrequencyB = FrequencyB,
                BandA = BandA,
                BandB = BandB,
                ModeA = ModeA ?? "",
                ModeB = ModeB ?? "",
                AntennaA = AntennaA ?? "",
                AntennaB = AntennaB ?? "",
                RoofingFilterA = RoofingFilterA ?? "",
                RoofingFilterB = RoofingFilterB ?? "",
                Power = Power,
                AfGainA = AfGainA,
                AfGainB = AfGainB,
                MicGain = MicGain,
                AgcA = AgcA,
                AgcB = AgcB,
                IpoA = IpoA,
                IpoB = IpoB,
                AttA = AttA,
                AttB = AttB,
                NrA = NrA,
                NrB = NrB,
                AutoNotchA = AutoNotchA,
                AutoNotchB = AutoNotchB,
                ManualNotchA = ManualNotchA,
                ManualNotchB = ManualNotchB,
                IfWidthA = IfWidthA,
                IfWidthB = IfWidthB,
                IfShiftA = IfShiftA,
                IfShiftB = IfShiftB,
                ClarifierOffsetA = ClarifierOffsetA,
                ClarifierOffsetB = ClarifierOffsetB,
                ContourOnA = ContourOnA,
                ContourOnB = ContourOnB,
                ContourFreqA = ContourFreqA,
                ContourFreqB = ContourFreqB,
                ApfOnA = ApfOnA,
                ApfOnB = ApfOnB,
                ApfFreqA = ApfFreqA,
                ApfFreqB = ApfFreqB,
                ProcEnabled = ProcEnabled,
                ProcLevel = ProcLevel
            };
        }
    }
}