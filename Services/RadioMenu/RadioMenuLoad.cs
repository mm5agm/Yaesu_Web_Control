using System.Text.RegularExpressions;

namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>One item line of a radio menu file.</summary>
/// <param name="Line">1-based line number, for messages.</param>
/// <param name="Section">The [section] it sits under, e.g. "RADIO SETTING > MODE SSB".</param>
/// <param name="Name">The item's name, left of "=".</param>
/// <param name="Value">What the user wants, right of "=" and before ";".</param>
/// <param name="Address">The EX address from the comment, if there is one.</param>
/// <param name="SavedRaw">What the radio answered when the file was saved, if known.</param>
public sealed record RadioMenuFileItem(int Line, string Section, string Name, string Value, string? Address, string? SavedRaw);

/// <summary>A radio menu file as read back: the [File] header and the item lines.</summary>
public sealed record RadioMenuFile(string? Program, string? Radio, IReadOnlyList<RadioMenuFileItem> Items);

/// <summary>
/// Reads a radio menu .ini and turns each value back into what EX sends.
///
/// The value as the radio shows it wins: a user edits "SSB OUT LEVEL = 50"
/// and never needs to touch the raw value in the comment. The saved raw value
/// is only used when the shown value is still what it decodes to, so an
/// unedited line goes back exactly as it was read.
///
/// A value is encoded by trying every value the item accepts, in the width
/// and sign the radio used for its current answer, and keeping the one that
/// decodes to the text. So nothing can be written that the table does not
/// list, and no width table is needed.
/// </summary>
public static class RadioMenuLoad
{
    private static readonly Regex SectionLine = new(@"^\[([^\]]*)\]$", RegexOptions.Compiled);
    private static readonly Regex Note = new(@"^\s*(\d{6})(?:\s*=\s*(.*?))?\s*(?:\[TX\])?\s*$", RegexOptions.Compiled);
    private static readonly Regex NumberWithUnit = new(@"^([+-]?\d+(?:\.\d+)?)\s*[A-Za-z/]+$", RegexOptions.Compiled);

    public const string NoAnswer = "(no answer)";

