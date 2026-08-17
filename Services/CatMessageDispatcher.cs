namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Routes CAT messages to appropriate handlers and updates radio state
    /// </summary>
    public class CatMessageDispatcher
    {
        private readonly RadioStateService _stateService;
        private readonly ILogger<CatMessageDispatcher> _logger;

        // Callback for initialization complete
        public Action? OnInitializationComplete { get; set; }

        // Invoked after a VS change on single-receiver radios once init is
        // complete. CatMultiplexerService wires a debounced per-VFO re-query.
        public Action? OnActiveVfoChanged { get; set; }

        public CatMessageDispatcher(
            RadioStateService stateService,
            ILogger<CatMessageDispatcher> logger)
        {
            _stateService = stateService;
            _logger = logger;
            _ = ProcessPendingAsync();
        }

        // -- P1=0-Fixed receive-control buffering --------------------------
        //
        // On single-receiver radios (FTdx10 / FT-710 / FTDX3000 / FT-991A)
        // the receive-control CAT commands (GT, PA, RA, NR, NB, NL, BC, SH,
        // IS, BP, CO, RL, AG, RG, SQ, SL) all use "P1: 0 Fixed". The radio
        // has one physical set of controls and the command's P1 doesn't
        // identify a VFO. When the user presses A/B on the front panel,
        // the radio broadcasts the new VFO's receive-control values BEFORE
        // it broadcasts VS, so a naive "route by current ActiveVfo" would
        // write the new VFO's values to the OLD ActiveVfo's slot.
        //
        // Pre5 fix (Jacek SP3L #34, after pre4): buffer single-receiver
        // P1=0-Fixed broadcasts for 300 ms before applying so A/B-press
        // broadcasts that arrive before VS can settle. Updates whose
        // ActiveVfo changed between enqueue and flush are dropped — any
        // broadcast spanning a VS boundary is ambiguous; ground truth comes
        // from the post-VS re-query burst. Dual-receiver (FTdx101) routes
        // immediately by P1 — no race possible there.
        //
        // Items process in arrival order on a single consumer task so
        // last-write-wins semantics for the same property are preserved.

        private const int BufferDelayMs = 300;

        private sealed record PendingDispatch(
            DateTimeOffset EnqueuedAt,
            int ActiveVfoAtEnqueue,
            Action<bool> Apply);

        private readonly System.Threading.Channels.Channel<PendingDispatch> _pending
            = System.Threading.Channels.Channel.CreateUnbounded<PendingDispatch>();

        private async Task ProcessPendingAsync()
        {
            await foreach (var item in _pending.Reader.ReadAllAsync())
            {
                var age = DateTimeOffset.UtcNow - item.EnqueuedAt;
                var wait = TimeSpan.FromMilliseconds(BufferDelayMs) - age;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                if (_stateService.ActiveVfo != item.ActiveVfoAtEnqueue)
                {
                    _logger.LogDebug(
                        "Dropped buffered P1=0-Fixed update: ActiveVfo changed {Old} → {New} during {DelayMs} ms buffer",
                        item.ActiveVfoAtEnqueue, _stateService.ActiveVfo, BufferDelayMs);
                    continue;
                }
                try { item.Apply(_stateService.ActiveVfo == 1); }
                catch { /* per-item failures shouldn't break the consumer */ }
            }
        }

        /// <summary>
        /// Apply a per-VFO receive-control update. On dual-receiver radios,
        /// writes immediately using the message's P1. On single-receiver
        /// radios, buffers the update for 300 ms then applies using ActiveVfo
        /// at apply-time unless ActiveVfo changed since enqueue (drop). See
        /// class comment above.
        /// </summary>
        /// <param name="p1Char">The P1 character from the CAT message (used
        /// on dual-receiver only).</param>
        /// <param name="apply">Action that takes a bool "route to B" and
        /// writes the value to the right RadioStateService slot.</param>
        private void SetPerVfo(char p1Char, Action<bool> apply)
        {
            if (_stateService.IsSingleReceiver)
            {
                _pending.Writer.TryWrite(new PendingDispatch(
                    DateTimeOffset.UtcNow,
                    _stateService.ActiveVfo,
                    apply));
            }
            else
            {
                apply(p1Char == '1');
            }
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
                        var faBody = message.Substring(2).TrimEnd(';');
                        if (long.TryParse(faBody, out var freqA))
                        {
                            _stateService.FrequencyA = freqA;
                            // Learn the radio's native frequency width (8 for the
                            // FTDX3000, 9 for the rest) so writes are formatted to
                            // match — see RadioStateService.FrequencyDigits.
                            if (faBody.Length is 8 or 9) _stateService.FrequencyDigits = faBody.Length;
                        }
                        break;
                    case "FB":
                        var fbBody = message.Substring(2).TrimEnd(';');
                        if (long.TryParse(fbBody, out var freqB))
                        {
                            _stateService.FrequencyB = freqB;
                            if (fbBody.Length is 8 or 9) _stateService.FrequencyDigits = fbBody.Length;
                        }
                        break;
                    case "DT":
                        HandleInitialization(message);
                        break;
                    case "MD":
                        // Example: MD01; (VFO A, LSB), MD12; (VFO B, USB)
                        // Single-receiver: P1 is "0 Fixed" — routes via SetPerVfo's
                        // 300 ms buffer so A/B-press broadcasts land on ActiveVfo.
                        if (message.Length >= 5)
                        {
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
                                SetPerVfo(message[2], routeB => {
                                    if (routeB) _stateService.ModeB = mode;
                                    else        _stateService.ModeA = mode;
                                });
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
                        // Single-receiver: P1 is "0 Fixed" — routes via SetPerVfo's 300 ms
                        // buffer so A/B-press broadcasts land on the new ActiveVfo.
                        if (message.Length >= 4)
                        {
                            var filterCode = message[3].ToString();
                            // FTDX3000 answers RF in a read-code space (P3) that
                            // differs from its dropdown set codes (P2); normalise
                            // so the UI stays in sync. Other single-receiver
                            // radios (FTdx10) already report in dropdown codes.
                            var code = _stateService.RadioModel == "FTDX3000"
                                ? Ftdx3000Roofing.NormalizeReadCode(filterCode)
                                : filterCode;
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.RoofingFilterB = code;
                                else        _stateService.RoofingFilterA = code;
                            });
                        }
                        break;
                    case "GT":
                        // GT0P2; or GT1P2; — P2: 0=OFF 1=FAST 2=MID 3=SLOW 4=AUTO 5/6=AUTO variant
                        // Values 5 and 6 (AUTO-FAST / AUTO-MID / AUTO-SLOW) are read-only settled
                        // states; normalise them to "4" (AUTO) so the UI dropdown stays consistent.
                        // Single-receiver: P1 is "0 Fixed" — routes via SetPerVfo's 300 ms buffer
                        // so that A/B-press broadcasts land on the new ActiveVfo (#34 pre5).
                        if (message.Length >= 4)
                        {
                            var agcCode = message[3].ToString();
                            if (agcCode == "5" || agcCode == "6") agcCode = "4";
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.AgcB = agcCode;
                                else _stateService.AgcA = agcCode;
                            });
                        }
                        break;
                    case "PA":
                        // PA{vfo}{code}; — vfo: 0=Main 1=Sub; code: 0=IPO 1=AMP1 2=AMP2
                        if (message.Length >= 4)
                        {
                            var code = message[3].ToString();
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.IpoB = code;
                                else _stateService.IpoA = code;
                            });
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
                            var code = message[3].ToString();
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.AutoNotchB = code;
                                else _stateService.AutoNotchA = code;
                            });
                        }
                        break;
                    case "NR":
                        // NR{vfo}{code}; — vfo: 0=Main 1=Sub; code: 0=OFF 1=NR1 2=NR2
                        if (message.Length >= 4)
                        {
                            var code = message[3].ToString();
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.NrB = code;
                                else _stateService.NrA = code;
                            });
                        }
                        break;
                    case "NB":
                        // NB{vfo}{code}; — vfo: 0=Main 1=Sub; code: 0=OFF 1=ON
                        if (message.Length >= 4)
                        {
                            var code = message[3].ToString();
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.NbB = code;
                                else _stateService.NbA = code;
                            });
                        }
                        break;
                    case "RA":
                        // RA{vfo}{n}; — vfo: 0=Main 1=Sub; n: 0=OFF 1=6dB 2=12dB 3=18dB
                        if (message.Length >= 4)
                        {
                            var catCode = message[3].ToString();
                            var code = catCode switch { "0" => "00", "1" => "06", "2" => "12", "3" => "18", _ => "00" };
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.AttB = code;
                                else _stateService.AttA = code;
                            });
                        }
                        break;
                    case "BP":
                        // BP{vfo}{param}{3-digit value};
                        // param 0 = on/off: 000=OFF 001=ON
                        // param 1 = frequency: value × 10 = Hz (001=10Hz … 320=3200Hz)
                        if (message.Length >= 7)
                        {
                            var param = message[3];
                            var val = message.Substring(4, 3);
                            if (param == '0')
                            {
                                var isOn = val == "001" ? "1" : "0";
                                SetPerVfo(message[2], routeB => {
                                    if (routeB) _stateService.ManualNotchB = isOn;
                                    else _stateService.ManualNotchA = isOn;
                                });
                            }
                            else if (param == '1' && int.TryParse(val, out int raw) && raw > 0)
                            {
                                var hz = raw * 10;
                                SetPerVfo(message[2], routeB => {
                                    if (routeB) _stateService.ManualNotchFreqB = hz;
                                    else _stateService.ManualNotchFreqA = hz;
                                });
                            }
                        }
                        break;
                    case "SH":
                        // SH{vfo}0{nn}; — vfo: 0=Main 1=Sub; [3]='0' fixed; [4-5]=2-digit code
                        if (message.Length >= 6)
                        {
                            var rawCode = message.Substring(4, 2).TrimStart('0');
                            var code = string.IsNullOrEmpty(rawCode) ? "0" : rawCode;
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.IfWidthB = code;
                                else _stateService.IfWidthA = code;
                            });
                        }
                        break;
                    case "IS":
                        // IS{vfo}0{sign}{nnnn}; — sign '+'/'-', nnnn=absolute Hz
                        if (message.Length >= 9)
                        {
                            var sign = message[4];
                            if (int.TryParse(message.Substring(5, 4), out int absHz))
                            {
                                var shiftHz = sign == '-' ? -absHz : absHz;
                                SetPerVfo(message[2], routeB => {
                                    if (routeB) _stateService.IfShiftB = shiftHz;
                                    else _stateService.IfShiftA = shiftHz;
                                });
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
                            char p2 = message[3];
                            if (int.TryParse(message.Substring(4, 4), out int coVvvv))
                            {
                                SetPerVfo(message[2], routeB => {
                                    switch (p2)
                                    {
                                        case '0':
                                            if (routeB) _stateService.ContourOnB = coVvvv == 1;
                                            else        _stateService.ContourOnA = coVvvv == 1;
                                            break;
                                        case '1':
                                            if (routeB) _stateService.ContourFreqB = coVvvv;
                                            else        _stateService.ContourFreqA = coVvvv;
                                            break;
                                        case '2':
                                            if (routeB) _stateService.ApfOnB = coVvvv == 1;
                                            else        _stateService.ApfOnA = coVvvv == 1;
                                            break;
                                        case '3':
                                            int apfHz = (coVvvv - 25) * 10;
                                            if (routeB) _stateService.ApfFreqB = apfHz;
                                            else        _stateService.ApfFreqA = apfHz;
                                            break;
                                    }
                                });
                            }
                        }
                        break;
                    case "PR":
                        // PR{P1}{P2}; — P1=0 Speech Processor (we ignore P1=1
                        // Parametric Mic EQ), P2=0 OFF / P2=1 ON. The CAT
                        // manual incorrectly lists P2 as 1=OFF/2=ON; bench
                        // testing on the FTdx101MP (2026-06-25) confirmed
                        // the real values are 0=OFF/1=ON.
                        if (message.Length >= 5 && message[2] == '0'
                            && (message[3] == '0' || message[3] == '1'))
                            _stateService.ProcEnabled = message[3] == '1';
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
                    case "SS":
                        // SS P1 P2 {5-char field}; — SPECTRUM SCOPE. The radio
                        // announces scope changes made at its own front panel,
                        // one message per sub-command, tagged with the band.
                        //
                        // Measured on an FTdx101MP, 2026-08-15: SPAN (P2=5),
                        // MODE (6) and HOLD (8) all report. A mode change
                        // arrives as TWO messages a few ms apart — the new mode
                        // and the span that mode carries, because the radio
                        // stores span per display mode. MARKER and LEVEL were
                        // never observed, which may mean they do not report or
                        // may only mean they were not touched during the test;
                        // routing on P2 instead of listing sub-commands means it
                        // costs nothing to be wrong about that.
                        //
                        // Deliberately not stored: the scope panel holds this
                        // state in the browser and re-reads it on every expand,
                        // and it is the only consumer. See BroadcastTransient.
                        if (message.Length >= 10)
                        {
                            var ss = message.TrimEnd(';');
                            if (ss.Length == 9)
                            {
                                _stateService.BroadcastTransient("ScopeSetting", new
                                {
                                    band    = ss[2] == '1' ? "sub" : "main",
                                    setting = ss[3].ToString(),
                                    field   = ss.Substring(4, 5)
                                });
                            }
                        }
                        break;
                    case "VS":
                        // VS{n}; — VFO SELECT: 0=VFO-A active (operating/RX),
                        // 1=VFO-B active. On single-receiver radios this is
                        // what changes when the user presses A/B on the front
                        // panel in normal mode (TxVfo / FT does NOT — that
                        // only flips when split mode toggles). Jacek SP3L #34
                        // R2 root cause: dispatcher had no case for VS so the
                        // radio's broadcast was silently dropped.
                        if (message.Length >= 4 && int.TryParse(message.Substring(2, 1), out int activeVfo))
                        {
                            var previous = _stateService.ActiveVfo;
                            _stateService.ActiveVfo = activeVfo;
                            if (_stateService.IsSingleReceiver
                                && _stateService.IsInitialized
                                && previous != activeVfo)
                            {
                                OnActiveVfoChanged?.Invoke();
                            }
                        }
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
                            var clamped = Math.Clamp(nlVal, 1, 20);
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.NbLevelB = clamped;
                                else _stateService.NbLevelA = clamped;
                            });
                        }
                        break;
                    case "RL":
                        // RL{vfo}{nn}; — NR Level / DNR algorithm 01-15 per VFO.
                        if (message.Length >= 5 && int.TryParse(message.Substring(3, 2), out int rlVal))
                        {
                            var clamped = Math.Clamp(rlVal, 1, 15);
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.NrLevelB = clamped;
                                else _stateService.NrLevelA = clamped;
                            });
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
                            var clamped = Math.Clamp(rgVal, 0, 255);
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.RfGainB = clamped;
                                else _stateService.RfGainA = clamped;
                            });
                        }
                        break;
                    case "SQ":
                        // SQ{V}{NNN}; — Squelch 000-255 per VFO (V=0=Main, V=1=Sub)
                        if (message.Length >= 6 && int.TryParse(message.Substring(3, 3), out int sqVal))
                        {
                            var clamped = Math.Clamp(sqVal, 0, 255);
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.SquelchB = clamped;
                                else _stateService.SquelchA = clamped;
                            });
                        }
                        break;
                    case "AG":
                        // AG{V}{NNN}; — AF Gain 000-255 per VFO (V=0=Main, V=1=Sub)
                        if (message.Length >= 6 && int.TryParse(message.Substring(3, 3), out int agVal))
                        {
                            var clamped = Math.Clamp(agVal, 0, 255);
                            SetPerVfo(message[2], routeB => {
                                if (routeB) _stateService.AfGainB = clamped;
                                else _stateService.AfGainA = clamped;
                            });
                        }
                        break;
                    case "MS":
                        // MS{P1}; or MS{P1}{P2}; — front-panel METER SW selection.
                        // Stored verbatim: the parameter encoding differs per model
                        // (see RadioStateService.RadioMeterSelection) and YWC only
                        // ever needs to replay it, never to understand it. Sent at
                        // init from RadioInitializationService's readQueries, and
                        // pushed by the radio's auto-information whenever the
                        // operator changes meters. (It was NOT sent between
                        // 2026-02-22 and 2026-08-15: the read sat in
                        // CatMultiplexerService.GetInitialValues(), which lost its
                        // only caller in 5d83175 and became dead code. That method
                        // was deleted 2026-08-17 — see git history if you need the
                        // original list of reads.)
                        {
                            var msDigits = message.Substring(2).TrimEnd(';');
                            if (msDigits.Length is 1 or 2 && msDigits.All(char.IsDigit))
                                _stateService.ReportMeterSelection(msDigits);
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
                    case "ID":
                        // ID{nnnn}; — hardware revision identifier e.g. ID0682;
                        // Stored for runtime capability checks (VC Tune CAT support etc.).
                        if (message.Length > 4)
                        {
                            var hwId = message.Substring(2).TrimEnd(';');
                            if (!string.IsNullOrEmpty(hwId))
                                _stateService.Id = hwId;
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