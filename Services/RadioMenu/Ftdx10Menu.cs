using static Yaesu_Web_Control.Services.RadioMenu.Ftdx101Menu;

namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>
/// The FTDX10 menu as the EX command sees it: Table 2 (MENU Chart) of the
/// FTDX10 CAT manual, 2308-F, pages 9-11. Built the same way as
/// <see cref="Ftdx101Menu"/> and with its decoders.
///
/// The FTDX10 has one receiver, so there are no OUT SELECT (MAIN/SUB) items,
/// no DECODE RX SELECT, HEADPHONE MIX or ANT3 SELECT, and one keyer, not
/// front and rear. It adds AF TREBLE/MIDDLE/BASS GAIN at the top of each
/// mode group, FM repeater and tone items, and SPLIT FREQ DISPLAY. It has
/// no CW BK-IN DELAY, CS DIAL, DNR LEVEL, PROC TYPE, TFT CONTRAST, DIMMER TFT
/// or FREQ STYLE menu items.
///
/// The CAT manual repeats the FTdx101 manual's misprints (MARK FREQUENCY
/// "1: 1275 2: 2125", SHIFT FREQUENCY "1: 170 1: 200", DECODE AFC RANGE
/// "1: 1.5"); those are taken as 0-based, as they are on the FTdx101.
/// CW WAVE SHAPE and MY CALL TIME follow the FTDX10 operating manual
/// (2307A: 4/6/8 ms and OFF/1-5 s), which the FTdx101 radio sided with.
/// KEYBOARD LANGUAGE has the operating manual's eleven languages; the CAT
/// manual's "11: LEVEL" there is left over from the FTdx101's CS DIAL list.
///
/// Not yet read back on an FTDX10. The raw value is saved beside each
/// decoded one, so a wrong guess shows up in the file.
/// </summary>
public static class Ftdx10Menu
{
    /// <summary>The 50 standard CTCSS tones, TONE FREQ 00-49.</summary>
    internal static readonly string[] CtcssTones =
    [
        "67.0", "69.3", "71.9", "74.4", "77.0", "79.7", "82.5", "85.4", "88.5", "91.5",
        "94.8", "97.4", "100.0", "103.5", "107.2", "110.9", "114.8", "118.8", "123.0", "127.3",
        "131.8", "136.5", "141.3", "146.2", "151.4", "156.7", "159.8", "162.2", "165.5", "167.9",
        "171.3", "173.8", "177.3", "179.9", "183.5", "186.2", "189.9", "192.8", "196.6", "199.5",
        "203.5", "206.5", "210.7", "218.1", "225.7", "229.1", "233.6", "241.8", "250.3", "254.1",
    ];

    // After CtcssTones: static fields are set in order, and Build uses them.
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

        // Every mode group starts with the AF tone controls and the AGC delays.
        void ToneAndAgc()
        {
            Item(1, "AF TREBLE GAIN", Units("", -10, 10));
            Item(2, "AF MIDDLE TONE GAIN", Units("", -10, 10));
            Item(3, "AF BASS GAIN", Units("", -10, 10));
            Item(4, "AGC FAST DELAY", Units("ms", 20, 4000, 20));
            Item(5, "AGC MID DELAY", Units("ms", 20, 4000, 20));
            Item(6, "AGC SLOW DELAY", Units("ms", 20, 4000, 20));
        }
        void Cuts(int first)
        {
            Item(first, "LCUT FREQ", Steps(100, 50, 1000, "Hz"));
            Item(first + 1, "LCUT SLOPE", Choice("6 dB/oct", "18 dB/oct"));
            Item(first + 2, "HCUT FREQ", Steps(700, 50, 4000, "Hz"));
            Item(first + 3, "HCUT SLOPE", Choice("6 dB/oct", "18 dB/oct"));
        }
        var txBpf = Choice("50-3050", "100-2900", "200-2800", "300-2700", "400-2600");
        var micRear = Choice("MIC", "REAR");
        var dataUsb = Choice("DATA", "USB");
        var rptt = Choice("DAKY", "RTS", "DTR");
        var norRev = Choice("NOR", "REV");
        var offOn = Choice("OFF", "ON");
        var disEn = Choice("DISABLE", "ENABLE");
        var keyer = Choice("OFF", "BUG", "ELEKEY-A", "ELEKEY-B", "ELEKEY-Y", "ACS");
        var baud = Choice("4800 bps", "9600 bps", "19200 bps", "38400 bps");
        var timeout = Choice("10 ms", "100 ms", "1000 ms", "3000 ms");

