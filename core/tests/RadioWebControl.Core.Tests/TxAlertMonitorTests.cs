using RadioWebControl.Core.Services.TxAlert;

namespace RadioWebControl.Core.Tests;

// The high SWR / high ALC alert (YWC Discussion #257), on a fake clock.
public class TxAlertMonitorTests
{
    private static readonly TxAlertLimits On = new(true, 2.5, true, 50);
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 14, 5, 12, TimeSpan.Zero);

    private sealed class Rig
    {
        public long Now = 1_000_000;
        public readonly TxAlertMonitor M;
        public Rig() => M = new TxAlertMonitor(() => Now, () => T0.AddMilliseconds(Now - 1_000_000));

        // Feed the same reading every 100 ms for ms milliseconds.
        public TxAlertSnapshot Hold(int ms, TxAlertReading r, TxAlertLimits? l = null)
        {
            TxAlertSnapshot s = M.Update(r, l ?? On);
            for (int t = 100; t <= ms; t += 100) { Now += 100; s = M.Update(r, l ?? On); }
            return s;
        }
    }

    private static TxAlertReading Tx(double swr, double alc = 0, string mode = "USB") => new(true, swr, alc, mode);
    private static readonly TxAlertReading Rx = new(false, 0, 0, "USB");

    [Fact]
    public void AHighSwrHeldForTheHoldTimeRaisesAnAlert()
    {
        var r = new Rig();
        var s = r.Hold(TxAlertMonitor.HoldMs, Tx(3.2));
        Assert.NotNull(s.Swr);
        Assert.True(s.Swr!.Active);
        Assert.Equal(3.2, s.Swr.Peak);
        Assert.Equal(2.5, s.Swr.Limit);
        Assert.Equal("USB", s.Swr.Mode);
    }

    [Fact]
    public void AOnePollSpikeAsTheRadioKeysUpIsIgnored()
    {
        var r = new Rig();
        r.Hold(TxAlertMonitor.HoldMs - 100, Tx(4.0));
        var s = r.Hold(1000, Tx(1.3));
        Assert.Null(s.Swr);
    }

    [Fact]
    public void ASwrAtTheLimitIsNotOverIt()
    {
        var r = new Rig();
        Assert.Null(r.Hold(2000, Tx(2.5)).Swr);
    }

    [Fact]
    public void ThePeakFollowsTheWorstReading()
    {
        var r = new Rig();
        r.Hold(500, Tx(3.0));
        r.Hold(300, Tx(4.4));
        var s = r.Hold(300, Tx(3.1));
        Assert.Equal(4.4, s.Swr!.Peak);
    }

    [Fact]
    public void TheAlertStaysAfterTheOverWithItsPeak()
    {
        var r = new Rig();
        long id = r.Hold(600, Tx(3.6)).Swr!.Id;
        var s = r.Hold(1000, Rx);
        Assert.False(s.Swr!.Active);
        Assert.Equal(3.6, s.Swr.Peak);
        Assert.Equal(id, s.Swr.Id);
        Assert.Equal(T0.AddMilliseconds(TxAlertMonitor.HoldMs), s.Swr.At);
    }

    [Fact]
    public void AnEndedAlertGoesAfterFiveMinutes()
    {
        var r = new Rig();
        r.Hold(600, Tx(3.6));
        r.Hold(100, Rx);
        r.Now += TxAlertMonitor.KeepEndedMs;
        Assert.Null(r.M.Update(Rx, On).Swr);
    }

    [Fact]
    public void DippingBelowAndBackInTheSameOverIsTheSameAlert()
    {
        var r = new Rig();
        long id = r.Hold(600, Tx(3.0)).Swr!.Id;
        Assert.False(r.Hold(300, Tx(2.0)).Swr!.Active);
        var s = r.Hold(600, Tx(2.9));
        Assert.True(s.Swr!.Active);
        Assert.Equal(id, s.Swr.Id);
    }

    [Fact]
    public void TheNextOverGetsANewAlert()
    {
        var r = new Rig();
        long first = r.Hold(600, Tx(3.0)).Swr!.Id;
        r.Hold(500, Rx);
        var s = r.Hold(600, Tx(3.0));
        Assert.True(s.Swr!.Active);
        Assert.NotEqual(first, s.Swr.Id);
    }

    [Fact]
    public void HighAlcInADataModeRaisesAnAlert()
    {
        var r = new Rig();
        var s = r.Hold(600, Tx(1.2, alc: 72, mode: "DATA-U"));
        Assert.Null(s.Swr);
        Assert.True(s.Alc!.Active);
        Assert.Equal(72, s.Alc.Peak);
        Assert.Equal("DATA-U", s.Alc.Mode);
    }

    [Theory]
    [InlineData("USB")]      // some ALC is normal on SSB
    [InlineData("RTTY-L")]   // FSK: keyed, no audio
    [InlineData("CW-U")]
    [InlineData("")]
    public void HighAlcOutsideTheAudioDataModesIsIgnored(string mode)
    {
        var r = new Rig();
        Assert.Null(r.Hold(2000, Tx(1.2, alc: 95, mode: mode)).Alc);
    }

    [Theory]
    [InlineData("DATA-U", true)]
    [InlineData("data-l", true)]
    [InlineData("DATA-FM-N", true)]
    [InlineData("PSK", true)]
    [InlineData("PKTUSB", true)]
    [InlineData("USB-D", true)]
    [InlineData("RTTY-U", false)]
    [InlineData("LSB", false)]
    [InlineData(null, false)]
    public void AudioDataModes(string? mode, bool expected) => Assert.Equal(expected, TxAlertMonitor.IsAudioDataMode(mode));

    [Fact]
    public void TurningTheAlcAlertOffLeavesSwr()
    {
        var r = new Rig();
        var s = r.Hold(600, Tx(3.0, alc: 90, mode: "DATA-U"), On with { AlcEnabled = false });
        Assert.NotNull(s.Swr);
        Assert.Null(s.Alc);
    }

    [Fact]
    public void TurningTheAlertOffClearsEverything()
    {
        var r = new Rig();
        r.Hold(600, Tx(3.0, alc: 90, mode: "DATA-U"));
        var s = r.M.Update(Tx(3.0, alc: 90, mode: "DATA-U"), On with { Enabled = false });
        Assert.False(s.Enabled);
        Assert.Null(s.Swr);
        Assert.Null(s.Alc);
    }

    [Fact]
    public void NothingCountsWhileReceiving()
    {
        var r = new Rig();
        Assert.Null(r.Hold(2000, new TxAlertReading(false, 9.9, 100, "DATA-U")).Swr);
    }

    [Fact]
    public void AMissingReadingIsNotHigh()
    {
        var r = new Rig();
        Assert.Null(r.Hold(2000, new TxAlertReading(true, null, null, "DATA-U")).Swr);
    }

    [Theory]
    [InlineData(1.0, 1.5)]
    [InlineData(2.04, 2.0)]
    [InlineData(9.0, 5.0)]
    [InlineData(double.NaN, 2.5)]
    public void SwrLimitsAreClamped(double given, double used) => Assert.Equal(used, TxAlertLimits.ClampSwr(given));

    [Theory]
    [InlineData(0, 10)]
    [InlineData(49.6, 50)]
    [InlineData(150, 100)]
    public void AlcLimitsAreClamped(double given, double used) => Assert.Equal(used, TxAlertLimits.ClampAlcPercent(given));
}
