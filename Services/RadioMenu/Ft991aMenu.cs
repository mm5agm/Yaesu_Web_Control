using static Yaesu_Web_Control.Services.RadioMenu.Ftdx101Menu;

namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>
/// The FT-991A menu as the EX command sees it: the EX table of the FT-991
/// CAT Operation Reference Book, pages 7-9, items 001-153. Unlike the
/// FTdx101, FTDX10 and FT-710 menus, the address is one three-digit number
/// (EX001 to EX153), not P1 P2 P3, so the groups here are the operating
/// manual's menu groups rather than parts of the address.
///
/// 087 RADIO ID is read-only and has no value in the CAT table, so it is left
/// out. TIME ZONE and PRESET FREQUENCY are saved but not loaded: the CAT table
/// gives no format for the first, and the second is a frequency, not a choice.
///
/// The CAT manual numbers DATA PORT SELECT and FM PKT PORT SELECT from 1
/// ("1: DATA 2: USB") where AM and SSB PORT SELECT start at 0; that is taken as
/// written. RTTY SHIFT FREQ ("1: 170 1: 200") and RTTY MARK FREQ ("1: 1275
/// 2: 2125") are taken as 0-based, as the same misprints are on the FTdx101.
///
/// Not yet read back on an FT-991A. The raw value is saved beside each
/// decoded one, so a wrong guess shows up in the file.
/// </summary>
public static class Ft991aMenu
{
    public static readonly IReadOnlyList<RadioMenuItem> Items = Build();

