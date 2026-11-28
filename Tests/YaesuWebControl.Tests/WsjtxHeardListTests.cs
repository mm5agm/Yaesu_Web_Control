using Yaesu_Web_Control.Services.Wsjtx;

namespace YaesuWebControl.Tests;

// The Spot Map's instant "I heard": stations WSJT-X/WS decodes go on the map
// straight away instead of waiting for PSK Reporter's batches.
public sealed class WsjtxHeardListTests
{
    private static readonly DateTime T0 = new(2026, 11, 28, 16, 0, 0, DateTimeKind.Utc);

    private static WsjtxDecode D(string message, int snr = -10, uint df = 1500, bool isNew = true, bool offAir = false) =>
        new("WSJT-X", isNew, 0, snr, 0.2, df, "~", message, false, offAir);

    [Theory]
    [InlineData("CQ K1ABC FN42", "K1ABC", "FN42")]
    [InlineData("CQ DX K1ABC FN42", "K1ABC", "FN42")]
    [InlineData("CQ POTA K1ABC", "K1ABC", "")]
    [InlineData("W9XYZ K1ABC FN42", "K1ABC", "FN42")]
    [InlineData("W9XYZ K1ABC R FN42", "K1ABC", "FN42")]
    [InlineData("W9XYZ K1ABC -10", "K1ABC", "")]
    [InlineData("W9XYZ K1ABC R-10", "K1ABC", "")]
    [InlineData("W9XYZ K1ABC RR73", "K1ABC", "")]
    [InlineData("W9XYZ K1ABC 73", "K1ABC", "")]
    [InlineData("W9XYZ <K1ABC/P> FN42", "K1ABC/P", "FN42")]
    public void Sender_IsTheSecondCall(string message, string call, string grid)
    {
        var s = WsjtxHeardList.Sender(message, "MM5AGM");
        Assert.NotNull(s);
        Assert.Equal(call, s.Value.Call);
        Assert.Equal(grid, s.Value.Grid);
    }

    [Theory]
    [InlineData("TNX FER QSO 73")]
    [InlineData("K1ABC MM5AGM IO75")]
    [InlineData("CQ MM5AGM IO75")]
    [InlineData("HELLO")]
    public void Sender_SkipsFreeTextAndOwnMessages(string message)
        => Assert.Null(WsjtxHeardList.Sender(message, "MM5AGM"));

    [Fact]
    public void Add_PublishesWithDialPlusOffset()
    {
        var list = new WsjtxHeardList();
        list.Add(D("CQ K1ABC FN42", snr: -7, df: 1234), 14_074_000, "FT8", "MM5AGM", T0);
        var (seq, items) = list.Since(0);
        var h = Assert.Single(items);
        Assert.Equal(1, seq);
        Assert.Equal("K1ABC", h.Call);
        Assert.Equal("FN42", h.Grid);
        Assert.Equal(14_075_234, h.FrequencyHz);
        Assert.Equal("FT8", h.Mode);
        Assert.Equal(-7, h.Snr);
        Assert.Equal((long)(T0 - DateTime.UnixEpoch).TotalSeconds, h.UnixSeconds);
    }

    [Fact]
    public void Add_HoldsAStationBackUntilItsGridIsKnown()
    {
        var list = new WsjtxHeardList();
        list.Add(D("W9XYZ K1ABC -10"), 14_074_000, "FT8", "MM5AGM", T0);
        Assert.Empty(list.Since(0).Items);
        list.Add(D("W9XYZ K1ABC FN42"), 14_074_000, "FT8", "MM5AGM", T0.AddSeconds(15));
        var h = Assert.Single(list.Since(0).Items);
        Assert.Equal("FN42", h.Grid);
    }

    [Fact]
    public void Add_RemembersAGridFromAnEarlierMessage()
    {
        var list = new WsjtxHeardList();
        list.Add(D("CQ K1ABC FN42"), 14_074_000, "FT8", "MM5AGM", T0);
        list.Add(D("W9XYZ K1ABC RR73"), 14_074_000, "FT8", "MM5AGM", T0.AddSeconds(30));
        var (seq, items) = list.Since(1);
        Assert.Equal(2, seq);
        Assert.Equal("FN42", Assert.Single(items).Grid);
    }

    [Fact]
    public void Add_SkipsReplaysAndDecodesFromAFile()
    {
        var list = new WsjtxHeardList();
        list.Add(D("CQ K1ABC FN42", isNew: false), 14_074_000, "FT8", "MM5AGM", T0);
        list.Add(D("CQ K2ABC FN42", offAir: true), 14_074_000, "FT8", "MM5AGM", T0);
        Assert.Empty(list.Since(0).Items);
    }

    [Fact]
    public void Since_DropsStationsOlderThanAnHour()
    {
        var list = new WsjtxHeardList();
        list.Add(D("CQ K1ABC FN42"), 14_074_000, "FT8", "MM5AGM", T0);
        list.Add(D("CQ K2ABC FN42"), 14_074_000, "FT8", "MM5AGM", T0.AddMinutes(61));
        Assert.Equal("K2ABC", Assert.Single(list.Since(0).Items).Call);
    }
}
