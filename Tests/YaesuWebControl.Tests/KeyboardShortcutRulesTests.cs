using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

public class KeyboardShortcutRulesTests
{
    [Theory]
    [InlineData(false, false, 100)]
    [InlineData(true, false, 1000)]
    [InlineData(false, true, 10000)]
    [InlineData(true, true, 0)]
    public void ResolveTuneStepHz_matches_js_contract(bool shift, bool alt, int expected)
        => Assert.Equal(expected, KeyboardShortcutRules.ResolveTuneStepHz(shift, alt));

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 50)]
    public void ResolveBracketTuneStepHz_matches_js_contract(bool shift, int expected)
        => Assert.Equal(expected, KeyboardShortcutRules.ResolveBracketTuneStepHz(shift));

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