    private static List<RadioMenuItem> Build()
    {
        var items = new List<RadioMenuItem>();
        const string section = "MENU";
        string group = "";

        void Group(string name) => group = name;
        void Item(int number, string function, Kind kind, bool tx = false, bool link = false)
            => items.Add(new RadioMenuItem($"{number:D3}", section, group, function, kind.Decode, tx,
                kind.Values, kind.TextMax, link));

        var slope = Choice("6 dB/oct", "18 dB/oct");
        var lcut = Steps(100, 50, 1000, "Hz");
        var hcut = Steps(700, 50, 4000, "Hz");
        var level = Units("", 0, 100);
        var micRear = Choice("MIC", "REAR");
        var ptt = Choice("DAKY", "RTS", "DTR");
        var offOn = Choice("OFF", "ON");
        var disableEnable = Choice("DISABLE", "ENABLE");
        var colours = new[] { "BLUE", "GRAY", "GREEN", "ORANGE", "PURPLE", "RED", "SKY BLUE" };

        // Each mode group starts with its RX audio cuts.
        void Cuts(int first, string mode)
        {
            Item(first, $"{mode} LCUT FREQ", lcut);
            Item(first + 1, $"{mode} LCUT SLOPE", slope);
            Item(first + 2, $"{mode} HCUT FREQ", hcut);
            Item(first + 3, $"{mode} HCUT SLOPE", slope);
        }

        // The parametric mic equalizer, once for the processor off (PRMTRC)
        // and once for it on (P-PRMTRC).
        void Equalizer(int first, string name)
        {
            Item(first, $"{name} EQ1 FREQ", Steps(100, 100, 700, "Hz"), tx: true);
            Item(first + 1, $"{name} EQ1 LEVEL", Units("", -20, 10), tx: true);
            Item(first + 2, $"{name} EQ1 BWTH", Units("", 1, 10), tx: true);
            Item(first + 3, $"{name} EQ2 FREQ", Steps(700, 100, 1500, "Hz"), tx: true);
            Item(first + 4, $"{name} EQ2 LEVEL", Units("", -20, 10), tx: true);
            Item(first + 5, $"{name} EQ2 BWTH", Units("", 1, 10), tx: true);
            Item(first + 6, $"{name} EQ3 FREQ", Steps(1500, 100, 3200, "Hz"), tx: true);
            Item(first + 7, $"{name} EQ3 LEVEL", Units("", -20, 10), tx: true);
            Item(first + 8, $"{name} EQ3 BWTH", Units("", 1, 10), tx: true);
        }

        Group("AGC");
        Item(1, "AGC FAST DELAY", Units("ms", 20, 4000, 20));
        Item(2, "AGC MID DELAY", Units("ms", 20, 4000, 20));
        Item(3, "AGC SLOW DELAY", Units("ms", 20, 4000, 20));

        Group("DISPLAY");
        Item(4, "HOME FUNCTION", Choice("SCOPE", "FUNCTION"));
        Item(5, "MY CALL INDICATION", Units("s", 0, 5));
        Item(6, "DISPLAY COLOR", Choice(colours));
        Item(7, "DIMMER LED", Choice("1", "2"));
        Item(8, "DIMMER TFT", Units("", 0, 15));
        Item(9, "BAR MTR PEAK HOLD", Choice("OFF", "0.5 s", "1.0 s", "2.0 s"));

        Group("DVS");
        Item(10, "DVS RX OUT LEVEL", level);
        Item(11, "DVS TX OUT LEVEL", level, tx: true);

        Group("KEYER");
        Item(12, "KEYER TYPE", Choice("OFF", "BUG", "ELEKEY-A", "ELEKEY-B", "ELEKEY-Y", "ACS"));
        Item(13, "KEYER DOT/DASH", Choice("NORMAL", "REVERSE"));
        Item(14, "CW WEIGHT", Tenths);
        Item(15, "BEACON INTERVAL", OffOrUnits("s", 690), tx: true);
        Item(16, "NUMBER STYLE", Choice("1290", "AUNO", "AUNT", "A2NO", "A2NT", "12NO", "12NT"));
        Item(17, "CONTEST NUMBER", Units("", 0, 9999));
        for (int m = 1; m <= 5; m++)
            Item(17 + m, $"CW MEMORY {m}", Choice("TEXT", "MESSAGE"));

        Group("GENERAL");
        Item(23, "NB WIDTH", Choice("1 ms", "3 ms", "10 ms"));
        Item(24, "NB REJECTION", Choice("10 dB", "30 dB", "50 dB"));
        Item(25, "NB LEVEL", Units("", 0, 10));
        Item(26, "BEEP LEVEL", level);
        Item(27, "TIME ZONE", new Kind(raw => raw, null));
        // 0 GPS1, 1 GPS2, 3 RS232C: there is no 2.
        Item(28, "GPS/232C SELECT", new Kind(raw => raw switch
        {
            "0" => "GPS1",
            "1" => "GPS2",
            "3" => "RS232C",
            _ => raw,
        }, [0, 1, 3]), link: true);
        Item(29, "232C RATE", Choice("4800 bps", "9600 bps", "19200 bps", "38400 bps"), link: true);
        Item(30, "232C TOT", Choice("10 ms", "100 ms", "1000 ms", "3000 ms"), link: true);
        Item(31, "CAT RATE", Choice("4800 bps", "9600 bps", "19200 bps", "38400 bps"), link: true);
        Item(32, "CAT TOT", Choice("10 ms", "100 ms", "1000 ms", "3000 ms"), link: true);
        Item(33, "CAT RTS", disableEnable, link: true);
        Item(34, "MEM GROUP", disableEnable);
        Item(35, "QUICK SPLIT FREQ", Units("kHz", -20, 20));
        Item(36, "TX TOT", OffOrUnits("min", 30), tx: true);
        Item(37, "MIC SCAN", disableEnable);
        Item(38, "MIC SCAN RESUME", Choice("PAUSE", "TIME"));
        Item(39, "REF FREQ ADJ", Units("", -25, 25));
        Item(40, "CLAR MODE SELECT", Choice("RX", "TX", "TRX"));

        Group("MODE AM");
        Cuts(41, "AM");
        Item(45, "AM MIC SELECT", micRear, tx: true);
        Item(46, "AM OUT LEVEL", level);
        Item(47, "AM PTT SELECT", ptt, tx: true);
        Item(48, "AM PORT SELECT", Choice("DATA", "USB"), tx: true);
        Item(49, "AM DATA GAIN", level, tx: true);

        Group("MODE CW");
        Cuts(50, "CW");
        Item(54, "CW OUT LEVEL", level);
        Item(55, "CW AUTO MODE", Choice("OFF", "50 MHz", "ON"), tx: true);
        Item(56, "CW BK-IN TYPE", Choice("SEMI BREAK-IN", "FULL BREAK-IN"), tx: true);
        Item(57, "CW BK-IN DELAY", Units("ms", 30, 3000, 10), tx: true);
        Item(58, "CW WAVE SHAPE", Choice("1 ms", "2 ms", "4 ms", "6 ms"), tx: true);
        Item(59, "CW FREQ DISPLAY", Choice("DIRECT FREQ", "PITCH OFFSET"));
        Item(60, "PC KEYING", Choice("OFF", "DAKY", "RTS", "DTR"), tx: true);
        Item(61, "QSK DELAY TIME", Choice("15 ms", "20 ms", "25 ms", "30 ms"), tx: true);

        Group("MODE DATA");
        Item(62, "DATA MODE", Choice("PSK", "OTHER"));
        Item(63, "PSK TONE", Choice("1000 Hz", "1500 Hz", "2000 Hz"));
        Item(64, "OTHER DISP (SSB)", Units("Hz", -3000, 3000, 10));
        Item(65, "OTHER SHIFT (SSB)", Units("Hz", -3000, 3000, 10));
        Cuts(66, "DATA");
        Item(70, "DATA IN SELECT", micRear, tx: true);
        Item(71, "DATA PTT SELECT", ptt, tx: true);
        Item(72, "DATA PORT SELECT", FromOne("DATA", "USB"), tx: true);
        Item(73, "DATA OUT LEVEL", level);

        Group("MODE FM");
        Item(74, "FM MIC SELECT", micRear, tx: true);
        Item(75, "FM OUT LEVEL", level);
        Item(76, "FM PKT PTT SELECT", ptt, tx: true);
        Item(77, "FM PKT PORT SELECT", FromOne("DATA", "USB"), tx: true);
        Item(78, "FM PKT TX GAIN", level, tx: true);
        Item(79, "FM PKT MODE", Choice("1200", "9600"));
        Item(80, "RPT SHIFT 28MHz", Units("kHz", 0, 1000, 10), tx: true);
        Item(81, "RPT SHIFT 50MHz", Units("kHz", 0, 4000, 10), tx: true);
        Item(82, "RPT SHIFT 144MHz", Units("kHz", 0, 4000, 10), tx: true);
        Item(83, "RPT SHIFT 430MHz", Units("kHz", 0, 10000, 10), tx: true);
        Item(84, "ARS 144MHz", offOn, tx: true);
        Item(85, "ARS 430MHz", offOn, tx: true);
        Item(86, "DCS POLARITY", Choice("Tn-Rn", "Tn-Riv", "Tiv-Rn", "Tiv-Riv"), tx: true);

        Group("C4FM");
        Item(88, "GM DISPLY", Choice("DISTANCE", "STRENGTH"));
        Item(89, "DISTANCE", Choice("km", "mile"));
        Item(90, "AMS TX MODE", Choice("AUTO", "MANUAL", "DN", "VW", "ANALOG"), tx: true);
        Item(91, "STANDBY BEEP", offOn);

        Group("MODE RTTY");
        Cuts(92, "RTTY");
        Item(96, "RTTY SHIFT PORT", Choice("SHIFT", "DTR", "RTS"), tx: true);
        Item(97, "RTTY POLARITY-RX", Choice("NORMAL", "REVERSE"));
        Item(98, "RTTY POLARITY-TX", Choice("NORMAL", "REVERSE"), tx: true);
        Item(99, "RTTY OUT LEVEL", level);
        Item(100, "RTTY SHIFT FREQ", Choice("170 Hz", "200 Hz", "425 Hz", "850 Hz"));
        Item(101, "RTTY MARK FREQ", Choice("1275 Hz", "2125 Hz"));

        Group("MODE SSB");
        Cuts(102, "SSB");
        Item(106, "SSB MIC SELECT", micRear, tx: true);
        Item(107, "SSB OUT LEVEL", level);
        Item(108, "SSB PTT SELECT", ptt, tx: true);
        Item(109, "SSB PORT SELECT", Choice("DATA", "USB"), tx: true);
        Item(110, "SSB TX BPF", Choice("50-3000", "100-2900", "200-2800", "300-2700", "400-2600"), tx: true);

        Group("RX DSP");
        Item(111, "APF WIDTH", Choice("NARROW", "MEDIUM", "WIDE"));
        Item(112, "CONTOUR LEVEL", Units("", -40, 20));
        Item(113, "CONTOUR WIDTH", Units("", 1, 11));
        Item(114, "IF NOTCH WIDTH", Choice("NARROW", "WIDE"));

        Group("SCOPE");
        Item(115, "SCP DISPLAY MODE", Choice("SPECTRUM", "WATER FALL"));
        Item(116, "SCP SPAN FREQ", From(3, "50 kHz", "100 kHz", "200 kHz", "500 kHz", "1000 kHz"));
        Item(117, "SPECTRUM COLOR", Choice(colours));
        Item(118, "WATER FALL COLOR", Choice([.. colours, "MULTI"]));

        Group("TX AUDIO");
        Equalizer(119, "PRMTRC");
        Equalizer(128, "P-PRMTRC");

        Group("TX GNRL");
        Item(137, "HF TX MAX POWER", Units("W", 5, 100), tx: true);
        Item(138, "50M TX MAX POWER", Units("W", 5, 100), tx: true);
        Item(139, "144M TX MAX POWER", Units("W", 5, 50), tx: true);
        Item(140, "430M TX MAX POWER", Units("W", 5, 50), tx: true);
        Item(141, "TUNER SELECT", Choice("OFF", "INTERNAL", "EXTERNAL", "ATAS", "LAMP"), tx: true);
        Item(142, "VOX SELECT", Choice("MIC", "DATA"), tx: true);
        Item(143, "VOX GAIN", level, tx: true);
        Item(144, "VOX DELAY", Units("ms", 30, 3000, 10), tx: true);
        Item(145, "ANTI VOX GAIN", level, tx: true);
        Item(146, "DATA VOX GAIN", level, tx: true);
        Item(147, "DATA VOX DELAY", Units("ms", 30, 3000, 10), tx: true);
        Item(148, "ANTI DVOX GAIN", level, tx: true);
        Item(149, "EMERGENCY FREQ TX", disableEnable, tx: true);

        Group("WIRES-X");
        Item(150, "PRT/WIRES FREQ", Choice("MANUAL", "PRESET"));
        Item(151, "PRESET FREQUENCY", new Kind(raw =>
            long.TryParse(raw, out var hz)
                ? (hz / 1e6).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + " MHz"
                : raw, null));
        Item(152, "SEARCH SETUP", Choice("HISTORY", "ACTIVITY"));
        Item(153, "WIRES DG-ID", new Kind(raw =>
            !int.TryParse(raw, out var v) ? raw : v == 0 ? "AUTO" : $"DG-ID {v:D2}", Range(0, 99)));

        return items;
    }

    /// <summary>A choice whose first label is <paramref name="first"/>, not 0.</summary>
    private static Kind From(int first, params string[] labels) => new(raw =>
        int.TryParse(raw, out var i) && i >= first && i < first + labels.Length ? labels[i - first] : raw,
        Range(first, first + labels.Length - 1));

    /// <summary>A choice numbered from 1, as the CAT manual gives DATA PORT SELECT.</summary>
    private static Kind FromOne(params string[] labels) => From(1, labels);
}
