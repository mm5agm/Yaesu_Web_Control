using Xunit;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The '101's PROC knob sets AMC output (AO) while the processor is off.
    /// YWC read only PL until 2026-10-15, so /radio's outer ring never moved
    /// with the radio's knob.
    /// </summary>
    public class AmcCodesTests
    {
        [Theory]
        [InlineData("AO050;", 50)]
        [InlineData("AO001;", 1)]
        [InlineData("AO100;", 100)]
        [InlineData("AO000;", 1)]       // outside the radio's 001-100: clamped
        public void ParsesAmcLevel(string msg, int expected) => Assert.Equal(expected, AmcCodes.ParseAmcLevel(msg));

        [Theory]
        [InlineData("AO;")]
        [InlineData("AO5;")]
        [InlineData("PL050;")]
        [InlineData("?;")]
        public void RejectsOtherReplies(string msg) => Assert.Null(AmcCodes.ParseAmcLevel(msg));

        [Theory]
        [InlineData("FTdx101MP", "030301")]
        [InlineData("FTdx101D", "030301")]
        [InlineData("FTdx10", null)]
        [InlineData("FT-710", null)]
        public void ProcTypeOnlyWhereConfirmed(string model, string? addr) => Assert.Equal(addr, AmcCodes.ProcTypeExAddress(model));
    }
}
