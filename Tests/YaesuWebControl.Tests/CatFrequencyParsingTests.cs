using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// FA/FB replies are 8 digits on the FTDX3000 and FTDX5000 and 9 digits on
// every other supported model. The old parser assumed 9 unconditionally:
// CatCommands.ParseFrequency returned 0 for an 8-digit reply, and
// MultiplexedCatClient.QueryFrequencyAAsync threw ArgumentOutOfRangeException
// on Substring(2, 9). The latter broke every band change on an FTDX5000
// because SetBandA reads the frequency back after writing it (issue #235).
public sealed class CatFrequencyParsingTests
{
    [Theory]
    [InlineData("FA014074000", 14_074_000)]
    [InlineData("FA014074000;", 14_074_000)]
    [InlineData("FB000249150", 249_150)]
    [InlineData("FB000249150;", 249_150)]
    public void ParseFrequency_ReadsNineDigitReplies(string response, long expected)
        => Assert.Equal(expected, CatCommands.ParseFrequency(response));

    [Theory]
    [InlineData("FA07074000", 7_074_000)]
    [InlineData("FA07074000;", 7_074_000)]
    [InlineData("FB00030000", 30_000)]
    [InlineData("FB00030000;", 30_000)]
    public void ParseFrequency_ReadsEightDigitReplies(string response, long expected)
        => Assert.Equal(expected, CatCommands.ParseFrequency(response));

    // The multiplexer strips the semicolon before handing a reply to its
    // awaiter, so the no-semicolon forms above are the ones the band-change
    // read-back actually sees.
    [Fact]
    public void ParseFrequency_DoesNotThrowOnAnEightDigitReply()
    {
        var exception = Record.Exception(() => CatCommands.ParseFrequency("FA07074000"));
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("")]
    [InlineData("FA")]
    [InlineData("FA;")]
    [InlineData("FA1234567")]       // 7 digits
    [InlineData("FA1234567890")]    // 10 digits
    [InlineData("MD01;")]
    [InlineData("GARBAGE")]
    public void ParseFrequency_ReturnsZero_ForMalformedOrUnrelatedReplies(string response)
        => Assert.Equal(0, CatCommands.ParseFrequency(response));
}
