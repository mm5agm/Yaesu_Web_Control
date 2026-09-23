using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The Radio Display meter pop-up turns a click into an MS write to the radio's
// front panel. A wrong digit here is silent in the browser - the request
// succeeds and the radio shows a different meter from the one clicked - so
// the mapping is pinned to the values confirmed on the FTdx101MP's panel and
// to the CAT manual tables for the single-meter radios.
public sealed class FrontPanelMetersTests
{
    [Theory]
    [InlineData("0", "0", "00")]   // PO + ALC, the radio's default - confirmed 2026-08-15
    [InlineData("1", "3", "13")]   // COMP + SWR, YWC's TX borrow - confirmed 2026-08-14
    [InlineData("2", "2", "22")]   // TEMP + ID - confirmed 2026-08-15
    public void Ftdx101_BuildsLeftThenRight(string left, string right, string expected)
    {
        Assert.Equal(expected, FrontPanelMeters.BuildDigits("FTdx101MP", [left, right]));
        Assert.Equal(expected, FrontPanelMeters.BuildDigits("FTdx101D", [left, right]));
    }

    [Theory]
    [InlineData("3", "0")]   // the left meter has only PO / COMP / TEMP
    [InlineData("0", "4")]   // the right meter has only ALC / VDD / ID / SWR
    public void Ftdx101_RejectsCodesOutsideEachMeter(string left, string right)
        => Assert.Null(FrontPanelMeters.BuildDigits("FTdx101MP", [left, right]));

    [Fact]
    public void Ftdx101_NeedsBothMeters()
        => Assert.Null(FrontPanelMeters.BuildDigits("FTdx101MP", ["1"]));

    [Theory]
    [InlineData("FTdx10")]
    [InlineData("FT-710")]
    public void SingleMeterRadios_SendTheFixedZeroP2(string model)
    {
        Assert.Equal("50", FrontPanelMeters.BuildDigits(model, ["5"]));   // SWR
        Assert.Null(FrontPanelMeters.BuildDigits(model, ["6"]));
        Assert.Null(FrontPanelMeters.BuildDigits(model, ["0", "0"]));
    }

    [Theory]
    [InlineData("FTDX3000")]
    [InlineData("FTDX5000MP")]
    [InlineData("FT-991A")]
    [InlineData("")]
    public void RadiosWithNoTftTable_OfferNothing(string model)
    {
        Assert.Null(FrontPanelMeters.For(model));
        Assert.Null(FrontPanelMeters.BuildDigits(model, ["0"]));
        Assert.Empty(FrontPanelMeters.ParseDigits(model, "00"));
    }

    [Theory]
    [InlineData("13", true,  true)]    // YWC's own borrow: both readings
    [InlineData("10", true,  false)]   // COMP + ALC
    [InlineData("03", false, true)]    // PO + SWR
    [InlineData("21", false, false)]   // TEMP + VDD
    [InlineData(null, false, false)]
    public void Rm0_CarriesCompOnlyOnTheLeftAndSwrOnlyOnTheRight(string? digits, bool comp, bool swr)
        => Assert.Equal((comp, swr), FrontPanelMeters.Rm0Carries(digits));

    [Fact]
    public void TxGaugesLost_NamesWhatThePanelNoLongerShows()
    {
        Assert.Equal(["COMP", "SWR"], FrontPanelMeters.TxGaugesLost("FTdx101MP", "21"));
        Assert.Equal(["SWR"], FrontPanelMeters.TxGaugesLost("FTdx101D", "10"));
        Assert.Empty(FrontPanelMeters.TxGaugesLost("FTdx101MP", "13"));
        // The single-meter radios read COMP (RM3) and SWR (RM6) directly.
        Assert.Empty(FrontPanelMeters.TxGaugesLost("FTdx10", "30"));
    }

    [Fact]
    public void Parse_ReadsBackWhatBuildWrote()
        => Assert.Equal(["1", "3"], FrontPanelMeters.ParseDigits("FTdx101MP", "13"));

    [Fact]
    public void Parse_UnknownOrMissingDigitsAreNull()
    {
        Assert.Equal([null, null], FrontPanelMeters.ParseDigits("FTdx101MP", null));
        Assert.Equal(["2", null], FrontPanelMeters.ParseDigits("FTdx101MP", "29"));
        Assert.Equal(["4"], FrontPanelMeters.ParseDigits("FTdx10", "40"));
    }
}
