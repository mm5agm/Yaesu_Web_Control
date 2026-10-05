using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The FT-991A as its own model rather than a radio posing as an FT-710.
// Every expectation here comes from the FT-991A CAT manual
// (docs/manuals/FT-991A_CAT_OM_ENG_1711-D.pdf); none of it is bench-verified
// until a 991A owner has run it. What these tests hold is that the gates say
// what the manual says, and that the existing models are left exactly as they
// were.
public sealed class Ft991aCapabilityTests
{
    private static readonly string[] OtherModels =
        { "FTdx101MP", "FTdx101D", "FTdx10", "FT-710", "FTDX3000", "FTDX5000MP", "FTDX5000D" };

    // MD code E is C4FM on the 991A and PSK everywhere else. Reading a C4FM
    // signal as PSK would label a digital-voice FM signal as a data mode.
    [Fact]
    public void ModeCodeE_IsC4fm_OnTheFt991a()
    {
        Assert.Equal("C4FM", CatCommands.ModeFromCode('E', "FT-991A"));
        Assert.Equal("C4FM", CatCommands.ParseMode("MD0E;", "FT-991A"));
    }

    [Fact]
    public void ModeCodeE_StaysPsk_OnEveryOtherModel()
    {
        foreach (var model in OtherModels)
            Assert.Equal("PSK", CatCommands.ModeFromCode('E', model));
        Assert.Equal("PSK", CatCommands.ModeFromCode('E', null));
        Assert.Equal("PSK", CatCommands.ParseMode("MD0E;"));
    }

    [Fact]
    public void C4fm_IsSentAsCodeE()
        => Assert.Equal("MD0E;", CatCommands.FormatMode("C4FM", isSubVfo: false));

    // EX139 / EX140: 144 and 430 MHz top out at 50 W; HF and 6 m are 100 W.
    [Theory]
    [InlineData(14_074_000L, 100)]
    [InlineData(50_313_000L, 100)]
    [InlineData(144_300_000L, 50)]
    [InlineData(432_200_000L, 50)]
    public void MaxPowerWattsAt_DropsTo50_On2mAnd70cm(long hz, int expected)
        => Assert.Equal(expected, RadioCapabilities.MaxPowerWattsAt("FT-991A", hz));

    [Fact]
    public void MaxPowerWattsAt_MatchesMaxPowerWatts_OnEveryOtherModel()
    {
        foreach (var model in OtherModels)
            Assert.Equal(RadioCapabilities.MaxPowerWatts(model),
                         RadioCapabilities.MaxPowerWattsAt(model, 144_300_000L));
    }

    // No RM9 in the 991A's meter table; polling it every 2 s would only
    // collect "?;" answers.
    [Fact]
    public void Ft991a_HasNoTemperatureMeter_AndIsNotPolledForOne()
    {
        Assert.False(RadioCapabilities.HasPaTemperatureMeter("FT-991A"));
        Assert.False(RadioCapabilities.PollsTemperature("FT-991A"));
    }

    [Fact]
    public void TemperaturePoll_IsUnchanged_OnEveryOtherModel()
    {
        foreach (var model in OtherModels)
            Assert.True(RadioCapabilities.PollsTemperature(model), model);
    }

    [Fact]
    public void Ft991a_HasNoRoofingFilterCommand()
    {
        Assert.False(RadioCapabilities.HasRoofingFilterCat("FT-991A"));
        Assert.False(RadioCapabilities.HasRoofingFilterCat("FT-710"));
        Assert.True(RadioCapabilities.HasRoofingFilterCat("FTdx101MP"));
        Assert.True(RadioCapabilities.HasRoofingFilterCat("FTdx10"));
        Assert.True(RadioCapabilities.HasRoofingFilterCat("FTDX3000"));
    }

    // No ST: split is FT alone, as on the FTDX3000.
    [Fact]
    public void Ft991a_DrivesSplitWithFtOnly_LikeTheFtdx3000()
    {
        Assert.True(RadioCapabilities.SplitViaFtOnly("FT-991A"));
        Assert.True(RadioCapabilities.SplitViaFtOnly("FTDX3000"));
        Assert.False(RadioCapabilities.SplitViaFtOnly("FTdx10"));
        Assert.False(RadioCapabilities.SplitViaFtOnly("FT-710"));
        Assert.False(RadioCapabilities.SplitViaFtOnly("FTdx101MP"));
    }

    // No VS and no FR: the receive VFO cannot be chosen over CAT.
    [Fact]
    public void Ft991a_CannotSelectItsReceiveVfo()
    {
        Assert.False(RadioCapabilities.CanSelectRxVfo("FT-991A"));
        foreach (var model in OtherModels)
            Assert.True(RadioCapabilities.CanSelectRxVfo(model), model);
    }

    // 470 MHz is what makes the VFO display nine digits wide on this model
    // only (site.js vfoDigitCount); every other model must stay under 100 MHz
    // or its display would gain a leading zero.
    [Fact]
    public void OnlyTheFt991a_ReachesAbove100MHz()
    {
        Assert.Equal(470_000_000L, RadioCapabilities.FrequencyRangeHz("FT-991A").MaxHz);
        foreach (var model in OtherModels)
            Assert.True(RadioCapabilities.FrequencyRangeHz(model).MaxHz < 100_000_000L, model);
    }
}
