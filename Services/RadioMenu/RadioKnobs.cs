using System.Globalization;
using System.Text.RegularExpressions;

namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>
/// Where the radio keeps a front-panel setting: one for the whole radio, one
/// for each band, one for each mode, or one for each mode on each band.
/// Found on an FTdx101MP on 2026-11-27 by setting each one and reading it in
/// another mode and on another band.
/// </summary>
public enum RadioKnobScope { Global, Band, Mode, BandMode }

/// <summary>
/// A front-panel setting saved with the radio menu: a knob or button the menu
/// does not hold, such as MIC GAIN, RF GAIN or the AGC. Read and set with its
/// own CAT command, a fixed number of digits.
/// </summary>
/// <param name="Command">The CAT command, e.g. "MG", or "PR0" / "CO01" for one that takes selectors.</param>
/// <param name="Name">The name the radio uses for it.</param>
/// <param name="Min">The lowest value the radio takes.</param>
/// <param name="Max">The highest value the radio takes.</param>
/// <param name="Width">How many digits the value has on the wire.</param>
/// <param name="Show">Turns the value into what the radio shows.</param>
public sealed record RadioKnob(string Command, string Name, int Min, int Max, int Width, Func<int, string> Show)
{
    public RadioKnobScope Scope { get; init; } = RadioKnobScope.Mode;

    /// <summary>The value is one of a few names (OFF/ON, AMP 1), not a number.</summary>
    public bool Choice { get; init; }

    /// <summary>A number shows as value × Step + Offset: KEY PITCH 33 is 630 Hz.</summary>
    public int Step { get; init; } = 1;

    public int Offset { get; init; }

    /// <summary>A transmit setting: marked [TX] in the file, so Load starts it unticked.</summary>
    public bool Tx { get; init; }

    /// <summary>Maps an answer the radio only reports to one it takes (AGC AUTO-FAST reads 4 to 6, is set as 4).</summary>
    public Func<int, int>? Fold { get; init; }

    /// <summary>Only saved in modes whose name starts with this, e.g. "CW" for BREAK-IN.</summary>
    public string? OnlyIn { get; init; }

    /// <summary>Not saved in modes whose name starts with one of these: the radio does not answer there (MIC GAIN in CW).</summary>
    public string[]? NotIn { get; init; }

    /// <summary>What to send to read it, when not the set command's prefix ("SH0" reads WIDTH; "SH00;" is not a read).</summary>
    public string? Query { get; init; }

    public bool PerMode => Scope is RadioKnobScope.Mode or RadioKnobScope.BandMode;
    public bool PerBand => Scope is RadioKnobScope.Band or RadioKnobScope.BandMode;
    public bool AppliesIn(RadioKnobMode mode) =>
        (OnlyIn is null || mode.Name.StartsWith(OnlyIn, StringComparison.Ordinal))
        && (NotIn is null || !NotIn.Any(n => mode.Name.StartsWith(n, StringComparison.Ordinal)));
}

/// <summary>A main-band mode the knobs are read in: its MD code and its name on the radio.</summary>
public sealed record RadioKnobMode(string Code, string Name);

/// <summary>
/// One saved value's place: the knob, and the mode and band it is for (null
/// where the radio keeps one for every mode or every band).
/// </summary>
public sealed record RadioKnobSlot(RadioKnob Knob, RadioKnobMode? Mode, string? Band)
{
    /// <summary>"knob:RG0", "knob:PA0@20m", "knob:2:MG" or "knob:2:GT0@20m".</summary>
    public string Address => RadioKnobs.AddressPrefix + (Mode is null ? "" : Mode.Code + ":") + Knob.Command + (Band is null ? "" : "@" + Band);

    /// <summary>"RF GAIN", "IPO (20m)", "MIC GAIN (USB)" or "AGC (USB, 20m)".</summary>
    public string Label => (Mode, Band) switch
    {
        (null, null) => Knob.Name,
        (null, _) => $"{Knob.Name} ({Band})",
        (_, null) => $"{Knob.Name} ({Mode.Name})",
        _ => $"{Knob.Name} ({Mode.Name}, {Band})",
    };

