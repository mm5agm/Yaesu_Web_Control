namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Represents the persistent state of the radio (both receivers A and B).
    /// This state is saved to radio_state.json and can be manually edited.
    /// </summary>
    public class RadioState
    {
        /// <summary>
        /// Receiver A frequency in Hz (e.g., 14074000 for 14.074 MHz)
        /// </summary>
        public long FrequencyA { get; set; }

        /// <summary>
        /// Receiver B frequency in Hz (e.g., 7074000 for 7.074 MHz)
        /// </summary>
        public long FrequencyB { get; set; }

        /// <summary>
        /// Receiver A band (e.g., "20m", "40m", "80m")
        /// </summary>
        public string BandA { get; set; } = "";

        /// <summary>
        /// Receiver B band (e.g., "20m", "40m", "80m")
        /// </summary>
        public string BandB { get; set; } = "";

        /// <summary>
        /// Receiver A mode (e.g., "USB", "LSB", "CW", "FM", "AM", "DATA-USB")
        /// </summary>
        public string ModeA { get; set; } = "";

        /// <summary>
        /// Receiver B mode (e.g., "USB", "LSB", "CW", "FM", "AM", "DATA-USB")
        /// </summary>
        public string ModeB { get; set; } = "";

        /// <summary>
        /// Receiver A antenna selection (1, 2, or 3)
        /// </summary>
        public string AntennaA { get; set; } = "";

        /// <summary>
        /// Receiver B antenna selection (1, 2, or 3)
        /// </summary>
        public string AntennaB { get; set; } = "";

        /// <summary>
        /// Transmit power level (0-100/200)
        /// </summary>
        public int Power { get; set; } = 0;

        /// <summary>
        /// Receiver A AF Gain (0-255)
        /// </summary>
        public int AfGainA { get; set; } = 0;

        /// <summary>
        /// Receiver B AF Gain (0-255)
        /// </summary>
        public int AfGainB { get; set; } = 0;

        /// <summary>
        /// MIC Gain (0-100)
        /// </summary>
        public int MicGain { get; set; } = 50;

        /// <summary>
        /// Speech Processor enabled
        /// </summary>
        public bool ProcEnabled { get; set; } = false;

        /// <summary>
        /// Speech Processor level (0-100)
        /// </summary>
        public int ProcLevel { get; set; } = 50;

        /// <summary>
        /// Receiver A roofing filter code (6=12kHz, 7=3kHz, 8=1.2kHz, 9=600Hz, A=300Hz)
        /// </summary>
        public string RoofingFilterA { get; set; } = "";

        /// <summary>
        /// Receiver B roofing filter code (6=12kHz, 7=3kHz, 8=1.2kHz, 9=600Hz, A=300Hz)
        /// </summary>
        public string RoofingFilterB { get; set; } = "";

        /// <summary>
        /// AGC setting per VFO: "0"=OFF "1"=FAST "2"=MID "3"=SLOW "4"=AUTO
        /// </summary>
        public string AgcA { get; set; } = "2";
        public string AgcB { get; set; } = "2";

        /// <summary>
        /// IPO/AMP per VFO: "0"=IPO "1"=AMP1 "2"=AMP2
        /// </summary>
        public string IpoA { get; set; } = "0";
        public string IpoB { get; set; } = "0";

        /// <summary>
        /// Attenuator per VFO: "00"=OFF "06"=6dB "12"=12dB "18"=18dB
        /// </summary>
        public string AttA { get; set; } = "00";
        public string AttB { get; set; } = "00";

        /// <summary>
        /// Digital Noise Reduction (NR command) per VFO: "0"=OFF "1"=ON
        /// </summary>
        public string NrA { get; set; } = "0";
        public string NrB { get; set; } = "0";

        /// <summary>
        /// Auto Notch per VFO: "0"=OFF "1"=ON
        /// </summary>
        public string AutoNotchA { get; set; } = "0";
        public string AutoNotchB { get; set; } = "0";

        /// <summary>
        /// Manual Notch per VFO: "0"=OFF "1"=ON
        /// </summary>
        public string ManualNotchA { get; set; } = "0";
        public string ManualNotchB { get; set; } = "0";

        /// <summary>
        /// IF Width per VFO: "0"=200Hz "1"=400Hz "2"=600Hz "3"=850Hz "4"=1200Hz
        ///                   "5"=1400Hz "6"=1800Hz "7"=2400Hz "8"=3000Hz
        /// </summary>
        public string IfWidthA { get; set; } = "8";
        public string IfWidthB { get; set; } = "8";

        /// <summary>
        /// IF Shift per VFO in Hz (-1000 to +1000)
        /// </summary>
        public int IfShiftA { get; set; } = 0;
        public int IfShiftB { get; set; } = 0;

        /// <summary>
        /// Clarifier (RIT/XIT) offset per VFO in Hz (-9990 to +9990)
        /// </summary>
        public int ClarifierOffsetA { get; set; } = 0;
        public int ClarifierOffsetB { get; set; } = 0;

        /// <summary>
        /// Contour filter on/off per VFO
        /// </summary>
        public bool ContourOnA { get; set; } = false;
        public bool ContourOnB { get; set; } = false;

        /// <summary>
        /// Contour filter frequency in Hz per VFO (100–3200 Hz FTdx101/10/710; 100–4000 Hz FTDX3000)
        /// </summary>
        public int ContourFreqA { get; set; } = 800;
        public int ContourFreqB { get; set; } = 800;

        /// <summary>
        /// APF (Audio Peak Filter) on/off per VFO
        /// </summary>
        public bool ApfOnA { get; set; } = false;
        public bool ApfOnB { get; set; } = false;

        /// <summary>
        /// APF frequency offset in Hz per VFO (-250 to +250 Hz)
        /// </summary>
        public int ApfFreqA { get; set; } = 0;
        public int ApfFreqB { get; set; } = 0;

        /// <summary>
        /// Additional controls and settings (for future expansion)
        /// </summary>
        public Dictionary<string, object> Controls { get; set; } = new();
    }
}