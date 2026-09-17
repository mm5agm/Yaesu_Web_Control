namespace Yaesu_Web_Control.Services;

/// <summary>
/// Pure keyboard-shortcut rules mirrored from
/// <c>wwwroot/js/ui/keyboard-shortcuts.js</c> (<c>resolveTuneStepHz</c> /
/// <c>resolveBracketTuneStepHz</c> / <c>resolveModeShortcut</c>). Kept in C#
/// so CI can lock the contract without a JS test runner. If you change either
/// side, update both.
/// </summary>
public static class KeyboardShortcutRules
{
    public const int BaseTuneStepHz = 100;

    /// <summary>
    /// j/i/arrow ladder. Fine 1 Hz / 50 Hz use bracket keys separately
    /// (<see cref="ResolveBracketTuneStepHz"/>); Ctrl is not used.
    /// </summary>
    public static int ResolveTuneStepHz(bool shiftKey, bool altKey)
    {
        if (shiftKey && altKey) return 0; // reserved for DX-spot hop
        if (altKey) return 10_000;
        if (shiftKey) return 1_000;
        return BaseTuneStepHz;
    }

    /// <summary>
    /// Physical bracket fine-tune: [ / ] → 1 Hz; Shift+[ / ] → 50 Hz.
    /// </summary>
    public static int ResolveBracketTuneStepHz(bool shiftKey)
        => shiftKey ? 50 : 1;

    public static string? ResolveModeShortcut(string key, bool altKey)
    {
        if (string.IsNullOrEmpty(key) || key.Length != 1)
            return null;

        var lower = char.ToLowerInvariant(key[0]);
        return lower switch
        {
            'l' => "LSB",
            'u' => "USB",
            'c' => (altKey || key[0] == 'C') ? "CW-L" : "CW-U",
            'a' => (altKey || key[0] == 'A') ? "AM-N" : "AM",
            'q' => (altKey || key[0] == 'Q') ? "FM-N" : "FM",
            'd' when key[0] == 'd' => altKey ? "DATA-L" : "DATA-U",
            _ => null
        };
    }
}