    /// <summary>"in USB", "in USB on 20m", "on 20m", or "" for one setting for the whole radio.</summary>
    public string Where => (Mode, Band) switch
    {
        (null, null) => "",
        (null, _) => $"on {Band}",
        (_, null) => $"in {Mode.Name}",
        _ => $"in {Mode.Name} on {Band}",
    };
}

/// <summary>A knob, what the radio answered (null if it did not), and the mode and band it was read for.</summary>
public sealed record RadioKnobReading(RadioKnobMode? Mode, RadioKnob Knob, int? Value, string? Band = null)
{
    public RadioKnobSlot Slot => new(Knob, Mode, Band);
}

/// <summary>
/// The front-panel part of the radio menu save. The radio keeps some of these
/// for each mode, so YWC switches the main band through the modes and puts
/// the mode back afterwards. Those it keeps for each band are saved for the
/// band the radio is on. Nothing here transmits; every command is a read, a
/// mode change or a setting.
/// </summary>
public static class RadioKnobs
{
    /// <summary>The section for settings that are not kept per mode: "[Front panel]".</summary>
    public const string SectionName = "Front panel";

    /// <summary>The start of each per-mode [section]: "[Front panel > USB]".</summary>
    public const string SectionPrefix = SectionName + " > ";

    /// <summary>Knob rows use "knob:2:MG" as their address, so they can never clash with an EX address.</summary>
    public const string AddressPrefix = "knob:";

    /// <summary>The FTdx101's main-band modes (MD0), in the order the file lists them.</summary>
    public static readonly IReadOnlyList<RadioKnobMode> Ftdx101Modes =
    [
        new("1", "LSB"), new("2", "USB"),
        new("3", "CW-U"), new("7", "CW-L"),
        new("5", "AM"), new("4", "FM"),
        new("8", "DATA-L"), new("C", "DATA-U"),
        new("6", "RTTY-L"), new("9", "RTTY-U"),
        new("E", "PSK"),
    ];

    /// <summary>The band names a saved per-band value can have.</summary>
    public static readonly IReadOnlyList<string> Bands =
        ["160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m", "4m", "GEN"];

