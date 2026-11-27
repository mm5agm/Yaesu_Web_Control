namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>The newest radio menu file for this radio: its name, when it was written, and its text.</summary>
public sealed record RadioMenuSaveInfo(string Name, DateTime Modified, string Text);

/// <summary>One front-panel level that is not what the newest file holds, and in which mode.</summary>
public sealed record RadioMenuChange(string Name, string Now, string Saved, string Mode);

/// <summary>
/// What the reminder should say. Kind is one of:
/// none (no file yet), changed (a level differs from the newest file),
/// old (the newest file is <see cref="RadioMenuReminder.OldAfterDays"/> days old or more),
/// noknobs (the newest file was saved before YWC saved the front panel levels),
/// ok (nothing to say).
/// </summary>
public sealed record RadioMenuReminderResult(string Kind, int? Days, string? File, string? Mode, IReadOnlyList<RadioMenuChange> Changes);

/// <summary>
/// Decides whether to remind the user to save the radio menu. Pure: the
/// controller finds the newest file and reads the levels in the radio's
/// current mode, so this needs no radio and no disk.
///
/// Only the current mode can be compared: reading the others would mean
/// switching modes, which a reminder must never do. Menu items are not
/// compared either (reading them all takes seconds); the age check covers
/// changes made on the radio's own menu.
/// </summary>
public static class RadioMenuReminder
{
    public const int OldAfterDays = 30;

    /// <param name="newest">The newest file for this radio, or null if there is none.</param>
    /// <param name="modeCode">The main band's MD0 code now, or null if it was not read.</param>
    /// <param name="live">The levels read now, by command (MG, AO, ...); null where the radio did not answer.</param>
    public static RadioMenuReminderResult Check(RadioMenuSaveInfo? newest, DateTime now, string? model,
        string? modeCode, IReadOnlyDictionary<string, int?> live)
    {
        if (newest is null)
            return new("none", null, null, null, Array.Empty<RadioMenuChange>());

        int days = Math.Max(0, (int)(now - newest.Modified).TotalDays);
        var file = RadioMenuLoad.Parse(newest.Text);
        var knobLines = file.Items.Where(i => RadioKnobs.IsKnobAddress(i.Address)).ToList();
        var table = RadioKnobs.TableFor(model);

        string? modeName = null;
        var changes = new List<RadioMenuChange>();
        if (table is not null && !string.IsNullOrEmpty(modeCode))
        {
            modeName = RadioKnobs.ModesFor(model).FirstOrDefault(m => m.Code == modeCode)?.Name;
            changes = Compare(file, model, live.Select(l => (modeCode, l.Key, l.Value)));
        }

        string kind = changes.Count > 0 ? "changed"
            : days >= OldAfterDays ? "old"
            : table is not null && knobLines.Count == 0 ? "noknobs"
            : "ok";
        return new(kind, days, newest.Name, modeName, changes);
    }

    /// <summary>
    /// The levels that differ from the file, each with its mode. A level the
    /// file has no line for, or the radio did not answer, is not a change.
    /// </summary>
    public static List<RadioMenuChange> Compare(RadioMenuFile file, string? model,
        IEnumerable<(string Mode, string Command, int? Value)> levels)
    {
        var changes = new List<RadioMenuChange>();
        var table = RadioKnobs.TableFor(model);
        if (table is null) return changes;
        var saved = file.Items.Where(i => RadioKnobs.IsKnobAddress(i.Address))
            .GroupBy(i => i.Address!).ToDictionary(g => g.Key, g => g.First());
        foreach (var (modeCode, command, value) in levels)
        {
            if (value is not int now) continue;
            var mode = RadioKnobs.ModesFor(model).FirstOrDefault(m => m.Code == modeCode);
            var knob = table.FirstOrDefault(k => k.Command == command);
            if (mode is null || knob is null) continue;
            // The value as the file shows it: what Load would set, even if edited by hand.
            if (!saved.TryGetValue(RadioKnobs.Address(mode, knob), out var line)) continue;
            if (RadioKnobs.Encode(knob, line.Value, out _) is not int was || was == now) continue;
            changes.Add(new(knob.Name, knob.Show(now), knob.Show(was), mode.Name));
        }
        return changes;
    }

