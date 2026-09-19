using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The VDD dial is per model (#155). Every radio used to draw the FTdx101MP's
// 40-55 V face, and the orchestrator's sanity window was in raw counts
// (175-235 — the MP's ~41-55 V), so a 13.8 V radio's every reading was
// discarded and the FTdx101D's needle sat pinned at 40 V. A wrong answer
// here is silent in exactly the same way: a plausible dial that never moves.
public sealed class RadioVddDialTests
{
    [Fact]
    public void FTdx101MP_RunsIts200WFinalFrom50V()
    {
        Assert.Equal(50.0, RadioCapabilities.PaSupplyVolts("FTdx101MP"));
        Assert.Equal((40, 55), RadioCapabilities.VddGaugeRange("FTdx101MP"));
    }

    [Theory]
    [InlineData("FTdx101D")]
    [InlineData("FTDX3000")]
    [InlineData("FTdx10")]
    [InlineData("FT-710")]
    public void ThirteenPointEightVoltRadios_GetTheLowVoltageDial(string model)
    {
        Assert.Equal(13.8, RadioCapabilities.PaSupplyVolts(model));
        Assert.Equal((10, 16), RadioCapabilities.VddGaugeRange(model));
    }

    // The dial must have room either side of the nominal supply: a 13.8 V rig
    // at 14.2 V or a 50 V rig sagging to 46 V on key-down is normal, not
    // off-scale — and off-scale readings are discarded, not clamped.
    [Theory]
    [InlineData("FTdx101MP")]
    [InlineData("FTdx101D")]
    [InlineData("FTDX3000")]
    [InlineData("FTdx10")]
    [InlineData("FT-710")]
    [InlineData("FTDX5000MP")]
    [InlineData("FTDX5000D")]
    public void NominalSupply_SitsInsideTheDial_WithHeadroom(string model)
    {
        double nominal = RadioCapabilities.PaSupplyVolts(model);
        var (min, max) = RadioCapabilities.VddGaugeRange(model);
        Assert.True(nominal * 0.9  >= min - 1e-9, $"{model}: a 10 % sag must stay on the dial");
        Assert.True(nominal * 1.1  <= max + 1e-9, $"{model}: a 10 % rise must stay on the dial");
    }
}
