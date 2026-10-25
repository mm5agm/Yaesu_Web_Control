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
/// Not used by Save; it marks the items a later Load must not write unasked.</param>
public sealed record RadioMenuItem(
    string Address,
    string Section,
    string Group,
    string Function,
    Func<string, string> Decode,
    bool Tx = false);

/// <summary>An item and what the radio answered for it (null if it did not).</summary>
public sealed record RadioMenuReading(RadioMenuItem Item, string? Raw)
{
    public string Value => Raw is null ? "" : Item.Decode(Raw);
}

/// <summary>
/// Read-only EX traffic for the menu save. There is no set command here on
/// purpose: a read is "EX" plus the six-digit address and nothing else, and
/// <see cref="ReadCommand"/> refuses anything that is not exactly that.
/// </summary>
public static class RadioMenuCat
{
    /// <summary>The menu table for a model, or null if YWC has none for it yet.</summary>
    public static IReadOnlyList<RadioMenuItem>? TableFor(string? radioModel) => radioModel switch
    {
        "FTdx101MP" or "FTdx101D" => Ftdx101Menu.Items,
        _ => null,
    };

    public static string ReadCommand(string address)
    {
        if (address.Length != 6 || !address.All(char.IsAsciiDigit))
            throw new ArgumentException($"Not a six-digit EX address: '{address}'", nameof(address));
        return $"EX{address};";
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
    /// The save file: one row per item, laid out like an operator's own menu
    /// sheet (section, group, function, value), with the EX address and raw
    /// value so a later Load can put each item back exactly.
    /// </summary>
    public static string ToCsv(string radioModel, DateTime takenLocal, IEnumerable<RadioMenuReading> readings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# YWC radio menu,{Csv(radioModel)},{takenLocal:yyyy-MM-dd HH:mm}");
        sb.AppendLine("Section,Group,Function,Value,EX,Raw,TX");
        foreach (var r in readings)
        {
            sb.Append(Csv(r.Item.Section)).Append(',')
              .Append(Csv(r.Item.Group)).Append(',')
              .Append(Csv(r.Item.Function)).Append(',')
              .Append(Csv(r.Raw is null ? "(no answer)" : r.Value)).Append(',')
              .Append(r.Item.Address).Append(',')
              // Written as ="0160" so Excel keeps the leading zeros instead of showing 160.
              .Append(r.Raw is null ? "" : $"=\"{r.Raw}\"").Append(',')
              .Append(r.Item.Tx ? "yes" : "")
              .AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