    /// <summary>
    /// Closing YWC: should it ask to save first? Asks when a front-panel level
    /// is not what the newest file holds, in the mode read just now or in any
    /// mode the user changed one in while YWC ran. With no file at all it asks
    /// only if a level was changed this session (the start already asked).
    /// </summary>
    /// <param name="touched">Levels the user changed this session (<see cref="RadioMenuSessionChanges.Touched"/>).</param>
    /// <param name="modeCode">The main band's MD0 code read just now, or null if it could not be read.</param>
    /// <param name="live">The levels read just now, by command; empty if they could not be read.</param>
    public static (bool Ask, string Message, IReadOnlyList<RadioMenuChange> Changes) ExitCheck(
        RadioMenuSaveInfo? newest, DateTime now, string? model,
        IReadOnlyList<(string Mode, string Command, int Value)> touched,
        string? modeCode, IReadOnlyDictionary<string, int?> live)
    {
        if (RadioKnobs.TableFor(model) is null)
            return (false, "", Array.Empty<RadioMenuChange>());

        if (newest is null)
        {
            if (touched.Count == 0) return (false, "", Array.Empty<RadioMenuChange>());
            var names = touched.Select(t => Name(model, t.Mode, t.Command)).Distinct();
            return (true, $"You changed {string.Join(", ", names)} while YWC was running, and the radio's settings have never been saved.",
                Array.Empty<RadioMenuChange>());
        }

        // Read just now wins over the session's last report for the same mode and level.
        var levels = new List<(string Mode, string Command, int? Value)>();
        if (!string.IsNullOrEmpty(modeCode))
            levels.AddRange(live.Select(l => (modeCode!, l.Key, l.Value)));
        levels.AddRange(touched
            .Where(t => !levels.Any(l => l.Mode == t.Mode && l.Command == t.Command))
            .Select(t => (t.Mode, t.Command, (int?)t.Value)));

        var changes = Compare(RadioMenuLoad.Parse(newest.Text), model, levels);
        if (changes.Count == 0) return (false, "", changes);
        int days = Math.Max(0, (int)(now - newest.Modified).TotalDays);
        return (true, $"Since your last save ({days} day{(days == 1 ? "" : "s")} ago), {Describe(changes)}.", changes);
    }

    /// <summary>"MIC GAIN in USB", for a level the file does not have yet.</summary>
    private static string Name(string? model, string modeCode, string command)
    {
        var knob = RadioKnobs.TableFor(model)?.FirstOrDefault(k => k.Command == command);
        var mode = RadioKnobs.ModesFor(model).FirstOrDefault(m => m.Code == modeCode);
        return $"{knob?.Name ?? command} in {mode?.Name ?? modeCode}";
    }

    /// <summary>"MIC GAIN changed from 50 to 0 in USB, RF POWER changed from 100 W to 50 W in DATA-U"</summary>
    public static string Describe(IEnumerable<RadioMenuChange> changes) =>
        string.Join(", ", changes.Select(c => $"{c.Name} changed from {c.Saved} to {c.Now} in {c.Mode}"));

    /// <summary>One line for the banner and the Settings page.</summary>
    public static string Message(RadioMenuReminderResult r) => r.Kind switch
    {
        "none" => "Your radio's settings have not been saved yet. A saved copy lets you put the menu and the front panel levels back if they get changed.",
        "changed" => $"Since your last save ({r.Days} day{(r.Days == 1 ? "" : "s")} ago), {Describe(r.Changes)}.",
        "old" => $"Your last save of the radio's settings was {r.Days} days ago.",
        "noknobs" => "Your last save was made before YWC saved the front panel levels (MIC GAIN, AMC, PROC, power). Save again to include them.",
        _ => "",
    };
}
