using Xunit;
using Yaesu_Web_Control.Pages;

namespace YaesuWebControl.Tests;

// The in-app manual rewrites every heading id to GitHub's slug so the TOC
// inside USER_MANUAL.md (written with GitHub anchors) works in both places.
// #158: nine TOC links were dead in the app because runs of spaces were
// collapsed to one hyphen where GitHub makes one hyphen per space.
public class UserManualSlugTests
{
    [Theory]
    [InlineData("5.20 Radio Scope — the radio's own display (FTdx101MP/D and FTdx10)",
                "520-radio-scope--the-radios-own-display-ftdx101mpd-and-ftdx10")]
    [InlineData("6.7 Backup & Restore", "67-backup--restore")]
    [InlineData("2.4 USB serial driver (Windows / macOS / Linux)", "24-usb-serial-driver-windows--macos--linux")]
    [InlineData("15.6 Can I use VSPE, OmniRig, com0com or a similar virtual COM port sharer?",
                "156-can-i-use-vspe-omnirig-com0com-or-a-similar-virtual-com-port-sharer")]
    [InlineData("  Leading and trailing  ", "leading-and-trailing")]
    public void Matches_GitHub_anchor(string heading, string expected)
        => Assert.Equal(expected, UserManualModel.GitHubSlug(heading));
}