    private static string Level(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static readonly string[] OffOn = ["OFF", "ON"];

    private static RadioKnob Lvl(string command, string name, int min, int max, int width, RadioKnobScope scope,
        bool tx = false, int step = 1, int offset = 0, string unit = "", string[]? notIn = null, string? query = null) =>
        new(command, name, min, max, width, v => Level(v * step + offset) + unit)
        { Scope = scope, Tx = tx, Step = step, Offset = offset, NotIn = notIn, Query = query };

    private static RadioKnob Pick(string command, string name, int width, RadioKnobScope scope, string[] names,
        bool tx = false, Func<int, int>? fold = null, string? onlyIn = null, string[]? notIn = null) =>
        new(command, name, 0, names.Length - 1, width, v => v >= 0 && v < names.Length ? names[v] : Level(v))
        { Scope = scope, Choice = true, Tx = tx, Fold = fold, OnlyIn = onlyIn, NotIn = notIn };

    private const RadioKnobScope G = RadioKnobScope.Global, B = RadioKnobScope.Band,
        M = RadioKnobScope.Mode, BM = RadioKnobScope.BandMode;

    // AF GAIN (the volume) and the frequency are left out on purpose. Also left
    // out: the roofing filter (the radio picks it from the WIDTH), IF SHIFT
    // and the FM tone settings. Order matters on Load: NAR before WIDTH, as
    // NAR changes the width.
    private static IReadOnlyList<RadioKnob> Ftdx101(int maxWatts) =>
    [
        // One setting for the whole radio.
        Lvl("RG0", "RF GAIN", 0, 255, 3, G),
        Lvl("SQ0", "SQUELCH", 0, 100, 3, G),
        Lvl("NL0", "NB LEVEL", 0, 10, 3, G),
        Lvl("CO03", "APF FREQ", 0, 50, 4, G),
        Lvl("KS", "KEY SPEED", 4, 60, 3, G, tx: true, unit: " wpm"),
        Lvl("KP", "KEY PITCH", 0, 75, 2, G, step: 10, offset: 300, unit: " Hz"),
        Lvl("SD", "BREAK-IN DELAY", 0, 30, 2, G, tx: true),
        Lvl("VD", "VOX DELAY", 0, 33, 2, G, tx: true),
        Lvl("AV", "ANTI VOX", 0, 100, 3, G, tx: true),

        // One for each band.
        Pick("PA0", "IPO", 1, B, ["IPO", "AMP 1", "AMP 2"]),
        Pick("RA0", "ATT", 1, B, ["OFF", "6 dB", "12 dB", "18 dB"]),
        Pick("NB0", "NB", 1, B, OffOn),
        Pick("NR0", "NR", 1, B, OffOn),
        Lvl("RL0", "NR LEVEL", 1, 15, 2, B),

        // One for each mode on each band.
        Pick("GT0", "AGC", 1, BM, ["OFF", "FAST", "MID", "SLOW", "AUTO"], fold: v => v is 5 or 6 ? 4 : v),
        Pick("NA0", "NAR", 1, BM, OffOn),
        Lvl("SH00", "WIDTH", 0, 23, 2, BM, query: "SH0"),
        Pick("BC0", "DNF", 1, BM, OffOn, notIn: ["FM"]),
        Pick("BP00", "NOTCH", 3, BM, OffOn),
        Lvl("BP01", "NOTCH FREQ", 1, 320, 3, BM, step: 10, unit: " Hz"),
        Pick("CO00", "CONTOUR", 4, BM, OffOn),
        Lvl("CO01", "CONTOUR FREQ", 10, 3200, 4, BM, unit: " Hz"),
        Pick("CO02", "APF", 4, BM, OffOn, onlyIn: "CW"),

        // One for each mode.
        Lvl("MG", "MIC GAIN", 0, 100, 3, M, tx: true, notIn: ["CW", "RTTY"]),
        Lvl("AO", "AMC LEVEL", 1, 100, 3, M, tx: true),
        Pick("PR0", "PROC", 1, M, OffOn, tx: true),
        Lvl("PL", "PROC LEVEL", 0, 100, 3, M, tx: true),
        Pick("PR1", "MIC EQ", 1, M, OffOn, tx: true),
        Lvl("PC", "RF POWER", 5, maxWatts, 3, M, tx: true, unit: " W"),
        Pick("ML0", "MONITOR", 3, M, OffOn, tx: true),
        Lvl("ML1", "MONITOR LEVEL", 0, 100, 3, M, tx: true),
        Lvl("VG", "VOX GAIN", 0, 100, 3, M, tx: true),
        Pick("BI", "BREAK-IN", 1, M, OffOn, tx: true, onlyIn: "CW"),
        Pick("KR", "KEYER", 1, M, OffOn, tx: true, onlyIn: "CW"),
    ];

    private static readonly IReadOnlyList<RadioKnob> Ftdx101Mp = Ftdx101(200);
    private static readonly IReadOnlyList<RadioKnob> Ftdx101D = Ftdx101(100);

    /// <summary>The knobs for a model, or null if YWC saves none for it.</summary>
    public static IReadOnlyList<RadioKnob>? TableFor(string? radioModel) => radioModel switch
    {
        "FTdx101MP" => Ftdx101Mp,
        "FTdx101D" => Ftdx101D,
        _ => null,
    };

    /// <summary>The modes the knobs are read in for a model.</summary>
    public static IReadOnlyList<RadioKnobMode> ModesFor(string? radioModel) =>
        TableFor(radioModel) is null ? [] : Ftdx101Modes;

    /// <summary>The address of a per-mode knob: "knob:2:MG".</summary>
    public static string Address(RadioKnobMode mode, RadioKnob knob) => new RadioKnobSlot(knob, mode, null).Address;

    public static bool IsKnobAddress(string? address) =>
        address is not null && address.StartsWith(AddressPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The knob, mode and band an address is for, or false if it is not one on
    /// this radio: the knob must exist, have a mode exactly when the radio keeps
    /// it per mode, and a band exactly when it keeps it per band.
    /// </summary>
    public static bool TryParseAddress(string? address, string? radioModel, out RadioKnobSlot slot)
    {
        slot = null!;
        if (!IsKnobAddress(address)) return false;
        var rest = address![AddressPrefix.Length..];
        string? band = null;
        int at = rest.IndexOf('@');
        if (at >= 0)
        {
            band = Bands.FirstOrDefault(b => b == rest[(at + 1)..]);
            if (band is null) return false;
            rest = rest[..at];
        }
        var parts = rest.Split(':');
        if (parts.Length > 2) return false;
        var knob = TableFor(radioModel)?.FirstOrDefault(x => x.Command == parts[^1]);
        if (knob is null || knob.PerBand != (band is not null)) return false;
        RadioKnobMode? mode = null;
        if (parts.Length == 2)
        {
            mode = ModesFor(radioModel).FirstOrDefault(x => x.Code == parts[0]);
            if (mode is null || !knob.AppliesIn(mode)) return false;
        }
        if (knob.PerMode != (mode is not null)) return false;
        slot = new RadioKnobSlot(knob, mode, band);
        return true;
    }

    /// <summary>Is this one of the front panel sections, "[Front panel]" or "[Front panel > USB]"?</summary>
    public static bool IsKnobSection(string section) =>
        section.Equals(SectionName, StringComparison.OrdinalIgnoreCase)
        || section.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The mode a "Front panel > USB" section is for, or null.</summary>
    public static RadioKnobMode? ModeForSection(string section, string? radioModel)
    {
        if (!section.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var name = section[SectionPrefix.Length..].Trim();
        return Ftdx101Modes.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static string ReadCommand(RadioKnob knob) => (knob.Query ?? knob.Command) + ";";

    /// <summary>
    /// The value from an answer such as "MG050;", or null if the answer is not
    /// for this knob or is not the right number of digits.
    /// </summary>
    public static int? ParseAnswer(RadioKnob knob, string? answer)
    {
        if (answer is null) return null;
        var a = answer.Trim().TrimEnd(';');
        if (!a.StartsWith(knob.Command, StringComparison.Ordinal)) return null;
        var digits = a[knob.Command.Length..];
        if (digits.Length != knob.Width || !digits.All(char.IsAsciiDigit)) return null;
        var value = int.Parse(digits, CultureInfo.InvariantCulture);
        return knob.Fold is null ? value : knob.Fold(value);
    }

    /// <summary>The set command, e.g. "MG050;". Refuses a value the radio does not take.</summary>
    public static string SetCommand(RadioKnob knob, int value)
    {
        if (value < knob.Min || value > knob.Max)
            throw new ArgumentOutOfRangeException(nameof(value), $"{knob.Name} takes {knob.Min} to {knob.Max}, not {value}.");
        return knob.Command + value.ToString(CultureInfo.InvariantCulture).PadLeft(knob.Width, '0') + ";";
    }

    private static readonly Regex Number = new(@"^\s*(-?\d+)\s*(?:[A-Za-z]+)?\s*$", RegexOptions.Compiled);

    /// <summary>
    /// What to send for a value as the file shows it ("50", "50 W", "630 Hz",
    /// "ON"), or null with the reason if the radio would not take it.
    /// </summary>
    public static int? Encode(RadioKnob knob, string shown, out string? error)
    {
        error = null;
        var text = shown.Trim();
        if (knob.Choice)
        {
            for (int v = knob.Min; v <= knob.Max; v++)
                if (knob.Show(v).Equals(text, StringComparison.OrdinalIgnoreCase)) return v;
            error = $"\"{text}\" is not a {knob.Name} setting. Use {string.Join(" or ", Enumerable.Range(knob.Min, knob.Max - knob.Min + 1).Select(knob.Show))}.";
            return null;
        }
        var m = Number.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            && (n - knob.Offset) % knob.Step == 0)
        {
            var v = (n - knob.Offset) / knob.Step;
            if (v >= knob.Min && v <= knob.Max) return v;
        }
        error = knob.Step == 1
            ? $"\"{text}\" is not a {knob.Name} level. Use {knob.Show(knob.Min)} to {knob.Show(knob.Max)}."
            : $"\"{text}\" is not a {knob.Name} level. Use {knob.Show(knob.Min)} to {knob.Show(knob.Max)}, in steps of {knob.Step}.";
        return null;
    }

    public static string ParseModeAnswer(string? answer) =>
        answer is not null && answer.Trim().TrimEnd(';') is { Length: 4 } a && a.StartsWith("MD0", StringComparison.Ordinal)
            ? a[3..] : "";

    /// <summary>The band of an "FA014074400;" answer, or null if it is not one.</summary>
    public static string? ParseBandAnswer(string? answer)
    {
        if (answer is null) return null;
        var a = answer.Trim().TrimEnd(';');
        if (a.Length != 11 || !a.StartsWith("FA", StringComparison.Ordinal) || !a[2..].All(char.IsAsciiDigit)) return null;
        return BandOf(long.Parse(a[2..], CultureInfo.InvariantCulture));
    }

    /// <summary>The band name a frequency is in, as the radio's band keys split them; "GEN" outside the amateur bands.</summary>
    public static string BandOf(long hz) => (hz / 1000) switch
    {
        >= 1_800 and <= 2_000 => "160m",
        >= 3_400 and <= 4_100 => "80m",
        >= 5_250 and <= 5_450 => "60m",
        >= 6_900 and <= 7_400 => "40m",
        >= 10_000 and <= 10_200 => "30m",
        >= 13_900 and <= 14_400 => "20m",
        >= 18_000 and <= 18_200 => "17m",
        >= 20_900 and <= 21_500 => "15m",
        >= 24_800 and <= 25_000 => "12m",
        >= 27_900 and <= 29_800 => "10m",
        >= 49_900 and <= 54_100 => "6m",
        >= 69_900 and <= 71_600 => "4m",
        _ => "GEN",
    };

    /// <summary>
    /// The knob part of the save file: a [Front panel] section for the settings
    /// not kept per mode, then one [Front panel > mode] section per mode.
    /// </summary>
    public static string ToIni(IEnumerable<RadioKnobReading> readings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine()
          .AppendLine("; Front panel: settings made with the knobs and buttons, not in the menu.")
          .AppendLine("; Some are one setting for the whole radio. The radio keeps others for each band,")
          .AppendLine("; each mode, or each mode on each band. Those are saved for the band the radio")
          .AppendLine("; was on, shown after the name, e.g. IPO (20m), and load only on that band.")
          .AppendLine("; AF GAIN (the volume) and the frequency are not saved.")
          .AppendLine("; Each line is: setting = value as the radio shows it   ; CAT command = what the radio answered");
        string? section = null;
        foreach (var r in readings.OrderBy(r => r.Mode is null ? 0 : 1))
        {
            var want = r.Mode is null ? SectionName : SectionPrefix + r.Mode.Name;
            if (want != section)
            {
                sb.AppendLine().AppendLine($"[{want}]");
                section = want;
            }
            var name = r.Band is null ? r.Knob.Name : $"{r.Knob.Name} ({r.Band})";
            var line = $"{name} = {(r.Value is int v ? r.Knob.Show(v) : RadioMenuLoad.NoAnswer)}";
            var note = r.Value is int raw
                ? $"{r.Knob.Command} = {raw.ToString(CultureInfo.InvariantCulture).PadLeft(r.Knob.Width, '0')}"
                : r.Knob.Command;
            if (r.Band is not null) note += $" @ {r.Band}";
            sb.Append(line.PadRight(40)).Append(" ; ").Append(note);
            if (r.Knob.Tx) sb.Append("  [TX]");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>What the knob pass needs from YWC: ask the radio, tell it, and whether it is transmitting.</summary>
public interface IRadioKnobLink
{
    /// <summary>Sends a read and returns the answer, or null if none came.</summary>
    Task<string?> AskAsync(string command, CancellationToken ct);

    /// <summary>Sends a set, which the radio does not answer.</summary>
    Task TellAsync(string command, CancellationToken ct);

    bool Transmitting { get; }
}

/// <summary>
/// Switches the main band through modes and back. Never changes mode while
/// the radio transmits: it stops before the next mode, and waits for receive
/// before putting the first mode back.
/// </summary>
public sealed class RadioKnobPass
{
    public const int Attempts = 3;

    private readonly IRadioKnobLink _link;
    private readonly int _settleMs;
    private readonly TimeSpan _txWait;

    public RadioKnobPass(IRadioKnobLink link, int settleMs = 150, TimeSpan? txWait = null)
    {
        _link = link;
        _settleMs = settleMs;
        _txWait = txWait ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>Why the pass stopped early, or null if it went through every mode.</summary>
    public string? Stopped { get; private set; }

    /// <summary>
    /// Runs <paramref name="inMode"/> once in each mode, then puts the main
    /// band back in the mode it started in. Returns false, with
    /// <see cref="Stopped"/> set, if it could not start or stopped early.
    /// </summary>
    public async Task<bool> RunAsync(IEnumerable<RadioKnobMode> modes, Func<RadioKnobMode, Task> inMode, CancellationToken ct)
    {
        Stopped = null;
        if (_link.Transmitting)
        {
            Stopped = "The radio is transmitting, so YWC did not change mode.";
            return false;
        }
        var start = await ReadModeAsync(ct);
        if (start.Length == 0)
        {
            Stopped = "The radio did not say which mode it is in, so YWC did not change mode.";
            return false;
        }
        var current = start;
        try
        {
            foreach (var mode in modes)
            {
                if (_link.Transmitting)
                {
                    Stopped = $"The radio started transmitting, so YWC stopped before {mode.Name}.";
                    return false;
                }
                if (current != mode.Code)
                {
                    await _link.TellAsync($"MD0{mode.Code};", ct);
                    if (_settleMs > 0) await Task.Delay(_settleMs, ct);
                    current = await ReadModeAsync(ct);
                    if (current != mode.Code)
                    {
                        Stopped = $"The radio did not change to {mode.Name}.";
                        return false;
                    }
                }
                await inMode(mode);
            }
            return true;
        }
        finally
        {
            if (current != start)
            {
                // Put the mode back even if the page went away mid-pass.
                var until = DateTime.UtcNow + _txWait;
                while (_link.Transmitting && DateTime.UtcNow < until)
                    await Task.Delay(200, CancellationToken.None);
                if (!_link.Transmitting)
                    await _link.TellAsync($"MD0{start};", CancellationToken.None);
                else
                {
                    const string stuck = "The radio is still transmitting, so YWC could not put the mode back.";
                    Stopped = Stopped is null ? stuck : $"{Stopped} {stuck}";
                }
            }
        }
    }

    public async Task<int?> ReadAsync(RadioKnob knob, CancellationToken ct)
    {
        for (int i = 0; i < Attempts; i++)
        {
            var value = RadioKnobs.ParseAnswer(knob, await _link.AskAsync(RadioKnobs.ReadCommand(knob), ct));
            if (value is not null) return value;
        }
        return null;
    }

    /// <summary>The main band's MD0 code now, or "" if the radio did not say.</summary>
    public async Task<string> ReadModeAsync(CancellationToken ct)
    {
        for (int i = 0; i < Attempts; i++)
        {
            var code = RadioKnobs.ParseModeAnswer(await _link.AskAsync("MD0;", ct));
            if (code.Length > 0) return code;
        }
        return "";
    }

    /// <summary>The band the main VFO is on now ("20m"), or null if the radio did not say.</summary>
    public async Task<string?> ReadBandAsync(CancellationToken ct)
    {
        for (int i = 0; i < Attempts; i++)
        {
            var band = RadioKnobs.ParseBandAnswer(await _link.AskAsync("FA;", ct));
            if (band is not null) return band;
        }
        return null;
    }
}