        Section("01", "RADIO SETTING");

        Group("01", "MODE SSB");
        ToneAndAgc();
        Cuts(7);
        Item(11, "SSB OUT LEVEL", Units("", 0, 100));
        Item(12, "TX BPF SEL", txBpf, tx: true);
        Item(13, "SSB MOD SOURCE", micRear, tx: true);
        Item(14, "REAR SELECT", dataUsb, tx: true);
        Item(15, "RPORT GAIN", Units("", 0, 100), tx: true);
        Item(16, "RPTT SELECT", rptt, tx: true);

        Group("02", "MODE AM");
        ToneAndAgc();
        Cuts(7);
        Item(11, "AM OUT LEVEL", Units("", 0, 100));
        Item(12, "TX BPF SEL", txBpf, tx: true);
        Item(13, "AM MOD SOURCE", micRear, tx: true);
        Item(14, "MIC GAIN", MicGain, tx: true);
        Item(15, "REAR SELECT", dataUsb, tx: true);
        Item(16, "RPORT GAIN", Units("", 0, 100), tx: true);
        Item(17, "RPTT SELECT", rptt, tx: true);

        Group("03", "MODE FM");
        ToneAndAgc();
        Cuts(7);
        Item(11, "FM OUT LEVEL", Units("", 0, 100));
        Item(12, "FM MOD SOURCE", micRear, tx: true);
        Item(13, "MIC GAIN", MicGain, tx: true);
        Item(14, "REAR SELECT", dataUsb, tx: true);
        Item(15, "RPORT GAIN", Units("", 0, 100), tx: true);
        Item(16, "RPTT SELECT", rptt, tx: true);
        Item(17, "RPT SHIFT (28MHz)", Units("kHz", 0, 1000, 10), tx: true);
        Item(18, "RPT SHIFT (50MHz)", Units("kHz", 0, 4000, 10), tx: true);
        Item(19, "RPT", Choice("-", "SIMPLEX", "+"), tx: true);
        Item(20, "TONE FREQ", Choice(CtcssTones.Select(t => t + " Hz").ToArray()), tx: true);
        Item(21, "ENC/DEC", Choice("OFF", "ENC", "TSQ"), tx: true);

        Group("04", "MODE PSK/DATA");
        ToneAndAgc();
        Item(7, "PSK TONE", Choice("1000 Hz", "1500 Hz", "2000 Hz"));
        Item(8, "DATA SHIFT (SSB)", Units("Hz", 0, 3000, 10));
        Cuts(9);
        Item(13, "DATA OUT LEVEL", Units("", 0, 100));
        Item(14, "TX BPF SEL", txBpf, tx: true);
        Item(15, "DATA MOD SOURCE", micRear, tx: true);
        Item(16, "REAR SELECT", dataUsb, tx: true);
        Item(17, "RPORT GAIN", Units("", 0, 100), tx: true);
        Item(18, "RPTT SELECT", rptt, tx: true);

        Group("05", "MODE RTTY");
        ToneAndAgc();
        Item(7, "POLARITY RX", norRev);
        Item(8, "POLARITY TX", norRev, tx: true);
        Cuts(9);
        Item(13, "RTTY OUT LEVEL", Units("", 0, 100));
        Item(14, "SHIFT PORT", Choice("SHIFT", "DTR", "RTS"), tx: true);
        Item(15, "MARK FREQUENCY", Choice("1275 Hz", "2125 Hz"));
        Item(16, "SHIFT FREQUENCY", Choice("170 Hz", "200 Hz", "425 Hz", "850 Hz"));

