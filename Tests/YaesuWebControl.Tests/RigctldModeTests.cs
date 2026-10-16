using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// get_mode used to compare against names ParseMode never returns, so every
// mode but USB/LSB/FM/AM came back to WSJT-X and Log4OM as USB.
public sealed class RigctldModeTests
{
    [Theory]
    [InlineData("MD02;", "USB")]
    [InlineData("MD01;", "LSB")]
    [InlineData("MD03;", "CW")]
    [InlineData("MD07;", "CW-R")]
    [InlineData("MD06;", "RTTY")]
    [InlineData("MD09;", "RTTY-R")]
    [InlineData("MD0C;", "PKTUSB")]
    [InlineData("MD08;", "PKTLSB")]
    [InlineData("MD0A;", "PKTFM")]
    [InlineData("MD04;", "FM")]
    [InlineData("MD0B;", "FM")]
    [InlineData("MD05;", "AM")]
    public void EveryRadioMode_ReachesItsHamlibName(string answer, string hamlib)
        => Assert.Equal(hamlib, RigctldServer.ToHamlibMode(CatCommands.ParseMode(answer)));

    [Fact]
    public void C4fm_IsReportedAsFm_OnTheFt991a()
        => Assert.Equal("FM", RigctldServer.ToHamlibMode(CatCommands.ParseMode("MD0E;", "FT-991A")));

    // set_mode then get_mode must hand back what was set, or a logger that
    // checks the rig after setting it sees a mismatch.
    [Theory]
    [InlineData("USB")] [InlineData("LSB")] [InlineData("CW")] [InlineData("CW-R")]
    [InlineData("RTTY")] [InlineData("RTTY-R")] [InlineData("AM")] [InlineData("FM")]
    [InlineData("PKTUSB")] [InlineData("PKTLSB")] [InlineData("PKTFM")]
    public void SetThenGet_RoundTrips(string hamlib)
    {
        var map = new Dictionary<string, string>
        {
            ["PKTUSB"] = "DATA-U", ["PKTLSB"] = "DATA-L", ["PKTFM"] = "DATA-FM",
            ["CW"] = "CW-U", ["CW-R"] = "CW-L", ["RTTY"] = "RTTY-L", ["RTTY-R"] = "RTTY-U",
        };
        string sent = CatCommands.FormatMode(map.GetValueOrDefault(hamlib, hamlib), isSubVfo: false);
        Assert.Equal(hamlib, RigctldServer.ToHamlibMode(CatCommands.ParseMode(sent)));
    }
}
