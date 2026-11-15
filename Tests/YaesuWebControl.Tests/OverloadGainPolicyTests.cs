using Yaesu_Web_Control.Services.Sdr;

namespace YaesuWebControl.Tests;

// The SDRplay receivers run with the AGC off. These pin when the overload
// guard changes the fixed gain: up while the ADC is overloaded, back down
// after a quiet spell (the worker never hears about band changes), and held
// when stepping down brings the overload straight back.
public sealed class OverloadGainPolicyTests
{
    private static readonly DateTime T0 = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Overload_RaisesInStepsUpToTheMaximum()
    {
        var p = new OverloadGainPolicy();
        Assert.Equal(43, p.OnOverload(T0));
        Assert.Equal(46, p.OnOverload(T0));
        for (int i = 0; i < 5; i++) p.OnOverload(T0);       // 40 + 7 x 3 caps at 59
        Assert.Equal(OverloadGainPolicy.MaxGrDb, p.GrDb);
        Assert.Null(p.OnOverload(T0));
    }

    [Fact]
    public void NoOverload_LeavesTheStartGain()
        => Assert.Null(new OverloadGainPolicy().OnTick(T0.AddHours(1), overloaded: false));

    [Fact]
    public void QuietSpell_StepsBackDownToTheStart()
    {
        var p = new OverloadGainPolicy();
        p.OnOverload(T0); p.OnOverload(T0);                       // 46
        Assert.Null(p.OnTick(T0.AddSeconds(29), false));          // not quiet long enough
        Assert.Null(p.OnTick(T0.AddSeconds(60), overloaded: true));
        Assert.Equal(43, p.OnTick(T0.AddSeconds(30), false));
        Assert.Equal(40, p.OnTick(T0.AddSeconds(35), false));
        Assert.Null(p.OnTick(T0.AddSeconds(40), false));          // never below the start
    }

    [Fact]
    public void OverloadRightAfterAStepDown_HoldsThatLevel()
    {
        var p = new OverloadGainPolicy();
        p.OnOverload(T0); p.OnOverload(T0);                       // 46
        var down = T0.AddSeconds(30);
        Assert.Equal(43, p.OnTick(down, false));
        Assert.Equal(46, p.OnOverload(down.AddSeconds(2)));      // the step down caused it
        Assert.Null(p.OnTick(down.AddMinutes(5), false));         // held
        Assert.Equal(43, p.OnTick(down.AddSeconds(2) + OverloadGainPolicy.Hold, false));
    }

    [Fact]
    public void Reset_GoesBackToTheStart()
    {
        var p = new OverloadGainPolicy();
        p.OnOverload(T0);
        p.Reset();
        Assert.Equal(OverloadGainPolicy.StartGrDb, p.GrDb);
    }
}
