namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>
/// One item of a radio's menu, reached with EX.
/// </summary>
/// <param name="Address">P1 P2 P3, six digits, e.g. "010306".</param>
/// <param name="Section">P1's name on the radio, e.g. "RADIO SETTING".</param>
/// <param name="Group">P2's name, e.g. "MODE FM".</param>
/// <param name="Function">The item's name as the radio shows it.</param>
/// <param name="Decode">Turns the answer's value into what the radio displays.</param>
/// <param name="Tx">True when the item changes how or when the radio transmits.
/// Load shows these unticked, so they are only written when asked for.</param>
/// <param name="Values">The values the radio accepts, as numbers ("+05" is 5).
/// Null for a text item, or for one Load cannot write.</param>
/// <param name="TextMax">For a text item such as MY CALL, the most characters it takes.</param>
/// <param name="Link">True for the items that set the CAT link itself (baud rate,
/// time-out, RTS). Load never writes these: a wrong one would cut YWC off the radio.</param>
public sealed record RadioMenuItem(
    string Address,
    string Section,
    string Group,
    string Function,
    Func<string, string> Decode,
    bool Tx = false,
    IReadOnlyList<int>? Values = null,
    int TextMax = 0,
    bool Link = false);

/// <summary>An item and what the radio answered for it (null if it did not).</summary>
public sealed record RadioMenuReading(RadioMenuItem Item, string? Raw)
{
    public string Value => Raw is null ? "" : Item.Decode(Raw);
}

/// <summary>
/// EX traffic for the menu save and load. A read is "EX" plus the six-digit
/// address and nothing else, and <see cref="ReadCommand"/> refuses anything
/// that is not exactly that. A set adds the value; <see cref="SetCommand"/>
/// only takes values that <see cref="RadioMenuLoad"/> has checked.
/// </summary>
public static class RadioMenuCat
{
    /// <summary>The menu table for a model, or null if YWC has none for it yet.</summary>
    public static IReadOnlyList<RadioMenuItem>? TableFor(string? radioModel) => radioModel switch
    {
        "FTdx101MP" => Ftdx101Menu.Items,
        "FTdx101D" => Ftdx101Menu.ItemsD,
        _ => null,
    };

    public static string ReadCommand(string address)
    {
        if (address.Length != 6 || !address.All(char.IsAsciiDigit))
            throw new ArgumentException($"Not a six-digit EX address: '{address}'", nameof(address));
        return $"EX{address};";
    }

    /// <summary>EX P1 P2 P3 P4; — sets one item. The radio does not answer a set.</summary>
    public static string SetCommand(string address, string value)
    {
        ReadCommand(address);
        if (value.Length == 0 || value.Any(c => c < ' ' || c > '~' || c == ';'))
            throw new ArgumentException($"Not a value EX can send: '{value}'", nameof(value));
        return $"EX{address}{value};";
    }

    /// <summary>
    /// The value from an EX answer, or null if the answer is not for this
    /// address. EX replies are keyed on "EX" alone, so another item's late
    /// answer could otherwise be read as this one.
    /// </summary>
    public static string? ParseAnswer(string address, string? answer)
    {
        if (answer is null) return null;
        var a = answer.Trim().TrimEnd(';');
        if (!a.StartsWith("EX" + address, StringComparison.Ordinal)) return null;
        var value = a.Substring(2 + address.Length);
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// The save file, an .ini that reads in Notepad: one [section] per menu
    /// group and one line per item, the value as the radio shows it, then a
    /// comment with the EX address and raw value so a later Load can put each
    /// item back exactly.
    /// </summary>
    public static string ToIni(string radioModel, DateTime takenLocal, IEnumerable<RadioMenuReading> readings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("; YWC radio menu: the radio's own menu settings.")
          .AppendLine("; Each line is: item = value as the radio shows it   ; EX address = what the radio answered")
          .AppendLine("; [TX] marks items that change how or when the radio transmits.")
          .AppendLine()
          .AppendLine("[File]")
          .AppendLine("Program = YWC")
          .AppendLine($"Radio = {radioModel}")
          .AppendLine($"Saved = {takenLocal:yyyy-MM-dd HH:mm}");
        string? header = null;
        foreach (var r in readings)
        {
            var h = $"[{r.Item.Section} > {r.Item.Group}]";
            if (h != header)
            {
                sb.AppendLine().AppendLine(h);
                header = h;
            }
            var line = $"{r.Item.Function} = {(r.Raw is null ? "(no answer)" : r.Value)}";
            var note = r.Raw is null ? r.Item.Address : $"{r.Item.Address} = {r.Raw}";
            sb.Append(line.PadRight(40)).Append(" ; ").Append(note);
            if (r.Item.Tx) sb.Append("  [TX]");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
