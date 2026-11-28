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
    public void Add_PassesOnAStationWithNoKnownGrid()
    {
        // The map fills the grid in from a PSK Reporter spot if it has one.
        var list = new WsjtxHeardList();
        list.Add(D("W9XYZ K1ABC -10"), 14_074_000, "FT8", "MM5AGM", T0);
        Assert.Equal("", Assert.Single(list.Since(0).Items).Grid);
        list.Add(D("W9XYZ K1ABC FN42"), 14_074_000, "FT8", "MM5AGM", T0.AddSeconds(15));
        Assert.Equal("FN42", Assert.Single(list.Since(1).Items).Grid);
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

    [Theory]
    [InlineData("CQ K1ABC FN42", "cq", "")]
    [InlineData("CQ DX K1ABC FN42", "cq", "")]
    [InlineData("QRZ K1ABC FN42", "cq", "")]
    [InlineData("MM5AGM K1ABC FN42", "you", "")]
    [InlineData("MM5AGM K1ABC -10", "you", "")]
    [InlineData("W9XYZ K1ABC FN42", "qso", "W9XYZ")]
    [InlineData("W9XYZ K1ABC R-10", "qso", "W9XYZ")]
    [InlineData("<W9XYZ/P> K1ABC -10", "qso", "W9XYZ/P")]
    [InlineData("W9XYZ K1ABC RR73", "fin", "W9XYZ")]
    [InlineData("W9XYZ K1ABC RRR", "fin", "W9XYZ")]
    [InlineData("W9XYZ K1ABC 73", "fin", "W9XYZ")]
    [InlineData("TNX FER QSO 73", "", "")]
    public void StateOf_SaysWhatTheStationIsDoing(string message, string state, string to)
        => Assert.Equal((state, to), WsjtxHeardList.StateOf(message, "MM5AGM"));

    [Fact]
    public void Add_PublishesTheState()
    {
        var list = new WsjtxHeardList();
        list.Add(D("W9XYZ K1ABC -10"), 14_074_000, "FT8", "MM5AGM", T0);
        var h = Assert.Single(list.Since(0).Items);
        Assert.Equal("qso", h.State);
        Assert.Equal("W9XYZ", h.To);
    }

    [Fact]
    public void TryGetLatest_KeepsEachStationsNewestDecode()
    {
        // The Spot Map's Call button echoes this decode back to WSJT-X.
        var list = new WsjtxHeardList();
        list.Add(D("CQ K1ABC FN42", snr: -12), 14_074_000, "FT8", "MM5AGM", T0);
        list.Add(D("W9XYZ K1ABC RR73", snr: -3), 14_074_000, "FT8", "MM5AGM", T0.AddSeconds(30));
        Assert.True(list.TryGetLatest("k1abc", out var d, out var at));
        Assert.Equal("W9XYZ K1ABC RR73", d!.Message);
        Assert.Equal(-3, d.Snr);
        Assert.Equal(T0.AddSeconds(30), at);
        Assert.False(list.TryGetLatest("W9XYZ", out _, out _));
        list.Clear();
        Assert.False(list.TryGetLatest("K1ABC", out _, out _));
    }
}
