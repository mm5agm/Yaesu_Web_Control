using System.Globalization;
using System.Text.RegularExpressions;

namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>
/// A front-panel level saved with the radio menu: a knob or button the menu
/// does not hold, such as MIC GAIN or the AMC level. Read and set with its
/// own CAT command, a fixed number of digits.
/// </summary>
/// <param name="Command">The CAT command, e.g. "MG", or "PR0" for one that takes a selector.</param>
/// <param name="Name">The name the radio uses for it.</param>
/// <param name="Min">The lowest value the radio takes.</param>
/// <param name="Max">The highest value the radio takes.</param>
/// <param name="Width">How many digits the value has on the wire.</param>
/// <param name="Show">Turns the value into what the radio shows.</param>
public sealed record RadioKnob(string Command, string Name, int Min, int Max, int Width, Func<int, string> Show);

/// <summary>A main-band mode the knobs are read in: its MD code and its name on the radio.</summary>
public sealed record RadioKnobMode(string Code, string Name);

/// <summary>A knob, the mode it was read in, and what the radio answered (null if it did not).</summary>
public sealed record RadioKnobReading(RadioKnobMode Mode, RadioKnob Knob, int? Value);

/// <summary>
/// The front-panel part of the radio menu save. The radio keeps some of these
/// levels for each mode, so each one is read in every mode: YWC switches the
/// main band through the modes and puts the mode back afterwards. Nothing
/// here transmits; every command is a read, a mode change or a level.
/// </summary>
public static class RadioKnobs
{
    /// <summary>The start of each knob [section]: "[Front panel > USB]".</summary>
    public const string SectionPrefix = "Front panel > ";

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

    private static string Level(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyList<RadioKnob> Ftdx101(int maxWatts) =>
    [
        new("MG", "MIC GAIN", 0, 100, 3, Level),
        new("AO", "AMC LEVEL", 1, 100, 3, Level),
        new("PR0", "PROC", 1, 2, 1, v => v == 2 ? "ON" : "OFF"),
        new("PL", "PROC LEVEL", 0, 100, 3, Level),
        new("PC", "RF POWER", 5, maxWatts, 3, v => $"{v} W"),
        new("ML1", "MONITOR LEVEL", 0, 100, 3, Level),
        new("VG", "VOX GAIN", 0, 100, 3, Level),
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

    public static string Address(RadioKnobMode mode, RadioKnob knob) => $"{AddressPrefix}{mode.Code}:{knob.Command}";

    public static bool IsKnobAddress(string? address) =>
        address is not null && address.StartsWith(AddressPrefix, StringComparison.Ordinal);

    /// <summary>The mode and knob a "knob:2:MG" address is for, or false if it is not one on this radio.</summary>
    public static bool TryParseAddress(string? address, string? radioModel, out RadioKnobMode mode, out RadioKnob knob)
    {
        mode = null!;
        knob = null!;
        if (!IsKnobAddress(address)) return false;
        var parts = address![AddressPrefix.Length..].Split(':');
        if (parts.Length != 2) return false;
        var m = ModesFor(radioModel).FirstOrDefault(x => x.Code == parts[0]);
        var k = TableFor(radioModel)?.FirstOrDefault(x => x.Command == parts[1]);
        if (m is null || k is null) return false;
        mode = m;
        knob = k;
        return true;
    }

    /// <summary>The mode a "Front panel > USB" section is for, or null.</summary>
    public static RadioKnobMode? ModeForSection(string section, string? radioModel)
    {
        if (!section.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var name = section[SectionPrefix.Length..].Trim();
        return Ftdx101Modes.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static string ReadCommand(RadioKnob knob) => knob.Command + ";";

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
        return int.Parse(digits, CultureInfo.InvariantCulture);
    }

    /// <summary>The set command, e.g. "MG050;". Refuses a value the radio does not take.</summary>
    public static string SetCommand(RadioKnob knob, int value)
    {
        if (value < knob.Min || value > knob.Max)
            throw new ArgumentOutOfRangeException(nameof(value), $"{knob.Name} takes {knob.Min} to {knob.Max}, not {value}.");
        return knob.Command + value.ToString(CultureInfo.InvariantCulture).PadLeft(knob.Width, '0') + ";";
    }

    private static readonly Regex Number = new(@"^\s*(\d+)\s*(?:W)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// What to send for a value as the file shows it ("50", "50 W", "ON"),
    /// or null with the reason if the radio would not take it.
    /// </summary>
    public static int? Encode(RadioKnob knob, string shown, out string? error)
    {
        error = null;
        var text = shown.Trim();
        for (int v = knob.Min; v <= knob.Max && knob.Max - knob.Min < 4; v++)
            if (knob.Show(v).Equals(text, StringComparison.OrdinalIgnoreCase)) return v;
        var m = Number.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n >= knob.Min && n <= knob.Max && knob.Max - knob.Min >= 4)
            return n;
        error = knob.Max - knob.Min < 4
            ? $"\"{text}\" is not a {knob.Name} setting. Use {string.Join(" or ", Enumerable.Range(knob.Min, knob.Max - knob.Min + 1).Select(knob.Show))}."
            : $"\"{text}\" is not a {knob.Name} level. Use {knob.Min} to {knob.Max}.";
        return null;
    }

    public static string ParseModeAnswer(string? answer) =>
        answer is not null && answer.Trim().TrimEnd(';') is { Length: 4 } a && a.StartsWith("MD0", StringComparison.Ordinal)
            ? a[3..] : "";

    /// <summary>The knob part of the save file, one [Front panel > mode] section per mode.</summary>
    public static string ToIni(IEnumerable<RadioKnobReading> readings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine()
          .AppendLine("; Front panel: levels set with the knobs and buttons, not in the menu.")
          .AppendLine("; The radio keeps some of them for each mode, so each is read in every mode.")
          .AppendLine("; Each line is: level = value as the radio shows it   ; CAT command = what the radio answered");
        string? mode = null;
        foreach (var r in readings)
        {
            if (r.Mode.Name != mode)
            {
                sb.AppendLine().AppendLine($"[{SectionPrefix}{r.Mode.Name}]");
                mode = r.Mode.Name;
            }
            var line = $"{r.Knob.Name} = {(r.Value is int v ? r.Knob.Show(v) : RadioMenuLoad.NoAnswer)}";
            var note = r.Value is int raw
                ? $"{r.Knob.Command} = {raw.ToString(CultureInfo.InvariantCulture).PadLeft(r.Knob.Width, '0')}"
                : r.Knob.Command;
            sb.Append(line.PadRight(40)).Append(" ; ").Append(note).AppendLine("  [TX]");
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

    private async Task<string> ReadModeAsync(CancellationToken ct)
    {
        for (int i = 0; i < Attempts; i++)
        {
            var code = RadioKnobs.ParseModeAnswer(await _link.AskAsync("MD0;", ct));
            if (code.Length > 0) return code;
        }
        return "";
    }
}