        Group("06", "ENC/DEC PSK");
        Item(1, "PSK MODE", Choice("BPSK", "QPSK"));
        Item(2, "DECODE AFC RANGE", Choice("8 Hz", "15 Hz", "30 Hz"));
        Item(3, "QPSK POLARITY RX", norRev);
        Item(4, "QPSK POLARITY TX", norRev, tx: true);
        Item(5, "PSK TX LEVEL", Units("", 0, 100), tx: true);

        Group("07", "ENC/DEC RTTY");
        Item(1, "RX USOS", disEn);
        Item(2, "TX USOS", disEn, tx: true);
        Item(3, "RX NEW LINE CODE", Choice("CR or LF or CR+LF", "CR, LF, CR+LF"));
        Item(4, "TX AUTO CR+LF", disEn, tx: true);
        Item(5, "TX DIDDLE", Choice("OFF", "BLANK", "LTRS"), tx: true);
        Item(6, "BAUDOT CODE", Choice("CCIT", "US"));

        Section("02", "CW SETTING");

        Group("01", "MODE CW");
        ToneAndAgc();
        Cuts(7);
        Item(11, "CW OUT LEVEL", Units("", 0, 100));
        Item(12, "CW AUTO MODE", Choice("OFF", "50 MHz", "ON"), tx: true);
        Item(13, "CW BK-IN TYPE", Choice("SEMI", "FULL"), tx: true);
        Item(14, "CW WAVE SHAPE", Choice("4 ms", "6 ms", "8 ms"), tx: true);
        Item(15, "CW FREQ DISPLAY", Choice("DIRECT FREQ", "PITCH OFFSET"));
        Item(16, "PC KEYING", Choice("OFF", "DAKY", "RTS", "DTR"), tx: true);
        Item(17, "QSK DELAY TIME", Choice("15 ms", "20 ms", "25 ms", "30 ms"), tx: true);
        Item(18, "CW INDICATOR", offOn);

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

        Group("03", "DECODE CW");
        Item(1, "CW DECODE BW", Choice("25 Hz", "50 Hz", "100 Hz", "250 Hz"));

        Section("03", "OPERATION SETTING");

        Group("01", "GENERAL");
        Item(1, "NB WIDTH", Choice("1 ms", "3 ms", "10 ms"));
        Item(2, "NB REJECTION", Choice("10 dB", "30 dB", "50 dB"));
        Item(3, "BEEP LEVEL", Units("", 0, 100));
        Item(4, "RF/SQL VR", Choice("RF", "SQL"));
        Item(5, "TUNER SELECT", Choice("INT", "EXT1", "EXT2", "EXT3"), tx: true);
        Item(6, "232C RATE", baud, link: true);
        Item(7, "232C TIME OUT TIMER", timeout, link: true);
        Item(8, "CAT RATE", baud, link: true);
        Item(9, "CAT TIME OUT TIMER", timeout, link: true);
        Item(10, "CAT RTS", disEn, link: true);
        Item(11, "QMB CH", Choice("5ch", "10ch"));
        Item(12, "MEM GROUP", disEn);
        Item(13, "QUICK SPLIT INPUT", disEn);
        Item(14, "QUICK SPLIT FREQ", Units("kHz", -20, 20));
        Item(15, "SPLIT FREQ DISPLAY", Choice("FREQ", "DELTA"));
        Item(16, "TX TIME OUT TIMER", OffOrUnits("min", 30), tx: true);
        Item(17, "MIC SCAN", disEn);
        Item(18, "MIC SCAN RESUME", Choice("PAUSE", "TIME"));
        Item(19, "REF FREQ ADJ", Units("", -25, 25));
        Item(20, "KEYBOARD LANGUAGE", Choice("JAPANESE", "ENGLISH(US)", "ENGLISH(UK)", "FRENCH",
            "FRENCH(CA)", "GERMAN", "PORTUGUESE", "PORTUGUESE(BR)", "SPANISH", "SPANISH(LATAM)", "ITALIAN"));

        Group("02", "RX DSP");
        Item(1, "APF WIDTH", Choice("NARROW", "MEDIUM", "WIDE"));
        Item(2, "CONTOUR LEVEL", Units("", -40, 20));
        Item(3, "CONTOUR WIDTH", Units("", 1, 11));
        Item(4, "IF NOTCH WIDTH", Choice("NARROW", "WIDE"));

