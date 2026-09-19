using Xunit;
using Yaesu_Web_Control;

namespace YaesuWebControl.Tests;

/// <summary>
/// AppVersion.Display is what the About page, tray and diagnostics block show.
/// CI bakes the git tag in; these pin the three shapes it can take so a
/// pre-release names itself and a full release still reads as the bare version.
/// </summary>
public class AppVersionDisplayTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LocalBuild_NoTag_ShowsCurrent(string tag)
        => Assert.Equal("2.5.2", AppVersion.ComputeDisplay("2.5.2", tag));

    [Theory]
    [InlineData("v2.5.2")]
    [InlineData("V2.5.2")]
    [InlineData("2.5.2")]
    public void FullRelease_TagMatchesCurrent_ShowsBareVersion(string tag)
        => Assert.Equal("2.5.2", AppVersion.ComputeDisplay("2.5.2", tag));

    [Theory]
    [InlineData("v2.5.2-pre2", "2.5.2-pre2")]
    [InlineData("v2.5.2-rc1",  "2.5.2-rc1")]
    [InlineData(" v2.5.2-pre2 ", "2.5.2-pre2")]
    public void PreRelease_ShowsVersionWithSuffix(string tag, string expected)
        => Assert.Equal(expected, AppVersion.ComputeDisplay("2.5.2", tag));

    [Theory]
    [InlineData("v2.6.0",       "2.5.2 (v2.6.0)")]
    [InlineData("v2.5.20-pre1", "2.5.2 (v2.5.20-pre1)")]
    [InlineData("unstable-20260917", "2.5.2 (unstable-20260917)")]
    public void MismatchedTag_IsShownBesideCurrent_NotHidden(string tag, string expected)
        => Assert.Equal(expected, AppVersion.ComputeDisplay("2.5.2", tag));

    [Fact]
    public void Display_IsDerivedFromCurrentAndBuildTag()
        => Assert.Equal(AppVersion.ComputeDisplay(AppVersion.Current, AppVersion.BuildTag), AppVersion.Display);
}
