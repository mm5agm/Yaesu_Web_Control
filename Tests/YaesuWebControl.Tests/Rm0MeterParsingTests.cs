using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// Issue #124, second half. The FTdx101MP reads compression and SWR together
// from one borrowed-meter response, RM0LLLRRR. Both parsers used to answer 0
// when the response was missing or malformed, which is also a perfectly valid
// reading -- 0 raw is SWR 1.0:1, a flat match. So a dropped CAT response, a
// timeout, or a partial line was published to the browser as "your antenna is
// perfect", and on a genuinely bad load the needle dipped to 1.0 for one poll
// cycle before snapping back to full scale.
//
// Nothing downstream could tell the two apart, because by then both were the
// integer 0. The distinction has to survive the parse, so these return null.
public sealed class Rm0MeterParsingTests
{
    [Theory]
    [InlineData("RM0128255;", 128, 255)]
    [InlineData("RM0000000;", 0, 0)]      // a real pair of zeroes still parses as zero
    [InlineData("RM0255000;", 255, 0)]
    [InlineData("RM0128255", 128, 255)]   // no trailing semicolon
    public void ParsesBothMetersFromAWellFormedResponse(string response, int left, int right)
    {
        Assert.Equal(left, CatCommands.ParseRm0LeftMeter(response));
        Assert.Equal(right, CatCommands.ParseRm0RightMeter(response));
    }

    [Theory]
    [InlineData("")]            // SendCommandAsync returned null -> "" at the call site
    [InlineData("RM")]          // truncated
    [InlineData("RM0")]         // prefix only, no digits
    [InlineData("RM5072000;")]  // a different meter's answer arriving on this read
    [InlineData("FA014250000;")]// an unrelated CAT line
    [InlineData("RM0abcdef;")]  // right shape, not numeric
    public void ReturnsNullRatherThanZeroWhenTheReadFailed(string response)
    {
        Assert.Null(CatCommands.ParseRm0LeftMeter(response));
        Assert.Null(CatCommands.ParseRm0RightMeter(response));
    }

    // The right meter needs 9 characters; the left needs only 6. A response
    // carrying just the left value must not report a confident zero for the
    // right one -- that zero is the SWR needle.
    [Fact]
    public void ShortResponseYieldsTheLeftMeterButNullForTheRight()
    {
        Assert.Equal(128, CatCommands.ParseRm0LeftMeter("RM0128;"));
        Assert.Null(CatCommands.ParseRm0RightMeter("RM0128;"));
    }
}