        Group("03", "TX AUDIO");
        Item(1, "AMC RELEASE TIME", Choice("FAST", "MID", "SLOW"), tx: true);
        foreach (var (first, prefix) in new[] { (2, "PRMTRC"), (11, "P PRMTRC") })
        {
            Item(first, $"{prefix} EQ1 FREQ", Steps(100, 100, 700, "Hz"), tx: true);
            Item(first + 1, $"{prefix} EQ1 LEVEL", Units("dB", -20, 10), tx: true);
            Item(first + 2, $"{prefix} EQ1 BWTH", Units("", 1, 10), tx: true);
            Item(first + 3, $"{prefix} EQ2 FREQ", Steps(700, 100, 1500, "Hz"), tx: true);
            Item(first + 4, $"{prefix} EQ2 LEVEL", Units("dB", -20, 10), tx: true);
            Item(first + 5, $"{prefix} EQ2 BWTH", Units("", 1, 10), tx: true);
            Item(first + 6, $"{prefix} EQ3 FREQ", Steps(1500, 100, 3200, "Hz"), tx: true);
            Item(first + 7, $"{prefix} EQ3 LEVEL", Units("dB", -20, 10), tx: true);
            Item(first + 8, $"{prefix} EQ3 BWTH", Units("", 1, 10), tx: true);
        }

        Group("04", "TX GNRL");
        Item(1, "HF MAX POWER", Units("W", 5, 100), tx: true);
        Item(2, "50M MAX POWER", Units("W", 5, 100), tx: true);
        Item(3, "70M MAX POWER", Units("W", 5, 50), tx: true);
        Item(4, "AM MAX POWER", Units("W", 5, 25), tx: true);
        Item(5, "VOX SELECT", Choice("MIC", "DATA"), tx: true);
        Item(6, "DATA VOX GAIN", Units("", 0, 100), tx: true);
        Item(7, "EMERGENCY FREQ TX", disEn, tx: true);

        Group("05", "TUNING");
        Item(1, "SSB/CW DIAL STEP", Choice("5 Hz", "10 Hz"));
        Item(2, "RTTY/PSK DIAL STEP", Choice("5 Hz", "10 Hz"));
        Item(3, "CH STEP", Choice("1 kHz", "2.5 kHz", "5 kHz", "10 kHz"));
        Item(4, "AM CH STEP", Choice("2.5 kHz", "5 kHz", "9 kHz", "10 kHz", "12.5 kHz", "25 kHz"));
        Item(5, "FM CH STEP", Choice("5 kHz", "6.25 kHz", "10 kHz", "12.5 kHz", "20 kHz", "25 kHz"));
        Item(6, "MAIN STEPS PER REV.", Choice("250", "500", "1000"));
        Item(7, "MPVD STEPS PER REV.", Choice("50", "250", "500"));

        Section("04", "DISPLAY SETTING");

        Group("01", "DISPLAY");
        Item(1, "MY CALL", Text(12));
        Item(2, "MY CALL TIME", Choice("OFF", "1 s", "2 s", "3 s", "4 s", "5 s"));
        Item(3, "SCREEN SAVER", Choice("OFF", "15 min", "30 min", "60 min"));
        Item(4, "DIMMER LED", Units("", 0, 20));
        Item(5, "MOUSE POINTER SPEED", Units("", 0, 20));

        Group("02", "SCOPE");
        Item(1, "RBW", Choice("HIGH", "MID", "LOW"));
        Item(2, "SCOPE CTR", Choice("FILTER", "CAR POINT"));
        Item(3, "2D DISP SENSITIVITY", Choice("NORMAL", "HI"));
        Item(4, "3DSS DISP SENSITIVITY", Choice("NORMAL", "HI"));

        Group("03", "EXT MONITOR");
        Item(1, "EXT DISPLAY", disEn);
        Item(2, "PIXEL", Choice("800x480", "800x600"));

        return items;
    }
}
