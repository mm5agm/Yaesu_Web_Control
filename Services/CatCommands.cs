namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// FTdx101MP CAT Command Reference
    /// Complete list of CAT commands for the Yaesu FTdx101MP transceiver
    /// </summary>
    public static class CatCommands
    {
        // FREQUENCY COMMANDS
        public const string FrequencyVfoA = "FA";
        public const string FrequencyVfoB = "FB";

        // MODE COMMANDS
        public const string ModeMain = "MD0";
        public const string ModeSub = "MD1";

        // S-METER COMMANDS
        public const string SMeterMain = "SM0";
        public const string SMeterSub = "SM1";

        // METER READING COMMANDS (RM)
        public const string MeterPower = "RM5";    // Power output meter (0-255)
        public const string MeterSWR = "RM6";      // SWR meter (0-255) — NOTE: RM6 returns stale/wrong values on FTdx101MP; use SetMetersSWR+MeterBoth instead
        public const string SetMetersCompAndSWR = "MS13"; // FTdx101MP/D ONLY: Compression(left) + SWR(right) for the RM0 read
        public const string SetMeterPower       = "MS00"; // FTdx101MP/D ONLY: POW(MAIN) + ALC(SUB) — the radio's own default pair

        // Fallback used when restoring the operator's meters and YWC never saw an
        // MS answer (radio powered up mid-session, init answer lost, etc.). The MS
        // digits are MAIN then SUB, not left then right: MAIN 0=POW 1=COMP 2=TEMP,
        // SUB 0=ALC 1=VDD 2=ID 3=SWR. So POW + ALC is MS00, not MS01 (which is
        // POW + VDD). Bench-confirmed on the FTdx101MP 2026-08-15: sending MS00;
        // put the front panel back to PO and ALC. The pre-existing shutdown restore
        // hard-coded MS01 and had been leaving the operator on POW + VDD.
        public const string DefaultFtdx101MeterSelection = "00";
        public const string MeterBoth = "RM0";            // Read both currently-selected meters: RM0LLLRRR;
        public const string MeterALC = "RM4";      // ALC meter (0-255)
        public const string MeterComp = "RM3";     // Compression meter (0-255)
        public const string MeterIDD = "RM7";      // IDD current meter (0-255)
        public const string MeterVDD = "RM8";      // VDD supply meter (0-255)
        public const string MeterTemp = "RM9";     // PA temperature meter (0-255)
        public const string MeterSMain = "RM1";    // S-meter MAIN band (0-255)
        public const string MeterSSub = "RM2";     // S-meter SUB band (0-255)

        // TRANSMIT STATUS
        public const string TransmitStatus = "TX";

        // POWER COMMANDS
        public const string TxPower = "PC";

        // AGC COMMANDS
        public const string Agc    = "GT0";   // Main (VFO A)
        public const string AgcSub = "GT1";   // Sub  (VFO B)

        // FILTER COMMANDS
        public const string FilterHigh = "SH0";

        // CLARIFIER COMMANDS
        public const string ClarifierClear = "RC";
        public const string ClarifierDown = "RD";
        public const string ClarifierUp = "RU";

        // SPLIT OPERATION
        public const string Split = "FT";

        // LOCK COMMANDS
        public const string Lock = "LK";

        // MENU COMMANDS  
        public const string ExtendedMenu = "EX";

        // INFORMATION COMMANDS
        public const string Information = "IF";
        public const string RadioId = "ID";

        // VFO COMMANDS
        public const string VfoSelect = "VS";
        public const string VfoAEqualsB = "AB";
        public const string VfoBEqualsA = "BA";
        public const string VfoSwap = "SV";

        // MEMORY COMMANDS
        public const string MemoryChannel = "MC";
        public const string MemoryRead = "MR";
        public const string MemoryWrite = "MW";

        // BAND COMMANDS
        public const string BandSelect = "BS";

        // CLARIFIER/RIT/XIT
        public const string RitOnOff = "RT";
        public const string XitOnOff = "XT";

        // KEYER COMMANDS
        public const string Keyer = "KY";
        public const string KeyerSpeed = "KS";

        // NOISE BLANKER
        public const string NoiseBlanker = "NB0";

        // NOISE REDUCTION
        public const string NoiseReduction = "NR0";

        // NOTCH FILTER
        public const string AutoNotch = "BC0";

        // CONTOUR
        public const string Contour = "CO00";

        // DNR (Digital Noise Reduction)
        public const string DnrLevel = "RL0";

        // HELPER METHODS
        public static string FormatFrequencyA(long frequencyHz)
            => $"FA{frequencyHz:D9};";

        public static string FormatFrequencyB(long frequencyHz)
            => $"FB{frequencyHz:D9};";

        public static long ParseFrequency(string response)
        {
            // Remove trailing semicolon if present
            var trimmed = response.TrimEnd(';');
            // Ensure the response is at least 11 characters (e.g., "FA007100000")
            if (trimmed.Length >= 11 && (trimmed.StartsWith("FA") || trimmed.StartsWith("FB")))
            {
                var freqStr = trimmed.Substring(2, 9);
                if (long.TryParse(freqStr, out var freq))
                    return freq;
            }
            return 0;
        }

        public static string FormatMode(string mode, bool isSubVfo = false)
        {
            var modeCode = mode.ToUpper() switch
            {
                "LSB"      => "1",
                "USB"      => "2",
                "CW-U"     => "3",
                "FM"       => "4",
                "AM"       => "5",
                "RTTY-L"   => "6",
                "CW-L"     => "7",
                "DATA-L"   => "8",
                "RTTY-U"   => "9",
                "DATA-FM"  => "A",
                "FM-N"     => "B",
                "DATA-U"   => "C",
                "AM-N"     => "D",
                "PSK"      => "E",
                "DATA-FM-N" => "F",
                _ => "2" // Default to USB
            };
            return $"MD{(isSubVfo ? "1" : "0")}{modeCode};";
        }

        public static string ParseMode(string response)
        {
            if (response.Length >= 4 && response.StartsWith("MD"))
            {
                var modeCode = response.Substring(3, 1);
                return modeCode switch
                {
                    "1" => "LSB",
                    "2" => "USB",
                    "3" => "CW-U",
                    "4" => "FM",
                    "5" => "AM",
                    "6" => "RTTY-L",
                    "7" => "CW-L",
                    "8" => "DATA-L",
                    "9" => "RTTY-U",
                    "A" => "DATA-FM",
                    "B" => "FM-N",
                    "C" => "DATA-U",
                    "D" => "AM-N",
                    "E" => "PSK",
                    "F" => "DATA-FM-N",
                    _ => "UNKNOWN"
                };
            }
            return "UNKNOWN";
        }

        public static int ParseSMeter(string response)
        {
            if (string.IsNullOrEmpty(response) || !response.StartsWith("SM"))
                return 0;

            int semicolonIndex = response.IndexOf(';');
            if (semicolonIndex > 0)
                response = response.Substring(0, semicolonIndex);

            if (response.Length >= 5)
            {
                string valueStr = response.Substring(3);
                if (int.TryParse(valueStr, out int value))
                    return value;
            }
            return 0;
        }

        // Returns null when the response is missing or malformed -- NOT zero.
        // These used to return 0 on every failure path, which made "the radio
        // did not answer" indistinguishable from "the SWR is 1.0:1". A single
        // dropped CAT response mid-over published a fabricated zero, and on a
        // genuinely bad load the meter dipped to perfect for one poll cycle
        // (issue #124). The caller skips the update when it gets null.
        public static int? ParseRm0LeftMeter(string response)
        {
            // Parse left-side meter from RM0 response: RM0LLLRRR;
            // Positions 3-5 are the left meter value (0-255).
            if (string.IsNullOrEmpty(response) || !response.StartsWith("RM0"))
                return null;
            int semicolonIndex = response.IndexOf(';');
            if (semicolonIndex > 0)
                response = response.Substring(0, semicolonIndex);
            if (response.Length >= 6)
            {
                if (int.TryParse(response.Substring(3, 3), out int value))
                    return value;
            }
            return null;
        }

        // Null on a missing or malformed response -- see ParseRm0LeftMeter.
        public static int? ParseRm0RightMeter(string response)
        {
            // Parse right-side meter from RM0 response: RM0LLLRRR;
            // Positions 6-8 are the right meter value (0-255).
            if (string.IsNullOrEmpty(response) || !response.StartsWith("RM0"))
                return null;
            int semicolonIndex = response.IndexOf(';');
            if (semicolonIndex > 0)
                response = response.Substring(0, semicolonIndex);
            if (response.Length >= 9)
            {
                if (int.TryParse(response.Substring(6, 3), out int value))
                    return value;
            }
            return null;
        }

        public static int ParseMeterReading(string response)
        {
            // Parse RM meter responses per FTdx101 manual
            // Format: RMP1P2P2P2P3P3P3; where:
            //   P1 = meter type (1 digit: 1=S-MAIN, 2=S-SUB, 3=COMP, 4=ALC, 5=PO, 6=SWR, etc.)
            //   P2 = left meter value (3 digits: 000-255)
            //   P3 = right meter value (3 digits: usually 000)
            // Example: RM5072000; means meter type 5 (power), value 072 (72 out of 255)
            if (string.IsNullOrEmpty(response) || !response.StartsWith("RM"))
                return 0;

            int semicolonIndex = response.IndexOf(';');
            if (semicolonIndex > 0)
                response = response.Substring(0, semicolonIndex);

            if (response.Length >= 6)
            {
                string valueStr = response.Substring(3, 3);
                if (int.TryParse(valueStr, out int value))
                    return value;
            }
            return 0;
        }

        // Initialization commands for the transceiver
        public static readonly string[] InitializationCommands = new[]
        {
            // Frequencies and VFO
            "FA;", "FB;", "FT;", "VS;",
            // Mode
            "MD0;", "MD1;",
            // Power and RF
            "PC;", "RG0;", "RG1;",
            // Antenna and roofing filter
            "AN0;", "AN1;", "RF0;", "RF1;",
            // AGC and IPO/AMP
            "GT0;", "GT1;", "PA0;", "PA1;",
            // Attenuator
            "RA0;", "RA1;",
            // AF and Mic gain
            "AG0;", "AG1;", "MG;",
            // Noise reduction and blanker
            "NR0;", "NR1;", "RL0;", "RL1;", "NB0;", "NB1;", "NL0;", "NL1;",
            // Notch (auto and manual)
            "CT0;", "CT1;", "ML0;", "ML1;",
            // DSP filter width and IF shift (future: DSP filter controls)
            "SH0;", "SH1;", "IS0;", "IS1;",
            // Beat cancel (future)
            "BC0;", "BC1;",
            // Speech processor
            "PR;", "PL;",
            // VOX
            "VX;", "VG;", "VD;",
            // FM repeater
            "RS;", "RO;", "CT;", "CN;",
            // Keyer speed and CW break-in
            "KS;", "BI;", "SD;",
            // Antenna tuner (future)
            "AC;",
            // Init completion signal — must be last
            "DT0;"
        };

        // P1=0-Fixed receive-control queries for single-receiver radios.
        // Used by the init ping-pong (temporarily switch VS to read the
        // inactive VFO's stored settings) and by the debounced post-VS
        // re-query burst after front-panel A/B presses.
        public static readonly string[] SingleReceiverPerVfoQueries =
        {
            // Mode is per-VFO at CAT (MD0=A, MD1=B) even on single-receiver.
            // Both are re-read after VS; the dispatcher routes by P1.
            "MD0;", "MD1;",
            "RF0;",          // roofing filter
            "GT0;",          // AGC
            "PA0;",          // IPO/AMP
            "RA0;",          // Attenuator
            "NR0;", "RL0;",  // NR + DNR level
            "NB0;", "NL0;",  // NB + NB level
            "BC0;",          // Auto Notch
            "BP00;", "BP01;",// Manual Notch on/off + freq
            "CO00;", "CO01;", "CO02;", "CO03;", // Contour + APF
            "SH0;",          // IF Width
            "IS0;",          // IF Shift
            "AG0;",          // AF Gain
            "RG0;",          // RF Gain
            "SQ0;",          // Squelch
        };
    }

    /// <summary>
    /// Frame construction for SS (SPECTRUM SCOPE) — the radio's OWN display,
    /// not YWC's SDR spectrum panel. See docs/design/scope-control-via-cat.md.
    ///
    /// Frame shape, confirmed against a real FTdx101MP rather than inferred from
    /// the CAT manual (whose table is mangled by the PDF layout):
    ///
    ///     Set / Answer   SS P1 P2 P3P4P5P6P7 ;    10 characters
    ///     Read           SS P1 P2 ;                5 characters
    ///
    /// P3-P7 is ONE five-character value field, not five one-character fields.
    /// LEVEL uses all five ("+05.0"); every other sub-command uses the first
    /// character and pads the rest with zeros. Reading SS04; on the bench
    /// answered SS04+05.0; which is what settles it.
    ///
    /// Everything here builds the padded field in one place on purpose. Writing
    /// the pad at each call site is exactly the kind of detail that gets
    /// miscounted, and the radio is tolerant enough of a malformed tail to hide
    /// the mistake — the write probe accidentally sent a NUL in the pad and the
    /// radio still applied the value.
    /// </summary>
    public static class ScopeCommands
    {
        public const string Opcode = "SS";

        // P2 sub-command selectors.
        public const char Speed  = '0';
        public const char Peak   = '1';
        public const char Marker = '2';
        public const char Color  = '3';
        public const char Level  = '4';
        public const char Span   = '5';
        public const char Mode   = '6';
        public const char AfFft  = '7';
        public const char Hold   = '8';   // absent on FT-710

        /// <summary>P1: 0 = MAIN scope, 1 = SUB scope. Fixed at 0 on every
        /// model except the FTdx101 family.</summary>
        public static char BandDigit(bool isSub) => isSub ? '1' : '0';

        /// <summary>Read frame, e.g. Read('0', Span) => "SS05;".</summary>
        public static string Read(char band, char subCommand) =>
            $"{Opcode}{band}{subCommand};";

        /// <summary>
        /// Set frame for the single-character sub-commands (everything except
        /// LEVEL), e.g. Set('0', Span, '4') => "SS0540000;".
        /// </summary>
        public static string Set(char band, char subCommand, char value) =>
            $"{Opcode}{band}{subCommand}{value}0000;";

        /// <summary>
        /// Set frame for AF-FFT / OSCILLOSCOPE (P2=7). Unlike the other
        /// single-character sub-commands, P3/P4/P5 are three independent axes
        /// packed into one field: FFT ATT, OSC ATT, OSC timebase. Writing any
        /// one of them with Set() would zero the other two, so they must go
        /// out together. P6-P7 are documented as fixed 0.
        ///
        /// P3 0/1/2 = AF-FFT ATT 0/10/20 dB
        /// P4 0/1/2 = OSC level ATT 0/10/20 dB
        /// P5 0-5   = OSC time 1/3/10/30/100/300 ms
        /// </summary>
        public static string SetAfFft(char band, char fftAtt, char oscAtt, char oscTime)
        {
            fftAtt  = ClampDigit(fftAtt,  '2');
            oscAtt  = ClampDigit(oscAtt,  '2');
            oscTime = ClampDigit(oscTime, '5');
            return $"{Opcode}{band}{AfFft}{fftAtt}{oscAtt}{oscTime}00;";
        }

        /// <summary>
        /// Unpacks the P2=7 five-character field into the three axes the UI
        /// exposes. "11200" => FFT ATT 10 dB, OSC ATT 10 dB, OSC 10 ms.
        /// Missing characters default to '0' rather than throwing — a short
        /// or null field is treated as unknown-but-harmless, same as Value().
        /// </summary>
        public static (char FftAtt, char OscAtt, char OscTime) ParseAfFft(string? field)
        {
            var f = (field ?? "00000").PadRight(5, '0');
            return (f[0], f[1], f[2]);
        }

        /// <summary>
        /// Set frame for COLOR (P2=3). P3/P4/P5 are independent axes packed into
        /// one field: scope colour, narrow-band colour, NB-colour on/off.
        /// Writing any one of them with Set() would zero the other two.
        ///
        /// P3 0–9/A = colour 1–11
        /// P4 0–6   = narrow-band colour 1–7
        /// P5 0/1   = narrow-band colour off/on
        /// </summary>
        public static string SetColor(char band, char color, char nbColor, char nbOn)
        {
            color   = ClampColor(color);
            nbColor = ClampDigit(nbColor, '6');
            nbOn    = ClampDigit(nbOn,    '1');
            return $"{Opcode}{band}{Color}{color}{nbColor}{nbOn}00;";
        }

        /// <summary>
        /// Unpacks the P2=3 five-character field. "41100" => colour 5, NB colour 2, NB on.
        /// </summary>
        public static (char Color, char NbColor, char NbOn) ParseColor(string? field)
        {
            var f = (field ?? "00000").PadRight(5, '0');
            return (char.ToUpperInvariant(f[0]), f[1], f[2]);
        }

        private static char ClampDigit(char value, char max)
        {
            if (value < '0') return '0';
            return value > max ? max : value;
        }

        private static char ClampColor(char value)
        {
            value = char.ToUpperInvariant(value);
            return value is >= '0' and <= '9' or 'A' ? value : '0';
        }

        /// <summary>
        /// Set frame for LEVEL, which is the one sub-command that uses the whole
        /// five-character field: -30.0 to +30.0 in 0.5 dB steps, always signed
        /// and always zero-padded to two integer digits ("+05.0", "-30.0").
        /// </summary>
        public static string SetLevel(char band, double db)
        {
            // Clamp then snap to the radio's 0.5 dB grid; a value off the grid
            // is not a rounding nicety, the radio rejects the frame.
            var clamped = Math.Clamp(db, -30.0, 30.0);
            var snapped = Math.Round(clamped * 2, MidpointRounding.AwayFromZero) / 2;
            var sign    = snapped < 0 ? '-' : '+';
            var field   = $"{sign}{Math.Abs(snapped):00.0}";
            return $"{Opcode}{band}{Level}{field};";
        }

        /// <summary>
        /// Extracts the five-character value field from an SS answer, or null if
        /// the answer is not the expected shape. "SS0540000;" => "40000".
        ///
        /// The terminator is optional on the way in. On the wire the radio always
        /// sends it, but CatMultiplexerService strips it before handing the
        /// answer back, so a parser that insists on it works perfectly against a
        /// raw serial probe and then returns null for everything in the actual
        /// app. It did exactly that once already.
        /// </summary>
        public static string? ValueField(string? answer, char band, char subCommand)
        {
            if (string.IsNullOrEmpty(answer)) return null;
            var a = answer.TrimEnd();
            if (a.EndsWith(';')) a = a[..^1];
            if (a.Length != 9) return null;
            if (a[0] != 'S' || a[1] != 'S') return null;
            if (a[2] != band || a[3] != subCommand) return null;
            return a.Substring(4, 5);
        }

        /// <summary>
        /// The P3 character of an SS answer — the value for every sub-command
        /// except LEVEL. Returns null if the answer did not parse.
        /// </summary>
        public static char? Value(string? answer, char band, char subCommand) =>
            ValueField(answer, band, subCommand) is { Length: > 0 } f ? f[0] : null;

        // ── MODE (P2=6) composition ──────────────────────────────────────────
        //
        // The twelve mode values are not an arbitrary list, they are a 2x3x3
        // grid, which is why the UI offers three small selectors rather than one
        // twelve-entry dropdown:
        //
        //   0,1,2  3DSS      CENTER / CURSOR / FIX          (no size variants)
        //   3,4,5  W/F       CENTER  x  L / N / S
        //   6,7,8  W/F       CURSOR  x  L / N / S
        //   9,A,B  W/F       FIX     x  L / N / S
        //
        // The FT-710 uses the same positions with only two sizes
        // (EXPAND / NORMAL) and documents the third slot of each group as "-",
        // i.e. it does not exist. Callers must therefore range-check `size`
        // against RadioCapabilities.ScopeSizeLabels for the model.

        public const int PlacementCenter = 0;
        public const int PlacementCursor = 1;
        public const int PlacementFix    = 2;

        /// <summary>
        /// Composes the P3 mode character from the three axes the UI exposes.
        /// 3DSS has no size variants, so <paramref name="size"/> is ignored when
        /// <paramref name="is3dss"/> is true.
        /// </summary>
        public static char ModeValue(bool is3dss, int placement, int size)
        {
            placement = Math.Clamp(placement, 0, 2);
            if (is3dss) return (char)('0' + placement);

            var index = 3 + placement * 3 + Math.Clamp(size, 0, 2);
            // 10 and 11 are 'A' and 'B', not '10' and '11'.
            return index < 10 ? (char)('0' + index) : (char)('A' + index - 10);
        }

        /// <summary>Decomposes a P3 mode character back into the three axes.</summary>
        public static (bool Is3dss, int Placement, int Size) ParseMode(char value)
        {
            var index = value switch
            {
                >= '0' and <= '9' => value - '0',
                >= 'A' and <= 'B' => value - 'A' + 10,
                >= 'a' and <= 'b' => value - 'a' + 10,
                _                 => 0
            };
            if (index < 3) return (true, index, 0);
            var offset = index - 3;
            return (false, offset / 3, offset % 3);
        }
    }

    public static class IFCommandParser
    {
        public static (long frequency, string mode) ParseIFResponse(string response)
        {
            if (string.IsNullOrEmpty(response) || response.Length < 20 || !response.StartsWith("IF"))
            {
                // Invalid response; return default
                return (0, "UNKNOWN");
            }

            try
            {
                string freqStr = response.Substring(5, 9);
                long frequency = long.Parse(freqStr);
                string mode = "USB";
                return (frequency, mode);
            }
            catch (Exception)
            {
                // Parsing failed; return default
                return (0, "UNKNOWN");
            }
        }
    }
}