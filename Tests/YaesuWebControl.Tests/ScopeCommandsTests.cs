using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

public sealed class ScopeCommandsTests
{
    [Theory]
    [InlineData(true,  0, 0, '0')]
    [InlineData(true,  1, 0, '1')]
    [InlineData(true,  2, 9, '2')]   // size ignored on 3DSS
    [InlineData(false, 0, 0, '3')]   // W/F CENTER L
    [InlineData(false, 0, 1, '4')]
    [InlineData(false, 0, 2, '5')]
    [InlineData(false, 1, 0, '6')]
    [InlineData(false, 2, 1, 'A')]   // W/F FIX N
    [InlineData(false, 2, 2, 'B')]
    public void ModeValue_ComposesTheDocumentedGrid(bool is3dss, int placement, int size, char expected)
    {
        Assert.Equal(expected, ScopeCommands.ModeValue(is3dss, placement, size));
    }

    [Fact]
    public void SetAfFft_PacksThreeAxesWithoutZeroingTheRest()
    {
        Assert.Equal("SS0711200;", ScopeCommands.SetAfFft('0', '1', '1', '2'));
        Assert.Equal("SS1700500;", ScopeCommands.SetAfFft('1', '0', '0', '5'));
    }

    [Fact]
    public void ParseAfFft_ReadsThePackedField()
    {
        var (fft, osc, time) = ScopeCommands.ParseAfFft("11200");
        Assert.Equal('1', fft);
        Assert.Equal('1', osc);
        Assert.Equal('2', time);
    }

    [Fact]
    public void SetColor_PacksThreeAxesWithoutZeroingTheRest()
    {
        Assert.Equal("SS0341100;", ScopeCommands.SetColor('0', '4', '1', '1'));
        Assert.Equal("SS13A0000;", ScopeCommands.SetColor('1', 'A', '0', '0'));
    }

    [Fact]
    public void ParseColor_ReadsThePackedField()
    {
        var (color, nb, on) = ScopeCommands.ParseColor("41100");
        Assert.Equal('4', color);
        Assert.Equal('1', nb);
        Assert.Equal('1', on);
    }

    [Fact]
    public void ParseAfFft_NullOrShortDefaultsToZeros()
    {
        var (fft, osc, time) = ScopeCommands.ParseAfFft(null);
        Assert.Equal('0', fft);
        Assert.Equal('0', osc);
        Assert.Equal('0', time);
    }

    [Fact]
    public void ScopeSpeedLabels_Ft710AddsStop()
    {
        Assert.Equal(5, RadioCapabilities.ScopeSpeedLabels("FTdx10").Length);
        Assert.Equal("STOP", RadioCapabilities.ScopeSpeedLabels("FT-710")[^1]);
    }

    [Fact]
    public void SupportsSpectrumScopeCat_IncludesFtdx10NotFt710()
    {
        Assert.True(RadioCapabilities.SupportsSpectrumScopeCat("FTdx10"));
        Assert.True(RadioCapabilities.SupportsSpectrumScopeCat("FTdx101MP"));
        Assert.False(RadioCapabilities.SupportsSpectrumScopeCat("FT-710"));
        Assert.False(RadioCapabilities.SupportsScopeMulti("FTdx10"));
    }

    // The FTDX10 CAT manual fixes SS P2=3 P4/P5 at zero, so the NB Col row must
    // not render there. Gated in _RadioScopeButtonsPartial on this method.
    [Theory]
    [InlineData("FTdx101MP", true)]
    [InlineData("FTdx101D",  true)]
    [InlineData("FTdx10",    false)]
    [InlineData("FT-710",    false)]
    [InlineData("",          false)]
    public void SupportsScopeNarrowBandColor_IsFtdx101Only(string model, bool expected)
    {
        Assert.Equal(expected, RadioCapabilities.SupportsScopeNarrowBandColor(model));
    }
}
