using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

public class KeyboardShortcutRulesTests
{
    [Theory]
    [InlineData(false, false, false, 100)]
    [InlineData(true, false, false, 1000)]
    [InlineData(false, true, false, 10000)]
    [InlineData(true, true, false, 0)]
    [InlineData(false, false, true, 1)]
    [InlineData(true, false, true, 50)]
    [InlineData(false, true, true, 1)]   // Ctrl wins over Alt for fine step
    [InlineData(true, true, true, 50)]   // Ctrl+Shift wins over Shift+Alt DX hop
    public void ResolveTuneStepHz_matches_js_contract(bool shift, bool alt, bool ctrl, int expected)
        => Assert.Equal(expected, KeyboardShortcutRules.ResolveTuneStepHz(shift, alt, ctrl));

    [Theory]
    [InlineData("l", false, "LSB")]
    [InlineData("u", false, "USB")]
    [InlineData("c", false, "CW-U")]
    [InlineData("C", false, "CW-L")]
    [InlineData("c", true, "CW-L")]
    [InlineData("a", false, "AM")]
    [InlineData("A", false, "AM-N")]
    [InlineData("a", true, "AM-N")]
    [InlineData("q", false, "FM")]
    [InlineData("Q", false, "FM-N")]
    [InlineData("d", false, "DATA-U")]
    [InlineData("d", true, "DATA-L")]
    [InlineData("D", false, null)] // uppercase D is DX spots, not DATA
    [InlineData("f", false, null)] // f is fullscreen, not FM
    public void ResolveModeShortcut_matches_js_contract(string key, bool alt, string? expected)
        => Assert.Equal(expected, KeyboardShortcutRules.ResolveModeShortcut(key, alt));
}
