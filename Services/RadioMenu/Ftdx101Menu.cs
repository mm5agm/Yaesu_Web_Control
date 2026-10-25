namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>
/// The FTdx101MP/D menu as the EX command sees it: Table 2 (MENU Chart) of
/// the FTDX101MP/D CAT manual, 2308-L, pages 10-12. Each address is P1 P2 P3,
/// two digits each, so "010306" is RADIO SETTING / MODE FM / HCUT FREQ.
///
/// Transcribed by hand from the PDF, whose Digits column did not survive
/// text extraction. Reading does not need it: the answer carries its own
/// value. Load writes a value in the same width and sign as the radio's
/// current answer for that item, so it does not need it either.
///
/// Each item also carries the values the radio accepts, from the same
/// table; Load writes nothing outside them. The MP and D differ only in
/// their power limits.
///
/// Where the manual misprints a code list (MARK FREQUENCY "1: 1275 2: 2125",
/// SHIFT FREQUENCY "1: 170 1: 200", DECODE AFC RANGE "1: 1.5") the list here
/// is taken as 0-based like every other item. The raw value is always saved
/// beside the decoded one, so a wrong guess shows up against the radio.
/// Those three were read back on an FTdx101MP on 2026-10-25 and match.
///
/// The operating manual (2102P) disagrees with the CAT manual on two lists,
/// and the radio sides with the operating manual on both (read 2026-10-25):
/// CW WAVE SHAPE is 4/6/8 ms (CAT: 1/2/4/6 ms), the radio showing 6 ms and
/// answering 1; MY CALL TIME is OFF/1-5 s (CAT: OFF/0.5/1/2/3/5 s), the radio
/// showing 1 s and answering 01.
/// </summary>
public static class Ftdx101Menu
{
    /// <summary>The FTdx101MP (200 W).</summary>
    public static readonly IReadOnlyList<RadioMenuItem> Items = Build(mp: true);

    /// <summary>The FTdx101D (100 W).</summary>
    public static readonly IReadOnlyList<RadioMenuItem> ItemsD = Build(mp: false);

    /// <summary>How an item's value is shown and which values the radio accepts.</summary>
    private sealed record Kind(Func<string, string> Decode, IReadOnlyList<int>? Values, int TextMax = 0);

