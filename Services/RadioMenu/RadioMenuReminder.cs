namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>The newest radio menu file for this radio: its name, when it was written, and its text.</summary>
public sealed record RadioMenuSaveInfo(string Name, DateTime Modified, string Text);

/// <summary>One front-panel level that is not what the newest file holds.</summary>
public sealed record RadioMenuChange(string Name, string Now, string Saved);

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
            foreach (var line in knobLines)
            {
                if (!RadioKnobs.TryParseAddress(line.Address, model, out var mode, out var knob) || mode.Code != modeCode)
                    continue;
                modeName = mode.Name;
                // The value as the file shows it: what Load would set, even if edited by hand.
                if (RadioKnobs.Encode(knob, line.Value, out _) is not int saved) continue;
                if (live.TryGetValue(knob.Command, out var read) && read is int value && value != saved)
                    changes.Add(new(knob.Name, knob.Show(value), knob.Show(saved)));
            }
        }

        string kind = changes.Count > 0 ? "changed"
            : days >= OldAfterDays ? "old"
            : table is not null && knobLines.Count == 0 ? "noknobs"
            : "ok";
        return new(kind, days, newest.Name, modeName, changes);
    }

    /// <summary>One line for the banner and the Settings page.</summary>
    public static string Message(RadioMenuReminderResult r) => r.Kind switch
    {
        "none" => "Your radio's settings have not been saved yet. A saved copy lets you put the menu and the front panel levels back if they get changed.",
        "changed" => $"Since your last save ({r.Days} day{(r.Days == 1 ? "" : "s")} ago), "
            + string.Join(", ", r.Changes.Select(c => $"{c.Name} changed from {c.Saved} to {c.Now}"))
            + (r.Mode is null ? "." : $" in {r.Mode}."),
        "old" => $"Your last save of the radio's settings was {r.Days} days ago.",
        "noknobs" => "Your last save was made before YWC saved the front panel levels (MIC GAIN, AMC, PROC, power). Save again to include them.",
        _ => "",
    };
}
