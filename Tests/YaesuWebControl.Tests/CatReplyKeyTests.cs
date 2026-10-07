using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// The meter reads share their two letters (RM0/RM5/RM7, SM0/SM1). Keyed on
// two letters, an RM0 reply that arrived late was handed to the waiting RM5
// read, and the power gauge showed the COMP/SWR word (FTdx101MP, 2026-10-07).
public class CatReplyKeyTests
{
    [Theory]
    [InlineData("RM5;", "RM5")]
    [InlineData("RM5089000;", "RM5")]
    [InlineData("RM0082201;", "RM0")]
    [InlineData("SM0;", "SM0")]
    [InlineData("SM1138;", "SM1")]
    public void MeterReads_AreKeyedOnTheirMeterNumber(string text, string key)
        => Assert.Equal(key, CatMultiplexerService.ReplyKey(text));

    [Fact]
    public void LateRm0Reply_DoesNotMatchAPendingRm5Read()
        => Assert.NotEqual(CatMultiplexerService.ReplyKey("RM5;"), CatMultiplexerService.ReplyKey("RM0082201;"));

    [Fact]
    public void Sm0Reply_DoesNotMatchAPendingSm1Read()
        => Assert.NotEqual(CatMultiplexerService.ReplyKey("SM1;"), CatMultiplexerService.ReplyKey("SM0148;"));

    [Theory]
    [InlineData("FA;", "FA")]
    [InlineData("FA014080130;", "FA")]
    [InlineData("MS13;", "MS")]
    [InlineData("PC010;", "PC")]
    [InlineData("TX0;", "TX")]
    [InlineData("?;", "?;")]
    public void OtherCommands_KeepTheirTwoLetterKey(string text, string key)
        => Assert.Equal(key, CatMultiplexerService.ReplyKey(text));
}
