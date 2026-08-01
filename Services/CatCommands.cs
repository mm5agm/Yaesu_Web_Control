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
        public const string SetMetersCompAndSWR = "MS13"; // Select Compression(left) + SWR(right) for RM0 read
        public const string SetMeterPower       = "MS01"; // Restore the default Power meter on shutdown so the user's radio is left in a normal state
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

        public static int ParseRm0LeftMeter(string response)
        {
            // Parse left-side meter from RM0 response: RM0LLLRRR;
            // Positions 3-5 are the left meter value (0-255).
            if (string.IsNullOrEmpty(response) || !response.StartsWith("RM0"))
                return 0;
            int semicolonIndex = response.IndexOf(';');
            if (semicolonIndex > 0)
                response = response.Substring(0, semicolonIndex);
            if (response.Length >= 6)
            {
                if (int.TryParse(response.Substring(3, 3), out int value))
                    return value;
            }
            return 0;
        }

        public static int ParseRm0RightMeter(string response)
        {
            // Parse right-side meter from RM0 response: RM0LLLRRR;
            // Positions 6-8 are the right meter value (0-255).
            if (string.IsNullOrEmpty(response) || !response.StartsWith("RM0"))
                return 0;
            int semicolonIndex = response.IndexOf(';');
            if (semicolonIndex > 0)
                response = response.Substring(0, semicolonIndex);
            if (response.Length >= 9)
            {
                if (int.TryParse(response.Substring(6, 3), out int value))
                    return value;
            }
            return 0;
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
            "MD0;",          // mode — per-VFO at CAT level; re-read after VS for safety
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