    public static RadioMenuFile Parse(string text)
    {
        string? program = null, radio = null;
        string section = "";
        var items = new List<RadioMenuFileItem>();
        var lines = text.TrimStart('﻿').Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            var m = SectionLine.Match(line);
            if (m.Success)
            {
                section = m.Groups[1].Value.Trim();
                continue;
            }
            int semi = line.IndexOf(';');
            var left = semi < 0 ? line : line[..semi];
            var comment = semi < 0 ? "" : line[(semi + 1)..];
            int eq = left.IndexOf('=');
            if (eq < 0) continue;
            var name = left[..eq].Trim();
            var value = left[(eq + 1)..].Trim();
            if (section.Equals("File", StringComparison.OrdinalIgnoreCase))
            {
                if (name.Equals("Program", StringComparison.OrdinalIgnoreCase)) program = value;
                else if (name.Equals("Radio", StringComparison.OrdinalIgnoreCase)) radio = value;
                continue;
            }
            var note = Note.Match(comment);
            string? address = note.Success ? note.Groups[1].Value : null;
            string? raw = note.Success && note.Groups[2].Success && note.Groups[2].Value.Length > 0 ? note.Groups[2].Value : null;
            items.Add(new RadioMenuFileItem(n + 1, section, name, value, address, raw));
        }
        return new RadioMenuFile(program, radio, items);
    }

    /// <summary>The table item a file line is for: by address, else by section and name.</summary>
    public static RadioMenuItem? Find(IReadOnlyList<RadioMenuItem> table, RadioMenuFileItem line)
    {
        if (line.Address is not null)
        {
            var byAddress = table.FirstOrDefault(i => i.Address == line.Address);
            if (byAddress is not null) return byAddress;
        }
        return table.FirstOrDefault(i =>
            Same($"{i.Section} > {i.Group}", line.Section) && Same(i.Function, line.Name));
    }

    /// <summary>
    /// What to send for <paramref name="text"/>, or null with a reason.
    /// <paramref name="currentRaw"/> is the radio's answer now; its width and
    /// sign are the ones used.
    /// </summary>
    public static string? Encode(RadioMenuItem item, string text, string? savedRaw, string currentRaw, out string? error)
    {
        error = null;
        if (item.Link)
        {
            error = "Not loaded: this sets the CAT link itself, so change it on the radio.";
            return null;
        }
        text = text.Trim();
        if (item.Values is null)
            return item.TextMax > 0 ? EncodeText(item, text, out error) : Fail(out error, "Load cannot set this item.");

        bool signed = currentRaw.StartsWith('+') || currentRaw.StartsWith('-');
        int width = currentRaw.Length - (signed ? 1 : 0);
        if (width < 1 || !int.TryParse(currentRaw, out _))
            return Fail(out error, $"The radio's answer '{currentRaw}' is not a number, so Load cannot set it.");

        string? Format(int v)
        {
            if (v < 0 && !signed) return null;
            var digits = Math.Abs(v).ToString().PadLeft(width, '0');
            if (digits.Length > width) return null;
            return signed ? (v < 0 ? "-" : "+") + digits : digits;
        }

        // The saved raw value, when the line has not been edited and it fits the radio now.
        if (savedRaw is not null && int.TryParse(savedRaw, out var saved) && item.Values.Contains(saved)
            && Format(saved) == savedRaw && Matches(text, item.Decode(savedRaw), loose: false))
            return savedRaw;

        var candidates = item.Values.Select(Format).Where(r => r is not null).Cast<string>().ToList();
        var hit = candidates.FirstOrDefault(r => Matches(text, item.Decode(r), loose: false))
               ?? candidates.FirstOrDefault(r => Matches(text, item.Decode(r), loose: true));
        if (hit is not null) return hit;

        var shown = candidates.Select(item.Decode).ToList();
        var list = shown.Count <= 12
            ? string.Join(", ", shown)
            : $"{shown[0]}, {shown[1]}, {shown[2]} … {shown[^1]}";
        return Fail(out error, $"'{text}' is not a setting the radio offers. It takes: {list}.");
    }

    /// <summary>
    /// Checks a value sent back by the page before it goes to the radio: one
    /// the item accepts, in the width and sign of the radio's current answer.
    /// The page could send anything, so this is the last word, not the preview.
    /// </summary>
    public static string? CheckRaw(RadioMenuItem item, string raw, string currentRaw, out string? error)
    {
        error = null;
        if (item.Link)
            return Fail(out error, "Not loaded: this sets the CAT link itself, so change it on the radio.");
        if (item.Values is null)
            return item.TextMax > 0 ? EncodeText(item, raw.Trim(), out error) : Fail(out error, "Load cannot set this item.");
        bool signed = currentRaw.StartsWith('+') || currentRaw.StartsWith('-');
        bool rawSigned = raw.StartsWith('+') || raw.StartsWith('-');
        if (!int.TryParse(raw, out var v) || !raw.Skip(rawSigned ? 1 : 0).All(char.IsAsciiDigit)
            || !item.Values.Contains(v) || rawSigned != signed || raw.Length != currentRaw.Length)
            return Fail(out error, "That value does not fit the radio now, so nothing was sent. Preview the file again.");
        return raw;
    }

    private static string? EncodeText(RadioMenuItem item, string text, out string? error)
    {
        error = null;
        var t = text.ToUpperInvariant();
        if (t.Length == 0) return Fail(out error, "Cannot be blank.");
        if (t.Length > item.TextMax) return Fail(out error, $"Too long: at most {item.TextMax} characters.");
        if (t.Any(c => c < ' ' || c > '~' || c == ';'))
            return Fail(out error, "Only letters, numbers and plain symbols (no ';').");
        return t;
    }

    /// <summary>True if the radio's answer after a set is the value sent.</summary>
    public static bool ReadBackMatches(RadioMenuItem item, string sent, string? answer)
    {
        if (answer is null) return false;
        if (item.Values is null) return answer.Trim().Equals(sent.Trim(), StringComparison.OrdinalIgnoreCase);
        return int.TryParse(answer, out var a) && int.TryParse(sent, out var s) && a == s;
    }

    /// <summary>
    /// Case, spaces and a leading "+" do not count. Loose also lets the unit
    /// go: "50" matches "50 W" and "1000" matches "1000 Hz".
    /// </summary>
    private static bool Matches(string text, string shown, bool loose)
    {
        var t = Norm(text);
        if (t == Norm(shown)) return true;
        if (!loose) return false;
        var m = NumberWithUnit.Match(shown.Trim());
        return m.Success && t == Norm(m.Groups[1].Value);
    }

    private static string Norm(string s)
    {
        var t = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        return t.StartsWith('+') ? t[1..] : t;
    }

    private static bool Same(string a, string b) => Norm(a) == Norm(b);

    private static string? Fail(out string? error, string message)
    {
        error = message;
        return null;
    }
}
