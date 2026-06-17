namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Routes CAT messages to appropriate handlers and updates radio state
    /// </summary>
    public class CatMessageDispatcher
    {
        private readonly RadioStateService _stateService;

        // Callback for initialization complete
        public Action? OnInitializationComplete { get; set; }

        public CatMessageDispatcher(RadioStateService stateService)
        {
            _stateService = stateService;
        }

        /// <summary>
        /// Process a complete CAT message and update state
        /// </summary>
        public void DispatchMessage(string message)
        {
            // Debug logging removed as part of cleanup

            if (string.IsNullOrEmpty(message) || message.Length < 3)
                return;

            int semicolonCount = message.Count(c => c == ';');
            if (semicolonCount > 1)
            {
                var parts = message.Split(';');
                foreach (var part in parts)
                {
                    if (!string.IsNullOrWhiteSpace(part))
                        DispatchMessage(part + ";");
                }
                return;
            }

            string command = message.Substring(0, 2);

            try
            {
                switch (command)
                {
                    case "FA":
                        // Example: FA01420000;
                        if (long.TryParse(message.Substring(2).TrimEnd(';'), out var freqA))
                        {
                            _stateService.FrequencyA = freqA;
                        }
                        break;
                    case "FB":
                        if (long.TryParse(message.Substring(2).TrimEnd(';'), out var freqB))
                        {
                            _stateService.FrequencyB = freqB;
                        }
                        break;
                    case "DT":
                        HandleInitialization(message);
                        break;
                    case "MD":
                        // Example: MD01; (VFO A, LSB), MD12; (VFO B, USB)
                        if (message.Length >= 5)
                        {
                            var vfo = message[2]; // '0' for A, '1' for B
                            var modeCode = message[3];
                            string? mode = modeCode switch
                            {
                                '1' => "LSB",
                                '2' => "USB",
                                '3' => "CW-U",
                                '4' => "FM",
                                '5' => "AM",
                                '6' => "RTTY-L",
                                '7' => "CW-L",
                                '8' => "DATA-L",
                                '9' => "RTTY-U",
                                'A' => "DATA-FM",
                                'B' => "FM-N",
                                'C' => "DATA-U",
                                'D' => "AM-N",
                                'E' => "PSK",
                                'F' => "DATA-FM-N",
                                _ => null
                            };
                            if (mode != null)
                            {
                                if (vfo == '0')
                                    _stateService.ModeA = mode;
                                else if (vfo == '1')
                                    _stateService.ModeB = mode;
                            }
                        }
                        break;
                    case "PC":
                        // Example: PC100; (100W)
                        if (message.Length >= 5 && int.TryParse(message.Substring(2, 3), out var watts))
                        {
                            _stateService.Power = watts;
                        }
                        break;
                    case "TX":
                        // Example: TX0; (not transmitting), TX1; (transmitting)
                        if (message.Length >= 4)
                        {
                            var txStatus = message[2];
                            _stateService.IsTransmitting = (txStatus == '1' || txStatus == '2');
                        }
                        break;
                    case "RF":
                        // Example: RF06; (VFO A, 12kHz filter), RF19; (VFO B, 600Hz filter)
                        // Response format: RF + P1 (0=Main/A, 1=Sub/B) + P3 (filter code 6-A)
                        if (message.Length >= 4)
                        {
                            var vfo = message[2]; // '0' for A, '1' for B
                            var filterCode = message[3].ToString();
                            if (vfo == '0')
                                _stateService.RoofingFilterA = filterCode;
                            else if (vfo == '1')
                                _stateService.RoofingFilterB = filterCode;
                        }
                        break;
                    case "RU":
                        // FTdx10 roofing filter. Response: RU{n}; n=0(Auto),1(15kHz),2(6kHz),3(3kHz)
                        // Single shared filter — store in both A and B so both dropdowns stay in sync.
                        if (message.Length >= 3)
                        {
                            var ruCode = message[2].ToString();
                            _stateService.RoofingFilterA = ruCode;
                            _stateService.RoofingFilterB = ruCode;
                        }
                        break;
                    case "GT":
                        // GT0P2; or GT1P2; — P2: 0=OFF 1=FAST 2=MID 3=SLOW 4=AUTO 5/6=AUTO variant
                        // Values 5 and 6 (AUTO-FAST / AUTO-MID / AUTO-SLOW) are read-only settled
                        // states; normalise them to "4" (AUTO) so the UI dropdown stays consistent.
                        if (message.Length >= 4)
                        {
                            var agcVfo = message[2];
                            var agcCode = message[3].ToString();
                            if (agcCode == "5" || agcCode == "6") agcCode = "4";
                            if (agcVfo == '0') _stateService.AgcA = agcCode;
                            else if (agcVfo == '1') _stateService.AgcB = agcCode;
                        }
                        break;
                    case "PA":
                        // PA{vfo}{code}; — vfo: 0=Main 1=Sub; code: 0=IPO 1=AMP1 2=AMP2
                        if (message.Length >= 4)
                        {
                            var vfo = message[2];
                            var code = message[3].ToString();
                            if (vfo == '0') _stateService.IpoA = code;
                            else if (vfo == '1') _stateService.IpoB = code;
                        }
                        break;
                    case "AN":
                        // AN{vfo}{n}[{p3}]; — vfo: 0=Main 1=Sub; n: 1, 2, 3 (antenna jack).
                        // Closes the gap from Issue #17: previously the front-panel
                        // antenna change was silently ignored by the UI.
                        if (message.Length >= 4)
                        {
                            var vfo = message[2];
                            var ant = message[3].ToString();
                            if (ant == "1" || ant == "2" || ant == "3")
                            {
                                if (vfo == '0') _stateService.AntennaA = ant;
                                else if (vfo == '1') _stateService.AntennaB = ant;
                            }
                        }
                        break;
                    case "BC":
                        // BC{vfo}{code}; — vfo: 0=Main 1=Sub; code: 0=OFF 1=ON
                        if (message.Length >= 4)
                        {
                            var vfo = message[2];
                            var code = message[3].ToString();
                            if (vfo == '0') _stateService.AutoNotchA = code;
                            else if (vfo == '1') _stateService.AutoNotchB = code;
                        }
                        break;
                    case "NR":
                        // NR{vfo}{code}; — vfo: 0=Main 1=Sub; code: 0=OFF 1=NR1 2=NR2
                        if (message.Length >= 4)
                        {
                            var vfo = message[2];
                            var code = message[3].ToString();
                            if (vfo == '0') _stateService.NrA = code;
                            else if (vfo == '1') _stateService.NrB = code;
                        }
                        break;
                    case "NB":
                        // NB{vfo}{code}; — vfo: 0=Main 1=Sub; code: 0=OFF 1=ON
                        if (message.Length >= 4)
                        {
                            var vfo = message[2];
                            var code = message[3].ToString();
                            if (vfo == '0') _stateService.NbA = code;
                            else if (vfo == '1') _stateService.NbB = code;
                        }
                        break;
                    case "RA":
                        // RA{vfo}{n}; — vfo: 0=Main 1=Sub; n: 0=OFF 1=6dB 2=12dB 3=18dB
                        if (message.Length >= 4)
                        {
                            var vfo = message[2];
                            var catCode = message[3].ToString();
                            var code = catCode switch { "0" => "00", "1" => "06", "2" => "12", "3" => "18", _ => "00" };
                            if (vfo == '0') _stateService.AttA = code;
                            else if (vfo == '1') _stateService.AttB = code;
                        }
                        break;
                    case "BP":
                        // BP{vfo}{param}{3-digit value};
                        // param 0 = on/off: 000=OFF 001=ON
                        // param 1 = frequency: value × 10 = Hz (001=10Hz … 320=3200Hz)
                        if (message.Length >= 7)
                        {
                            var vfo = message[2];
                            var param = message[3];
                            var val = message.Substring(4, 3);
                            if (param == '0')
                            {
                                var isOn = val == "001" ? "1" : "0";
                                if (vfo == '0') _stateService.ManualNotchA = isOn;
                                else if (vfo == '1') _stateService.ManualNotchB = isOn;
                            }
                            else if (param == '1' && int.TryParse(val, out int raw) && raw > 0)
                            {
                                var hz = raw * 10;
                                if (vfo == '0') _stateService.ManualNotchFreqA = hz;
                                else if (vfo == '1') _stateService.ManualNotchFreqB = hz;
                            }
                        }
                        break;
                    case "SH":
                        // SH{vfo}0{nn}; — vfo: 0=Main 1=Sub; [3]='0' fixed; [4-5]=2-digit code
                        if (message.Length >= 6)
                        {
                            var vfo = message[2];
                            var rawCode = message.Substring(4, 2).TrimStart('0');
                            var code = string.IsNullOrEmpty(rawCode) ? "0" : rawCode;
                            if (vfo == '0') _stateService.IfWidthA = code;
                            else if (vfo == '1') _stateService.IfWidthB = code;
                        }
                        break;
                    case "IS":
                        // IS{vfo}0{sign}{nnnn}; — sign '+'/'-', nnnn=absolute Hz
                        if (message.Length >= 9)
                        {
                            var vfo = message[2];
                            var sign = message[4];
                            if (int.TryParse(message.Substring(5, 4), out int absHz))
                            {
                                var shiftHz = sign == '-' ? -absHz : absHz;
                                if (vfo == '0') _stateService.IfShiftA = shiftHz;
                                else if (vfo == '1') _stateService.IfShiftB = shiftHz;
                            }
                        }
                        break;
                    case "RT":
                        // RT{P1}; — 0=RX clarifier OFF, 1=ON
                        if (message.Length >= 4 && int.TryParse(message.Substring(2, 1), out int rtVal))
                            _stateService.RxClarOn = rtVal == 1;
                        break;
                    case "XT":
                        // XT{P1}; — 0=TX clarifier OFF, 1=ON
                        if (message.Length >= 4 && int.TryParse(message.Substring(2, 1), out int xtVal))
                            _stateService.TxClarOn = xtVal == 1;
                        break;
                    case "CF":
                        // FTdx10/FT-710: CF{P1}0{P3}...; (11 chars total)
                        // P1=0=VFO A, 1=VFO B; P3=0 → pos5=RXon, pos6=TXon; P3=1 → pos5=sign, pos6-9=Hz
                        if (message.Length >= 11)
                        {
                            char cfVfo = message[2];
                            char p3    = message[4];
                            if (p3 == '0')
                            {
                                _stateService.RxClarOn = message[5] == '1';
                                _stateService.TxClarOn = message[6] == '1';
                            }
                            else if (p3 == '1' && int.TryParse(message.Substring(6, 4), out int offsetAbs))
                            {
                                int offset = message[5] == '-' ? -offsetAbs : offsetAbs;
                                if (cfVfo == '0') _stateService.ClarifierOffsetA = offset;
                                else               _stateService.ClarifierOffsetB = offset;
                            }
                        }
                        break;
                    case "CO":
                        // FTdx101/FTdx10/FT-710: CO[P1][P2][VVVV]; (9 chars)
                        //   P1=0/1, P2=0(ContourOn),1(ContourFreq),2(ApfOn),3(ApfFreq)
                        // FTDX3000: CO[PP][VV]; (7 chars)
                        //   PP=00(mode:0=off,1=contour,2=APF), 01(contourFreq), 02(apfFreq)
                        if (message.Length == 7)
                        {
                            // FTDX3000 format
                            string pp = message.Substring(2, 2);
                            if (int.TryParse(message.Substring(4, 2), out int coVv))
                            {
                                switch (pp)
                                {
                                    case "00":
                                        _stateService.ContourOnA = coVv == 1;
                                        _stateService.ApfOnA     = coVv == 2;
                                        _stateService.ContourOnB = coVv == 1;
                                        _stateService.ApfOnB     = coVv == 2;
                                        break;
                                    case "01":
                                        _stateService.ContourFreqA = coVv * 100;
                                        _stateService.ContourFreqB = coVv * 100;
                                        break;
                                    case "02":
                                        int apfHz3000 = (coVv - 10) * 25;
                                        _stateService.ApfFreqA = apfHz3000;
                                        _stateService.ApfFreqB = apfHz3000;
                                        break;
                                }
                            }
                        }
                        else if (message.Length >= 9)
                        {
                            // FTdx101/FTdx10/FT-710 format
                            bool vfoB = message[2] == '1';
                            char p2   = message[3];
                            if (int.TryParse(message.Substring(4, 4), out int coVvvv))
                            {
                                switch (p2)
                                {
                                    case '0':
                                        if (vfoB) _stateService.ContourOnB = coVvvv == 1;
                                        else       _stateService.ContourOnA = coVvvv == 1;
                                        break;
                                    case '1':
                                        if (vfoB) _stateService.ContourFreqB = coVvvv;
                                        else       _stateService.ContourFreqA = coVvvv;
                                        break;
                                    case '2':
                                        if (vfoB) _stateService.ApfOnB = coVvvv == 1;
                                        else       _stateService.ApfOnA = coVvvv == 1;
                                        break;
                                    case '3':
                                        int apfHz = (coVvvv - 25) * 10;
                                        if (vfoB) _stateService.ApfFreqB = apfHz;
                                        else       _stateService.ApfFreqA = apfHz;
                                        break;
                                }
                            }
                        }
                        break;
                    case "PR":
                        // PR{n}; — n=0=OFF, n=1=ON (speech processor)
                        if (message.Length >= 4 && (message[2] == '0' || message[2] == '1'))
                            _stateService.ProcEnabled = message[2] == '1';
                        break;
                    case "PL":
                        // PL{PPP}; — speech processor level 000-100
                        if (message.Length >= 6 && int.TryParse(message.Substring(2, 3), out var procLevel))
                            _stateService.ProcLevel = Math.Clamp(procLevel, 0, 100);
                        break;
                    case "ST":
                        // ST{mode}; — 0=OFF, 1=ON (VFO A=RX / VFO B=TX), 2=ON+5kHz Quick Split
                        if (message.Length >= 4 && int.TryParse(message.Substring(2, 1), out int splitMode))
                            _stateService.SplitMode = splitMode;
                        break;
                    case "FT":
                        // FT{n}; — 0=VFO A is TX, 1=VFO B is TX
                        if (message.Length >= 4 && int.TryParse(message.Substring(2, 1), out int txVfo))
                            _stateService.TxVfo = txVfo;
                        break;
                    case "AC":
                        // AC{P1}{P2}{P3}; per FTdx10 / FTdx101MP CAT manual page 6:
                        //   P1: 0 Fixed
                        //   P2: 0 Fixed
                        //   P3: 0 = Tuner OFF, 1 = Tuner ON, 2 = Tuning Start/Stop
                        // The multiplexer strips the trailing ';', so we receive
                        // "AC001" / "AC100" / "AC002" etc — length 5 with index
                        // positions: [0]='A' [1]='C' [2]=P1 [3]=P2 [4]=P3.
                        //
                        // We read P3 (message[4]) for on/off, and ALSO read P2
                        // defensively into AtuTuning. The manual says P2 is
                        // Fixed at 0, but if a firmware revision ever starts
                        // reporting tuning-in-progress in P2 we'll pick it up
                        // automatically. With current firmware, AtuTuning gets
                        // set to false on every AC message — harmless, the
                        // tuning-state UI is driven client-side anyway.
                        //
                        // Note: pre-2026-06-14 code read message[2] (P1) for
                        // on/off, which is always '0 Fixed'. That bug silently
                        // dropped every front-panel ATU toggle — only YWC-
                        // initiated toggles showed in the UI because SetAtu
                        // sets state directly without going through this parser.
                        if (message.Length >= 5)
                        {
                            _stateService.AtuEnabled = message[4] == '1';
                            _stateService.AtuTuning  = message[3] == '1';
                        }
                        break;
                    case "NL":
                        // NL{vfo}{nnn}; — NB level 001-020 per VFO
                        if (message.Length >= 6 && int.TryParse(message.Substring(3, 3), out int nlVal))
                        {
                            if (message[2] == '0') _stateService.NbLevelA = Math.Clamp(nlVal, 1, 20);
                            else if (message[2] == '1') _stateService.NbLevelB = Math.Clamp(nlVal, 1, 20);
                        }
                        break;
                    case "ML":
                        // ML0{nnn}; — monitor on/off (000=off, 001=on)
                        // ML1{nnn}; — monitor level 001-100
                        if (message.Length >= 6 && int.TryParse(message.Substring(3, 3), out int mlVal))
                        {
                            if (message[2] == '0') _stateService.MonitorOn = mlVal > 0;
                            else if (message[2] == '1') _stateService.MonitorLevelA = Math.Clamp(mlVal, 0, 100);
                        }
                        break;
                    case "KP":
                        // KP{NN}; — CW pitch code 00-75 (300-1050 Hz in 10 Hz steps)
                        if (message.Length >= 4 && int.TryParse(message.Substring(2, 2), out int kpVal))
                            _stateService.CwPitch = Math.Clamp(kpVal, 0, 75);
                        break;
                    case "RG":
                        // RG{V}{NNN}; — RF Gain 000-255 per VFO (V=0=Main, V=1=Sub)
                        if (message.Length >= 6 && int.TryParse(message.Substring(3, 3), out int rgVal))
                        {
                            if (message[2] == '0') _stateService.RfGainA = Math.Clamp(rgVal, 0, 255);
                            else if (message[2] == '1') _stateService.RfGainB = Math.Clamp(rgVal, 0, 255);
                        }
                        break;
                    case "SQ":
                        // SQ{V}{NNN}; — Squelch 000-255 per VFO (V=0=Main, V=1=Sub)
                        if (message.Length >= 6 && int.TryParse(message.Substring(3, 3), out int sqVal))
                        {
                            if (message[2] == '0') _stateService.SquelchA = Math.Clamp(sqVal, 0, 255);
                            else if (message[2] == '1') _stateService.SquelchB = Math.Clamp(sqVal, 0, 255);
                        }
                        break;
                    case "AG":
                        // AG{V}{NNN}; — AF Gain 000-255 per VFO (V=0=Main, V=1=Sub)
                        if (message.Length >= 6 && int.TryParse(message.Substring(3, 3), out int agVal))
                        {
                            if (message[2] == '0') _stateService.AfGainA = Math.Clamp(agVal, 0, 255);
                            else if (message[2] == '1') _stateService.AfGainB = Math.Clamp(agVal, 0, 255);
                        }
                        break;
                    case "MG":
                        // MG{NNN}; — MIC Gain 000-100
                        if (message.Length >= 6 && int.TryParse(message.Substring(2, 3), out int mgVal))
                            _stateService.MicGain = Math.Clamp(mgVal, 0, 100);
                        break;
                    case "VX":
                        // VX{n}; — 0=VOX off, 1=VOX on
                        if (message.Length >= 4)
                            _stateService.VoxOn = message[2] == '1';
                        break;
                    case "VG":
                        // VG{nnn}; — VOX gain 000-100
                        if (message.Length >= 6 && int.TryParse(message.Substring(2, 3), out int vgVal))
                            _stateService.VoxGain = Math.Clamp(vgVal, 0, 100);
                        break;
                    case "VD":
                        // VD{nnnn}; — VOX delay 0000-2500 ms (stored as int, divide by 25 for slider)
                        if (message.Length >= 7 && int.TryParse(message.Substring(2, 4), out int vdVal))
                            _stateService.VoxDelay = Math.Clamp(vdVal, 0, 2500);
                        break;
                    case "RS":
                        // RS{n}; — repeater shift: 0=none, 1=up, 2=down, 3=split
                        if (message.Length >= 4)
                            _stateService.FmShiftDir = message[2].ToString();
                        break;
                    case "RO":
                        // RO{nnnnnn}; — repeater offset in Hz
                        if (message.Length >= 9 && int.TryParse(message.Substring(2, 6), out int roVal))
                            _stateService.FmOffsetHz = roVal;
                        break;
                    case "CT":
                        // CT{nn}; — CTCSS mode: 00=off, 01=ENC, 02=DEC, 03=T-DEC
                        if (message.Length >= 5)
                            _stateService.CtcssMode = message.Substring(2, 2);
                        break;
                    case "CN":
                        // CN{nn}; — CTCSS/DCS tone number
                        if (message.Length >= 5)
                            _stateService.CtcssTone = message.Substring(2, 2);
                        break;
                    case "KS":
                        // KS{nnn}; — CW keyer speed 004-060 WPM
                        if (message.Length >= 6 && int.TryParse(message.Substring(2, 3), out int ksVal))
                            _stateService.CwSpeed = Math.Clamp(ksVal, 4, 60);
                        break;
                    case "BI":
                        // BI{n}; — CW break-in: 0=off, 1=semi BK-IN, 2=full BK-IN
                        if (message.Length >= 4)
                            _stateService.CwBreakIn = message[2].ToString();
                        break;
                    case "SD":
                        // SD{nnnn}; — semi BK-IN delay 0000-2500 ms
                        if (message.Length >= 7 && int.TryParse(message.Substring(2, 4), out int sdVal))
                            _stateService.CwBreakInDelay = Math.Clamp(sdVal, 0, 2500);
                        break;
                    case "SL":
                        // SL{vfo}0{nn}; — IF low cut code per VFO (mirrors SH structure)
                        if (message.Length >= 6)
                        {
                            var slVfo = message[2];
                            var slRaw = message.Substring(4, 2).TrimStart('0');
                            var slCode = string.IsNullOrEmpty(slRaw) ? "0" : slRaw;
                            if (slVfo == '0') _stateService.IfLowCutA = slCode;
                            else if (slVfo == '1') _stateService.IfLowCutB = slCode;
                        }
                        break;
                    // No debug logging for unhandled commands
                }
            }
            catch (Exception)
            {
                // Suppress diagnostics
            }
        }

        private void HandleInitialization(string message)
        {

            // Only signal initialization complete for DT0; message
            if (message.StartsWith("DT0;") || message.StartsWith("DT0"))
            {
                _stateService.CompleteInitialization(); // Optionally update radio state
                OnInitializationComplete?.Invoke();     // Notify any listeners
            }
        }
    }
}