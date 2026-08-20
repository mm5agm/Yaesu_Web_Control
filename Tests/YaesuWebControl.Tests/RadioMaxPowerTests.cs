using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The rated-output figure drives three things that must agree: the RF Power
// slider's range, the Power meter's dial scale, and the range check in
// CatController.SetPower. They used to be three separate literals, and they
// disagreed — the FTDX5000 pair offered 200 W on the slider while the API
// rejected anything over 100 W, and every 100 W radio drew a 0-200 W dial.
// A wrong answer here is silent: the operator sees a plausible number and only
// finds out when the radio quietly refuses to make the power.
public sealed class RadioMaxPowerTests
{
    [Theory]
    [InlineData("FTdx101MP")]
    [InlineData("FTDX5000MP")]
    [InlineData("FTDX5000D")]
    public void MaxPowerWatts_Is200_ForTheHighPowerModels(string model)
        => Assert.Equal(200, RadioCapabilities.MaxPowerWatts(model));

    [Theory]
    [InlineData("FTdx101D")]
    [InlineData("FTdx10")]
    [InlineData("FT-710")]
    [InlineData("FTDX3000")]
    [InlineData("FT-991A")]
    public void MaxPowerWatts_Is100_ForTheHundredWattModels(string model)
        => Assert.Equal(100, RadioCapabilities.MaxPowerWatts(model));

    // Every model in the Settings dropdown must be named explicitly. Falling
    // through to the default is how issue #37 happened: FTdx10, FT-710,
    // FTDX3000 and FT-991A were absent from the equivalent table in site.js,
    // so they inherited the FTdx101MP's 200 W and the slider let an operator
    // ask a 100 W radio for 150 W.
    [Theory]
    [InlineData("FTdx101MP")]
    [InlineData("FTdx101D")]
    [InlineData("FTdx10")]
    [InlineData("FT-710")]
    [InlineData("FTDX3000")]
    [InlineData("FTDX5000MP")]
    [InlineData("FTDX5000D")]
    [InlineData("FT-991A")]
    public void MaxPowerWatts_IsAPlausibleHfRating_ForEverySupportedModel(string model)
    {
        int watts = RadioCapabilities.MaxPowerWatts(model);
        Assert.True(watts is 100 or 200, $"{model} reported {watts} W");
    }

    // An unlisted model gets the generous bound rather than the tight one: too
    // high merely lets a command reach a radio that clamps it itself, too low
    // silently caps output with no operator override. Same reasoning as
    // FrequencyRangeHz.
    [Fact]
    public void MaxPowerWatts_FallsBackTo200_ForAnUnknownModel()
    {
        Assert.Equal(200, RadioCapabilities.MaxPowerWatts("FT-9999"));
        Assert.Equal(200, RadioCapabilities.MaxPowerWatts(""));
    }
}
