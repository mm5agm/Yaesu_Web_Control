using Xunit;
using Yaesu_Web_Control.Services.Rtty;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// Where the RTTY tuner puts its mark and space filters in the receive
    /// audio. Get the side wrong and the operator sees one arm of the cross
    /// and a blob, and tunes a signal that was right to begin with.
    /// </summary>
    public class RttyTunerTonesTests
    {
        [Fact]
        public void RttyLPutsSpaceAboveMark()
        {
            // Measured on the FTdx101MP 2026-09-23: in RTTY-L a carrier below
            // the dial rises in pitch from 2125 Hz, and space is below mark
            // on the air.
            Assert.Equal((2125.0, 2295.0), RttyTunerService.TonesFor("RTTY-L", 2125, 170, false));
        }

        [Fact]
        public void RttyUPutsSpaceBelowMark()
        {
            Assert.Equal((2125.0, 1955.0), RttyTunerService.TonesFor("RTTY-U", 2125, 170, false));
        }

        [Theory]
        [InlineData("DATA-L")]
        [InlineData("DATA-U")]
        [InlineData("LSB")]
        [InlineData("USB")]
        [InlineData(null)]
        public void AfskModesUseTheSoftwareDefaultTones(string? mode)
        {
            Assert.Equal((2125.0, 2295.0), RttyTunerService.TonesFor(mode, 2125, 170, false));
        }

        [Theory]
        [InlineData("RTTY-L", 1955.0)]
        [InlineData("RTTY-U", 2295.0)]
        [InlineData("DATA-L", 1955.0)]
        public void ReverseMovesSpaceToTheOtherSide(string mode, double space)
        {
            Assert.Equal((2125.0, space), RttyTunerService.TonesFor(mode, 2125, 170, true));
        }

        [Fact]
        public void ShiftAndMarkAreTheOperators()
        {
            Assert.Equal((1275.0, 2125.0), RttyTunerService.TonesFor("RTTY-L", 1275, 850, false));
        }
    }
}
