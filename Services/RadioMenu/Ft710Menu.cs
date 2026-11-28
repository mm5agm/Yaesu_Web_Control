using static Yaesu_Web_Control.Services.RadioMenu.Ftdx101Menu;

namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>
/// The FT-710 menu as the EX command sees it: Table 2 (MENU Chart) of the
/// FT-710 CAT manual, 2306-C, pages 9-12. Built the same way as
/// <see cref="Ftdx10Menu"/> and with the FTdx101's decoders.
///
/// The FT-710 has USB audio, so each mode group has USB and REAR OUT LEVEL,
/// MOD SOURCE (MIC/USB/REAR/AUTO), USB and REAR MOD GAIN, and a NAR WIDTH.
/// AF TREBLE/MIDDLE/BASS GAIN go from -20 to +10. It has three CAT ports,
/// a TUN/LIN port, MIC key assignments and five PRESETs (EXTENSION SETTING,
/// P1 = 06 in the chart) that hold the settings for FT8 and other data work.
///
/// The CAT manual repeats the FTdx101 manual's misprints (MARK FREQUENCY
/// "1: 1275 2: 2125", SHIFT FREQUENCY "1: 170 1: 200"); those are taken as
/// 0-based, as they are on the FTdx101.
///
/// Not yet read back on an FT-710. The raw value is saved beside each
/// decoded one, so a wrong guess shows up in the file.
/// </summary>
public static class Ft710Menu
{
    /// <summary>NAR WIDTH in SSB, 00-22.</summary>
    private static readonly string[] SsbNarrow =
    [
        "300", "400", "600", "850", "1100", "1200", "1500", "1650", "1800", "1950", "2100", "2250",
        "2400", "2450", "2500", "2600", "2700", "2800", "2900", "3000", "3200", "3500", "4000",
    ];

    /// <summary>NAR WIDTH in CW, RTTY and DATA, 00-20.</summary>
    private static readonly string[] CwNarrow =
    [
        "50", "100", "150", "200", "250", "300", "350", "400", "450", "500", "600",
        "800", "1200", "1400", "1700", "2000", "2400", "3000", "3200", "3500", "4000",
    ];

    /// <summary>What a MIC key can be set to, 00-20.</summary>
    private static readonly string[] MicKeys =
    [
        "LOCK", "QMB", "A/B", "V/M", "TUNER", "VOX/MOX", "MODE", "ZIN/SPOT", "SPLIT", "FINE", "NAR",
        "NB", "DNR", "FREQ UP", "FREQ DOWN", "BAND UP", "BAND DOWN", "ATT", "IPO", "DNF", "AGC",
    ];

    // After the arrays above: static fields are set in order, and Build uses them.
    public static readonly IReadOnlyList<RadioMenuItem> Items = Build();