    private static List<RadioMenuItem> Build(bool mp)
    {
        var items = new List<RadioMenuItem>();
        string section = "", group = "";
        string p1 = "", p2 = "";

        void Section(string code, string name) { p1 = code; section = name; }
        void Group(string code, string name) { p2 = code; group = name; }
        void Item(int p3, string function, Kind kind, bool tx = false, bool link = false)
            => items.Add(new RadioMenuItem($"{p1}{p2}{p3:D2}", section, group, function, kind.Decode, tx,
                kind.Values, kind.TextMax, link));

        // Lists common to the five RADIO SETTING mode groups.
        void Agc()
        {
            Item(1, "AGC FAST DELAY", Units("ms", 20, 4000, 20));
            Item(2, "AGC MID DELAY", Units("ms", 20, 4000, 20));
            Item(3, "AGC SLOW DELAY", Units("ms", 20, 4000, 20));
        }
        void Cuts(int first)
        {
            Item(first, "LCUT FREQ", Steps(100, 50, 1000, "Hz"));
            Item(first + 1, "LCUT SLOPE", Choice("6 dB/oct", "18 dB/oct"));
            Item(first + 2, "HCUT FREQ", Steps(700, 50, 4000, "Hz"));
            Item(first + 3, "HCUT SLOPE", Choice("6 dB/oct", "18 dB/oct"));
        }
        var txBpf = Choice("50-3050", "100-2900", "200-2800", "300-2700", "400-2600");
        var mainSub = Choice("MAIN", "SUB");
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
        Agc();
        Cuts(4);
        Item(8, "SSB OUT SELECT", mainSub);
        Item(9, "SSB OUT LEVEL", Units("", 0, 100));
        Item(10, "TX BPF SEL", txBpf, tx: true);
        Item(11, "SSB MOD SOURCE", micRear, tx: true);
        Item(12, "REAR SELECT", dataUsb, tx: true);
        Item(13, "RPORT GAIN", Units("", 0, 100), tx: true);
        Item(14, "RPTT SELECT", rptt, tx: true);

        Group("02", "MODE AM");
        Agc();
        Cuts(4);
        Item(8, "AM OUT SELECT", mainSub);
        Item(9, "AM OUT LEVEL", Units("", 0, 100));
        Item(10, "TX BPF SEL", txBpf, tx: true);
        Item(11, "AM MOD SOURCE", micRear, tx: true);
        Item(12, "MIC GAIN", MicGain, tx: true);
        Item(13, "REAR SELECT", dataUsb, tx: true);
        Item(14, "RPORT GAIN", Units("", 0, 100), tx: true);
        Item(15, "RPTT SELECT", rptt, tx: true);

        Group("03", "MODE FM");
        Agc();
        Cuts(4);
        Item(8, "FM OUT SELECT", mainSub);
        Item(9, "FM OUT LEVEL", Units("", 0, 100));
        Item(10, "FM MOD SOURCE", micRear, tx: true);
        Item(11, "MIC GAIN", MicGain, tx: true);
        Item(12, "REAR SELECT", dataUsb, tx: true);
        Item(13, "RPORT GAIN", Units("", 0, 100), tx: true);
        Item(14, "RPTT SELECT", rptt, tx: true);
        Item(15, "RPT SHIFT (28MHz)", Units("kHz", 0, 1000, 10), tx: true);
        Item(16, "RPT SHIFT (50MHz)", Units("kHz", 0, 4000, 10), tx: true);

        Group("04", "MODE PSK/DATA");
        Agc();
        Item(4, "PSK TONE", Choice("1000 Hz", "1500 Hz", "2000 Hz"));
        Item(5, "DATA SHIFT (SSB)", Units("Hz", 0, 3000, 10));
        Cuts(6);
        Item(10, "DATA OUT SELECT", mainSub);
        Item(11, "DATA OUT LEVEL", Units("", 0, 100));
        Item(12, "TX BPF SEL", txBpf, tx: true);
        Item(13, "DATA MOD SOURCE", micRear, tx: true);
        Item(14, "REAR SELECT", dataUsb, tx: true);
        Item(15, "RPORT GAIN", Units("", 0, 100), tx: true);
        Item(16, "RPTT SELECT", rptt, tx: true);

        Group("05", "MODE RTTY");
        Agc();
        Item(4, "POLARITY RX", norRev);
        Item(5, "POLARITY TX", norRev, tx: true);
        Cuts(6);
        Item(10, "RTTY OUT SELECT", mainSub);
        Item(11, "RTTY OUT LEVEL", Units("", 0, 100));
        Item(12, "SHIFT PORT", Choice("SHIFT", "DTR", "RTS"), tx: true);
        Item(13, "MARK FREQUENCY", Choice("1275 Hz", "2125 Hz"));
        Item(14, "SHIFT FREQUENCY", Choice("170 Hz", "200 Hz", "425 Hz", "850 Hz"));

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
        Agc();
        Cuts(4);
        Item(8, "CW OUT SELECT", mainSub);
        Item(9, "CW OUT LEVEL", Units("", 0, 100));
        Item(10, "CW AUTO MODE", Choice("OFF", "50 MHz", "ON"), tx: true);
        Item(11, "CW BK-IN TYPE", Choice("SEMI", "FULL"), tx: true);
        Item(12, "CW BK-IN DELAY", BreakInDelay, tx: true);
        Item(13, "CW WAVE SHAPE", Choice("4 ms", "6 ms", "8 ms"), tx: true);
        Item(14, "CW FREQ DISPLAY", Choice("DIRECT FREQ", "PITCH OFFSET"));
        Item(15, "PC KEYING", Choice("OFF", "DAKY", "RTS", "DTR"), tx: true);
        Item(16, "QSK DELAY TIME", Choice("15 ms", "20 ms", "25 ms", "30 ms"), tx: true);
        Item(17, "CW INDICATOR", offOn);

        Group("02", "KEYER");
        Item(1, "F KEYER TYPE", keyer, tx: true);
        Item(2, "F KEYER DOT/DASH", norRev, tx: true);
        Item(3, "R KEYER TYPE", keyer, tx: true);
        Item(4, "R KEYER DOT/DASH", norRev, tx: true);
        Item(5, "CW WEIGHT", Tenths, tx: true);
        Item(6, "NUMBER STYLE", Choice("1290", "AUNO", "AUNT", "A2NO", "A2NT", "12NO", "12NT"));
        Item(7, "CONTEST NUMBER", Units("", 1, 9999));
        Item(8, "CW MEMORY 1", Choice("TEXT", "MESSAGE"));
        Item(9, "CW MEMORY 2", Choice("TEXT", "MESSAGE"));
        Item(10, "CW MEMORY 3", Choice("TEXT", "MESSAGE"));
        Item(11, "CW MEMORY 4", Choice("TEXT", "MESSAGE"));
        Item(12, "CW MEMORY 5", Choice("TEXT", "MESSAGE"));
        Item(13, "REPEAT INTERVAL", Units("s", 1, 60));

        Group("03", "DECODE CW");
        Item(1, "CW DECODE BW", Choice("25 Hz", "50 Hz", "100 Hz", "250 Hz"));

        Section("03", "OPERATION SETTING");

        Group("01", "GENERAL");
        Item(1, "DECODE RX SELECT", mainSub);
        Item(2, "HEADPHONE MIX", Choice("SEPARATE", "COMBINE-1", "COMBINE-2"));
        Item(3, "ANT3 SELECT", Choice("TRX", "RX ANT"));
        Item(4, "NB WIDTH", Choice("1 ms", "3 ms", "10 ms"));
        Item(5, "NB REJECTION", Choice("10 dB", "30 dB", "50 dB"));
        Item(6, "BEEP LEVEL", Units("", 0, 100));
        Item(7, "RF/SQL VR", Choice("RF", "SQL"));
        Item(8, "TUNER SELECT", Choice("INT", "EXT1", "EXT2", "EXT3"), tx: true);
        Item(9, "232C RATE", baud, link: true);
        Item(10, "232C TIME OUT TIMER", timeout, link: true);
        Item(11, "CAT RATE", baud, link: true);
        Item(12, "CAT TIME OUT TIMER", timeout, link: true);
        Item(13, "CAT RTS", disEn, link: true);
        Item(14, "QMB CH", Choice("5ch", "10ch"));
        Item(15, "MEM GROUP", disEn);
        Item(16, "QUICK SPLIT INPUT", disEn);
        Item(17, "QUICK SPLIT FREQ", Units("kHz", -20, 20));
        Item(18, "TX TIME OUT TIMER", OffOrUnits("min", 30), tx: true);
        Item(19, "MIC SCAN", disEn);
        Item(20, "MIC SCAN RESUME", Choice("PAUSE", "TIME"));
        Item(21, "REF FREQ ADJ", Units("", -25, 25));
        Item(22, "CS DIAL", Choice("RF POWER", "MONI LEVEL", "DNR LEVEL", "NB LEVEL", "VOX GAIN",
            "VOX DELAY", "ANTI VOX", "STEP DIAL", "MEM CH", "GROUP", "R.FIL", "LEVEL"));
        Item(23, "KEYBOARD LANGUAGE", Choice("JAPANESE", "ENGLISH(US)", "ENGLISH(UK)", "FRENCH",
            "FRENCH(CA)", "GERMAN", "PORTUGUESE", "PORTUGUESE(BR)", "SPANISH", "SPANISH(LATAM)", "ITALIAN"));

        Group("02", "RX DSP");
        Item(1, "APF WIDTH", Choice("NARROW", "MEDIUM", "WIDE"));
        Item(2, "CONTOUR LEVEL", Units("", -40, 20));
        Item(3, "CONTOUR WIDTH", Units("", 1, 11));
        Item(4, "DNR LEVEL", Units("", 1, 15));
        Item(5, "IF NOTCH WIDTH", Choice("NARROW", "WIDE"));

        Group("03", "TX AUDIO");
        Item(1, "PROC TYPE", Choice("COMP", "AMC"), tx: true);
        Item(2, "AMC RELEASE TIME", Choice("FAST", "MID", "SLOW"), tx: true);
        foreach (var (first, prefix) in new[] { (3, "PRMTRC"), (12, "P PRMTRC") })
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
        Item(1, "HF MAX POWER", Units("W", 5, mp ? 200 : 100), tx: true);
        Item(2, "50M MAX POWER", Units("W", 5, mp ? 200 : 100), tx: true);
        Item(3, "70M MAX POWER", Units("W", 5, 50), tx: true);
        Item(4, "AM MAX POWER", Units("W", 5, mp ? 50 : 25), tx: true);
        Item(5, "VOX SELECT", Choice("MIC", "DATA"), tx: true);
        Item(6, "DATA VOX GAIN", Units("", 0, 100), tx: true);
        Item(7, "EMERGENCY FREQ TX", disEn, tx: true);

        Group("05", "TUNING");
        Item(1, "SSB/CW DIAL STEP", Choice("5 Hz", "10 Hz"));
        Item(2, "RTTY/PSK DIAL STEP", Choice("5 Hz", "10 Hz"));
        Item(3, "CH STEP", Choice("1 kHz", "2.5 kHz", "5 kHz"));
        Item(4, "AM CH STEP", Choice("2.5 kHz", "5 kHz", "9 kHz", "10 kHz", "12.5 kHz", "25 kHz"));
        Item(5, "FM CH STEP", Choice("5 kHz", "6.25 kHz", "10 kHz", "12.5 kHz", "20 kHz", "25 kHz"));
        Item(6, "MAIN STEPS PER REV.", Choice("250", "500", "1000"));
        Item(7, "MPVD STEPS PER REV.", Choice("50", "250", "500"));

        Section("04", "DISPLAY SETTING");

        Group("01", "DISPLAY");
        Item(1, "MY CALL", Text(12));
        Item(2, "MY CALL TIME", Choice("OFF", "1 s", "2 s", "3 s", "4 s", "5 s"));
        Item(3, "SCREEN SAVER", Choice("OFF", "15 min", "30 min", "60 min"));
        Item(4, "TFT CONTRAST", Units("", 0, 20));
        Item(5, "DIMMER TFT", Units("", 0, 20));
        Item(6, "DIMMER LED", Units("", 0, 20));
        Item(7, "MOUSE POINTER SPEED", Units("", 0, 20));
        Item(8, "FREQ STYLE", Choice("LIGHT", "BOLD"));

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

    // ── Decoders. Each falls back to the raw value when it cannot place it,
    //    so an unexpected answer is shown as sent rather than dropped.

    private static IReadOnlyList<int> Range(int min, int max, int step = 1) =>
        Enumerable.Range(0, (max - min) / step + 1).Select(i => min + i * step).ToArray();

    private static Kind Choice(params string[] labels) => new(raw =>
        int.TryParse(raw, out var i) && i >= 0 && i < labels.Length ? labels[i] : raw, Range(0, labels.Length - 1));

    /// <summary>A plain number, signed if the radio signs it ("+05" is +5).</summary>
    private static Kind Units(string unit, int min, int max, int step = 1) => new(UnitsText(unit), Range(min, max, step));

    private static Func<string, string> UnitsText(string unit) => raw =>
    {
        if (!int.TryParse(raw, out var v)) return raw;
        var text = raw.StartsWith('+') || raw.StartsWith('-') ? v.ToString("+0;-0;0") : v.ToString();
        return unit.Length == 0 ? text : $"{text} {unit}";
    };

    /// <summary>0 = OFF, then 1 to max.</summary>
    private static Kind OffOrUnits(string unit, int max) => new(raw =>
        int.TryParse(raw, out var v) && v == 0 ? "OFF" : UnitsText(unit)(raw), Range(0, max));

    /// <summary>00 = OFF, then 01 = first, each step adding step, up to last.</summary>
    private static Kind Steps(int first, int step, int last, string unit) => new(raw =>
        !int.TryParse(raw, out var v) ? raw : v == 0 ? "OFF" : $"{first + (v - 1) * step} {unit}",
        Range(0, (last - first) / step + 1));

    /// <summary>1000 = the MIC GAIN knob (MCVR), 0000-0100 = a fixed gain.</summary>
    private static Kind MicGain => new(raw =>
        !int.TryParse(raw, out var v) ? raw : v == 1000 ? "MCVR" : $"FIX {v}", Range(0, 100).Append(1000).ToArray());

    /// <summary>25-45 is a weight of 2.5-4.5.</summary>
    private static Kind Tenths => new(raw =>
        int.TryParse(raw, out var v) ? (v / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : raw,
        Range(25, 45));

    /// <summary>The same step table as the SD command: 30, 50, 100-300 in 50s, then 100s to 3000.</summary>
    private static Kind BreakInDelay => new(raw =>
    {
        if (!int.TryParse(raw, out var v) || v < 0 || v > 33) return raw;
        int ms = v switch { 0 => 30, 1 => 50, <= 6 => 100 + (v - 2) * 50, _ => 400 + (v - 7) * 100 };
        return $"{ms} ms";
    }, Range(0, 33));

    /// <summary>Free text of up to max characters, e.g. MY CALL.</summary>
    private static Kind Text(int max) => new(raw => raw.Trim(), null, max);
}
