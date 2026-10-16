using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The antenna dropdown, and the AN0;/AN1; poll behind it, only belong on radios
// whose CAT manual has an AN command. The FT-710 was missing from the list, so
// it showed three antennas for its one ANT jack and was asked for an antenna it
// cannot report every two seconds.
public sealed class RadioAntennaSelectorTests
{
    [Theory]
    [InlineData("FTdx101MP")]
    [InlineData("FTdx101D")]
    [InlineData("FTDX3000")]
    [InlineData("FTDX5000MP")]
    public void Radios_with_an_AN_command_get_the_selector(string model) =>
        Assert.True(RadioCapabilities.HasAntennaSelector(model));

    [Theory]
    [InlineData("FT-710")]
    [InlineData("FTdx10")]
    [InlineData("FT-991A")]
    public void Single_antenna_radios_with_no_AN_command_do_not(string model) =>
        Assert.False(RadioCapabilities.HasAntennaSelector(model));
}
