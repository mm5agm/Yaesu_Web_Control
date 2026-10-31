using Xunit;
using Yaesu_Web_Control.Models;
using Yaesu_Web_Control.Services.Rtty;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The operator's RTTY mode: what the tuner switches to and holds. Bruce
    /// VK2RT, 2026-10-26, runs AFSK in DATA-L, and with RTTY-L fixed the
    /// tuner kept taking him out of it.
    /// </summary>
    public class RttyTunerModeChoiceTests
    {
        [Theory]
        [InlineData("RTTY-L", "RTTY-L")]
        [InlineData("rtty-u", "RTTY-U")]
        [InlineData(" DATA-L ", "DATA-L")]
        [InlineData("DATA-U", "DATA-U")]
        [InlineData("USB", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void OnlyTheFourModesAreChoices(string? given, string? expected)
        {
            Assert.Equal(expected, RttyTunerModeService.Normalise(given));
        }

        [Theory]
        [InlineData("DATA-L", "DATA-L")]
        [InlineData("CW-U", "RTTY-L")]
        [InlineData("", "RTTY-L")]
        public void AnythingElseIsRttyL(string chosen, string expected)
        {
            var s = new ApplicationSettings { RttyTunerMode = chosen };
            Assert.Equal(expected, RttyTunerModeService.TargetMode(s));
        }

        [Fact]
        public void TheDefaultIsRttyL()
        {
            Assert.Equal("RTTY-L", RttyTunerModeService.TargetMode(new ApplicationSettings()));
        }

        [Theory]
        // The mode chosen, exactly: the other sideband is switched too (#250).
        [InlineData("RTTY-L", "RTTY-L", false)]
        [InlineData("RTTY-U", "RTTY-L", true)]
        [InlineData("DATA-L", "RTTY-L", true)]
        [InlineData("USB", "RTTY-L", true)]
        [InlineData("CW-U", "RTTY-L", true)]
        [InlineData("DATA-L", "DATA-L", false)]
        [InlineData("DATA-U", "DATA-L", true)]
        [InlineData("RTTY-L", "DATA-L", true)]
        [InlineData("LSB", "DATA-L", true)]
        [InlineData("DATA-FM", "DATA-U", true)]
        [InlineData(null, "DATA-L", true)]
        public void SwitchesToTheModeChosen(string? mode, string target, bool switches)
        {
            Assert.Equal(switches, RttyTunerModeService.NeedsSwitching(mode, target));
        }
    }
}
