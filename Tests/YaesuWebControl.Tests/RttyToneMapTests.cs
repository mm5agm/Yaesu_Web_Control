using Xunit;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The radio's RTTY MARK and SHIFT menu answers. A wrong reading is
    /// silent: the page shows a plausible number that is not the radio's.
    /// </summary>
    public class RttyToneMapTests
    {
        [Theory]
        [InlineData("0", 1275)]
        [InlineData("00", 1275)]
        [InlineData("1", 2125)]   // FTdx101MP showing 2125 answers 1 (measured 2026-09-23)
        [InlineData("01", 2125)]
        public void MarkCodesAreZeroBased(string code, int hz)
            => Assert.Equal(hz, RttyToneMap.MarkHzFromCode(code));

        [Theory]
        [InlineData("2")]
        [InlineData("x")]
        [InlineData(null)]
        public void UnknownMarkCodesAreNotGuessed(string? code)
            => Assert.Null(RttyToneMap.MarkHzFromCode(code));

        [Theory]
        [InlineData("0", 170)]    // FTdx101MP showing 170 answers 0 (measured 2026-09-23)
        [InlineData("1", 200)]
        [InlineData("2", 425)]
        [InlineData("3", 850)]
        public void ShiftCodesAreZeroBased(string code, int hz)
            => Assert.Equal(hz, RttyToneMap.ShiftHzFromCode(code));
    }
}
