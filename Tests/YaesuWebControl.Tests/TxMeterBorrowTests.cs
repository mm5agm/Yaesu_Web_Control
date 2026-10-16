using Xunit;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

public class TxMeterBorrowTests
{
    [Theory]
    [InlineData("LSB")]
    [InlineData("USB")]
    [InlineData("AM")]
    [InlineData("AM-N")]
    [InlineData("FM")]
    [InlineData("FM-N")]
    public void Voice_modes_borrow_comp_and_swr(string mode)
    {
        Assert.Equal("13", TxMeterBorrow.SelectionFor(mode));
        Assert.True(TxMeterBorrow.LeftIsCompression(TxMeterBorrow.SelectionFor(mode)));
    }

    [Theory]
    [InlineData("CW-U")]
    [InlineData("CW-L")]
    [InlineData("RTTY-L")]
    [InlineData("RTTY-U")]
    [InlineData("PSK")]
    [InlineData("DATA-L")]
    [InlineData("DATA-U")]
    [InlineData("DATA-FM")]
    [InlineData("DATA-FM-N")]
    public void Non_voice_modes_borrow_power_and_swr(string mode)
    {
        Assert.Equal("03", TxMeterBorrow.SelectionFor(mode));
        Assert.False(TxMeterBorrow.LeftIsCompression(TxMeterBorrow.SelectionFor(mode)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SOMETHING")]
    public void Unknown_mode_keeps_the_old_comp_and_swr(string? mode)
    {
        Assert.Equal("13", TxMeterBorrow.SelectionFor(mode));
    }
}
