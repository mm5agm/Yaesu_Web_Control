using Yaesu_Web_Control;
using Yaesu_Web_Control.Models;

namespace YaesuWebControl.Tests;

// #420 (W1WRH, pre28): the Log4OM launch button was a thin red line and the
// Diagnostics said pre28 was released 2026-11-16.
public class AppButtonNamesAndReleaseDateTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankButtonName_FallsBackToTheDefault(string? blank)
    {
        var s = new ApplicationSettings
        {
            App1Name = blank!, App2Name = blank!, App3Name = blank!, App4Name = blank!, App5Name = blank!,
        };
        Assert.Equal("WSJT-X", s.App1Name);
        Assert.Equal("JTAlert", s.App2Name);
        Assert.Equal("Log4OM", s.App3Name);
        Assert.Equal("GridTracker", s.App4Name);
        Assert.Equal("Fldigi", s.App5Name);
    }

    [Fact]
    public void ButtonName_IsKeptWhenGiven()
    {
        var s = new ApplicationSettings { App3Name = " Log4OM2 " };
        Assert.Equal("Log4OM2", s.App3Name);
    }

    [Fact]
    public void BlankNameInSavedSettings_IsMendedOnLoad()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>("{\"App3Name\":\"\"}")!;
        Assert.Equal("Log4OM", s.App3Name);
    }

    [Fact]
    public void ReleaseDate_IsTheBuildDayForACiBuild()
    {
        Assert.Equal("2026-11-23", AppVersion.ComputeReleaseDate("v2.5.6-pre28", "2026-11-23"));
        Assert.Equal(AppVersion.PlannedReleaseDate, AppVersion.ComputeReleaseDate("", "2026-11-23"));
        Assert.Equal(AppVersion.PlannedReleaseDate, AppVersion.ComputeReleaseDate("v2.5.6-pre28", ""));
    }
}