    private static List<RadioMenuItem> Build()
    {
        var items = new List<RadioMenuItem>();
        string section = "", group = "";
        string p1 = "", p2 = "";

        void Section(string code, string name) { p1 = code; section = name; }
        void Group(string code, string name) { p2 = code; group = name; }
        void Item(int p3, string function, Kind kind, bool tx = false, bool link = false)
            => items.Add(new RadioMenuItem($"{p1}{p2}{p3:D2}", section, group, function, kind.Decode, tx,
                kind.Values, kind.TextMax, link));

        var hz = (string[] list) => Choice(list.Select(w => w + " Hz").ToArray());
        var txBpf = Choice("50-3050", "100-2900", "200-2800", "300-2700", "400-2600");
        var modSource = Choice("MIC", "USB", "REAR", "AUTO");
        var rptt = Choice("OFF", "RTS", "DTR", "DAKY");
        var norRev = Choice("NOR", "REV");
        var offOn = Choice("OFF", "ON");
        var keyer = Choice("OFF", "BUG", "ELEKEY-A", "ELEKEY-B", "ELEKEY-Y", "ACS");
        var baud = Choice("4800 bps", "9600 bps", "19200 bps", "38400 bps", "115200 bps");
        var timeout = Choice("10 ms", "100 ms", "1000 ms", "3000 ms");
        var stopBit = Choice("1 bit", "2 bit");
        var slope = Choice("6 dB/oct", "18 dB/oct");
        var agcDelay = Units("ms", 20, 4000, 20);

        // Every mode group starts with the AF tone controls, the AGC delays and the cuts.
        void ToneAgcCuts()
        {
            Item(1, "AF TREBLE GAIN", Units("", -20, 10));
            Item(2, "AF MIDDLE TONE GAIN", Units("", -20, 10));
            Item(3, "AF BASS GAIN", Units("", -20, 10));
            Item(4, "AGC FAST DELAY", agcDelay);
            Item(5, "AGC MID DELAY", agcDelay);
            Item(6, "AGC SLOW DELAY", agcDelay);
            Item(7, "LCUT FREQ", Steps(100, 50, 1000, "Hz"));
            Item(8, "LCUT SLOPE", slope);
            Item(9, "HCUT FREQ", Steps(700, 50, 4000, "Hz"));
            Item(10, "HCUT SLOPE", slope);
            Item(11, "USB OUT LEVEL", Units("", 0, 100));
            Item(12, "REAR OUT LEVEL", Units("", 0, 100));
        }
        // TX BPF SEL (where the mode has it), MOD SOURCE, the two MOD GAINs and RPTT SELECT.
        void TxAudio(int first, bool bpf)
        {
            if (bpf) Item(first++, "TX BPF SEL", txBpf, tx: true);
            Item(first, "MOD SOURCE", modSource, tx: true);
            Item(first + 1, "USB MOD GAIN", Units("", 0, 100), tx: true);
            Item(first + 2, "REAR MOD GAIN", Units("", 0, 100), tx: true);
            Item(first + 3, "RPTT SELECT", rptt, tx: true);
        }

        Section("01", "RADIO SETTING");

        Group("01", "MODE SSB");
        ToneAgcCuts();
        TxAudio(13, bpf: true);
        Item(18, "NAR WIDTH", hz(SsbNarrow));
        Item(19, "CW AUTO MODE", Choice("OFF", "50 MHz", "ON"), tx: true);

        Group("02", "MODE AM");
        ToneAgcCuts();
        TxAudio(13, bpf: true);

        Group("03", "MODE FM");
        ToneAgcCuts();
        TxAudio(13, bpf: false);
        Item(17, "RPT SHIFT", Choice("-", "SIMPLEX", "+"), tx: true);
        Item(18, "RPT SHIFT (28MHz)", Units("kHz", 0, 1000, 10), tx: true);
        Item(19, "RPT SHIFT (50MHz)", Units("kHz", 0, 4000, 10), tx: true);
        Item(20, "ENC/DEC", Choice("OFF", "ENC", "TSQ"), tx: true);
        Item(21, "TONE FREQ", Choice(Ftdx10Menu.CtcssTones.Select(t => t + " Hz").ToArray()), tx: true);

        Group("04", "MODE PSK/DATA");
        ToneAgcCuts();
        TxAudio(13, bpf: true);
        Item(18, "NAR WIDTH", hz(CwNarrow));
        Item(19, "PSK TONE", Choice("1000 Hz", "1500 Hz", "2000 Hz"));
        Item(20, "DATA SHIFT (SSB)", Units("Hz", 0, 3000, 10));

        Group("05", "MODE RTTY");
        ToneAgcCuts();
        Item(13, "RPTT SELECT", rptt, tx: true);
        Item(14, "NAR WIDTH", hz(CwNarrow));
        Item(15, "MARK FREQUENCY", Choice("1275 Hz", "2125 Hz"));
        Item(16, "SHIFT FREQUENCY", Choice("170 Hz", "200 Hz", "425 Hz", "850 Hz"));
        Item(17, "POLARITY-TX", norRev, tx: true);

        Section("02", "CW SETTING");

        Group("01", "MODE CW");
        ToneAgcCuts();
        Item(13, "RPTT SELECT", rptt, tx: true);
        Item(14, "NAR WIDTH", hz(CwNarrow));
        Item(15, "PC KEYING", Choice("OFF", "RTS", "DTR", "DAKY"), tx: true);
        Item(16, "CW BK-IN TYPE", Choice("SEMI", "FULL"), tx: true);
        Item(17, "CW WAVE SHAPE", Choice("4 ms", "6 ms", "8 ms"), tx: true);
        Item(18, "CW FREQ DISPLAY", Choice("DIRECT FREQ", "PITCH OFFSET"));
        Item(19, "QSK DELAY TIME", Choice("15 ms", "20 ms", "25 ms", "30 ms"), tx: true);
        Item(20, "CW INDICATOR", offOn);

        Group("02", "KEYER");
        Item(1, "KEYER TYPE", keyer, tx: true);
        Item(2, "KEYER DOT/DASH", norRev, tx: true);
        Item(3, "CW WEIGHT", Tenths, tx: true);
        Item(4, "NUMBER STYLE", Choice("1290", "AUNO", "AUNT", "A2NO", "A2NT", "12NO", "12NT"));
        Item(5, "CONTEST NUMBER", Units("", 1, 9999));
        Item(6, "CW MEMORY 1", Choice("TEXT", "MESSAGE"));
        Item(7, "CW MEMORY 2", Choice("TEXT", "MESSAGE"));
        Item(8, "CW MEMORY 3", Choice("TEXT", "MESSAGE"));
        Item(9, "CW MEMORY 4", Choice("TEXT", "MESSAGE"));
        Item(10, "CW MEMORY 5", Choice("TEXT", "MESSAGE"));
        Item(11, "REPEAT INTERVAL", Units("s", 1, 60));

        Section("03", "OPERATION SETTING");

        Group("01", "GENERAL");
        Item(1, "BEEP LEVEL", Units("", 0, 100));
        Item(2, "RF/SQL VR", Choice("RF", "SQL", "SQL (FM only)"));
        Item(3, "TUN/LIN PORT SELECT", Choice("EXT-TUNER", "LINEAR", "CAT-3", "GPO"), tx: true);
        Item(4, "TUNER TYPE SELECT", Choice("INT", "INT (FAST)", "EXT", "ATAS"), tx: true);
        Item(5, "CAT-1 RATE", baud, link: true);
        Item(6, "CAT-1 TIME OUT TIMER", timeout, link: true);
        Item(7, "CAT-1 CAT-3 STOP BIT", stopBit, link: true);
        Item(8, "CAT-2 RATE", baud, link: true);
        Item(9, "CAT-2 TIME OUT TIMER", timeout, link: true);
        Item(10, "CAT-3 RATE", baud, link: true);
        Item(11, "CAT-3 TIME OUT TIMER", timeout, link: true);
        Item(12, "QMB CH", Choice("5ch", "10ch"));
        Item(13, "BAND STACK", offOn);
        Item(14, "MEM GROUP", offOn);
        Item(15, "TX TIME OUT TIMER", OffOrUnits("min", 30), tx: true);
        Item(16, "MIC SCAN", offOn);
        Item(17, "MIC SCAN RESUME", Choice("PAUSE", "TIME"));
        Item(18, "REF FREQ ADJ", Units("", -25, 25));
        Item(19, "KEYBOARD LANGUAGE", Choice("JAPANESE", "ENGLISH(US)", "ENGLISH(UK)", "FRENCH",
            "FRENCH(CA)", "GERMAN", "PORTUGUESE", "PORTUGUESE(BR)", "SPANISH", "SPANISH(LATAM)", "ITALIAN"));
        Item(20, "MIC P1", Choice(MicKeys));
        Item(21, "MIC P2", Choice(MicKeys));
        Item(22, "MIC P3", Choice(MicKeys));
        Item(23, "MIC P4", Choice(MicKeys));
        Item(24, "MIC UP", Choice(MicKeys));
        Item(25, "MIC DOWN", Choice(MicKeys));
        Item(26, "SCU-LAN10", offOn);

        Group("02", "RX DSP");
        Item(1, "IF NOTCH WIDTH", Choice("NARROW", "WIDE"));
        Item(2, "NB REJECTION", Choice("LOW", "MID", "HIGH"));
        Item(3, "NB WIDTH", Choice("NARROW", "MEDIUM", "WIDE"));
        Item(4, "APF WIDTH", Choice("NARROW", "MEDIUM", "WIDE"));
        Item(5, "CONTOUR LEVEL", Units("", -40, 20));
        Item(6, "CONTOUR WIDTH", Units("", 1, 11));

        Group("03", "TX AUDIO");
        Item(1, "AMC RELEASE TIME", Choice("FAST", "MID", "SLOW"), tx: true);
        foreach (var (first, prefix) in new[] { (2, "PRMTRC"), (11, "P PRMTRC") })
        {
            Item(first, $"{prefix} EQ1 FREQ", Steps(100, 100, 700, "Hz"), tx: true);
            Item(first + 1, $"{prefix} EQ1 LEVEL", Units("dB", -20, 10), tx: true);
            Item(first + 2, $"{prefix} EQ1 BWTH", Units("", 0, 10), tx: true);
            Item(first + 3, $"{prefix} EQ2 FREQ", Steps(700, 100, 1500, "Hz"), tx: true);
            Item(first + 4, $"{prefix} EQ2 LEVEL", Units("dB", -20, 10), tx: true);
            Item(first + 5, $"{prefix} EQ2 BWTH", Units("", 0, 10), tx: true);
            Item(first + 6, $"{prefix} EQ3 FREQ", Steps(1500, 100, 3200, "Hz"), tx: true);
            Item(first + 7, $"{prefix} EQ3 LEVEL", Units("dB", -20, 10), tx: true);
            Item(first + 8, $"{prefix} EQ3 BWTH", Units("", 0, 10), tx: true);
        }

        Group("04", "TX GENERAL");
        Item(1, "HF MAX POWER", Units("W", 5, 100), tx: true);
        Item(2, "50M MAX POWER", Units("W", 5, 100), tx: true);
        Item(3, "70M MAX POWER", Units("W", 5, 50), tx: true);
        Item(4, "AM MAX POWER", Units("W", 5, 25), tx: true);
        Item(5, "VOX SELECT", Choice("MIC", "USB", "REAR"), tx: true);
        Item(6, "EMERGENCY FREQ TX", offOn, tx: true);
        Item(7, "TX INHIBIT", offOn, tx: true);
        Item(8, "METER DETECTOR", Choice("AVERAGE", "PEAK"));

        Group("05", "TUNING");
        Item(1, "SSB/CW DIAL STEP", Choice("5 Hz", "10 Hz", "20 Hz"));
        Item(2, "RTTY/PSK DIAL STEP", Choice("5 Hz", "10 Hz", "20 Hz"));
        Item(3, "CH STEP", Choice("1 kHz", "2.5 kHz", "5 kHz", "10 kHz"));
        Item(4, "AM CH STEP", Choice("2.5 kHz", "5 kHz", "9 kHz", "10 kHz", "12.5 kHz", "25 kHz"));
        Item(5, "FM CH STEP", Choice("5 kHz", "6.25 kHz", "10 kHz", "12.5 kHz", "20 kHz", "25 kHz"));
        Item(6, "MAIN STEPS PER REV.", Choice("50", "100", "200"));

        Section("04", "DISPLAY SETTING");

        Group("01", "DISPLAY");
        Item(1, "MY CALL", Text(12));
        Item(2, "MY CALL TIME", Choice("OFF", "1 s", "2 s", "3 s", "4 s", "5 s"));
        Item(3, "POP-UP TIME", Choice("FAST", "MID", "SLOW"));
        Item(4, "SCREEN SAVER", Choice("OFF", "15 min", "30 min", "60 min"));
        Item(5, "DIMMER LED", Units("", 0, 20));
        Item(6, "MOUSE POINTER SPEED", Units("", 0, 20));

        Group("02", "SCOPE");
        Item(1, "RBW", Choice("HIGH", "MID", "LOW"));
        Item(2, "SCOPE CTR", Choice("FILTER", "CARRIER POINT"));
        Item(3, "2D DISP SENSITIVITY", Choice("NORMAL", "HI"));
        Item(4, "3DSS DISP SENSITIVITY", Choice("NORMAL", "HI"));

        Group("03", "VFO IND COLOR");
        var vmi = Choice("BLUE", "GREEN", "WHITE", "NONE");
        Item(1, "VMI COLOR VFO-A", vmi);
        Item(2, "VMI COLOR VFO-B", vmi);
        Item(3, "VMI COLOR MEMORY", vmi);
        Item(4, "VMI COLOR CLAR", Choice("RED", "NONE"));

        Group("04", "EXT MONITOR");
        Item(1, "EXT DISPLAY", offOn);
        Item(2, "PIXEL", Choice("800x480", "800x600"));

        Section("06", "EXTENSION SETTING");

        for (int n = 1; n <= 5; n++)
        {
            Group($"{n:D2}", $"PRESET{n}");
            Item(1, "PRESET NAME", Text(12));
            Item(2, "CAT-1 RATE", baud, link: true);
            Item(3, "CAT-1 TIME OUT TIMER", timeout, link: true);
            Item(4, "CAT-1 CAT-3 STOP BIT", stopBit, link: true);
            Item(5, "AGC FAST DELAY", agcDelay);
            Item(6, "AGC MID DELAY", agcDelay);
            Item(7, "AGC SLOW DELAY", agcDelay);
            Item(8, "LCUT FREQ", Steps(100, 50, 1000, "Hz"));
            Item(9, "LCUT SLOPE", slope);
            Item(10, "HCUT FREQ", Steps(700, 50, 4000, "Hz"));
            Item(11, "HCUT SLOPE", slope);
            Item(12, "USB OUT LEVEL", Units("", 0, 100));
            Item(13, "REAR OUT LEVEL", Units("", 0, 100));
            Item(14, "TX BPF SEL", txBpf, tx: true);
            Item(15, "MOD SOURCE", modSource, tx: true);
            Item(16, "USB MOD GAIN", Units("", 0, 100), tx: true);
            Item(17, "REAR MOD GAIN", Units("", 0, 100), tx: true);
            Item(18, "RPTT SELECT", rptt, tx: true);
        }

        return items;
    }
}
