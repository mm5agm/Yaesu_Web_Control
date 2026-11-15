using Yaesu_Web_Control.Services.Sdr;

namespace YaesuWebControl.Tests;

// Each span row must deliver the sample rate its span assumes. A wrong rate
// doesn't look broken: the trace is a good spectrum, just wider or narrower
// than the scale says. The 500 kHz row had exactly that bug until 2026-11-14.
// The API's low-IF stage decimates by 2, not 4, with the 600 kHz filter, so
// the row delivered 1 MS/s and the "500 kHz" span showed 1 MHz. These pin the
// rates measured against the radio's own ACC scope.
public sealed class SdrplayTunePlanTests
{
    [Theory]
    [InlineData(15_625, 15_625)]
    [InlineData(31_250, 31_250)]
    [InlineData(62_500, 62_500)]
    [InlineData(125_000, 125_000)]
    [InlineData(250_000, 250_000)]
    [InlineData(500_000, 500_000)]
    [InlineData(1_000_000, 1_000_000)]
    [InlineData(2_000_000, 2_000_000)]
    public void EachRow_DeliversTheRateItsSpanAssumes(double requested, double expected)
        => Assert.Equal(expected, SdrplayDevice.PlanFor(requested).AchievedRateHz);

    [Fact]
    public void SixHundredKilohertzFilter_HalvesRatherThanQuarters()
    {
        var plan = SdrplayDevice.PlanFor(500_000);
        Assert.Equal(2, plan.LifDecimation);
        Assert.Equal(4, SdrplayDevice.PlanFor(250_000).LifDecimation);
    }
}
