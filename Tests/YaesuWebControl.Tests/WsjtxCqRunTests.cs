using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Wsjtx;

namespace YaesuWebControl.Tests;

// Call CQ stops by itself after N CQs in a row nobody answered (Mike 2E0UMF,
// 2026-11-28), and the ALC/SWR peaks per transmission for the tiles page.
public class WsjtxCqRunTests
{
    private const string Cq = "CQ MM5AGM IO85";

    // One CQ: transmit on, then off. Returns what the off Status returned.
    private static bool SendOne(WsjtxCqRun run, string message)
    {
        Assert.False(run.Update(true, message));
        return run.Update(false, message);
    }

    [Fact]
    public void StopsWhenTheLastAllowedCqHasFinished()
    {
        var run = new WsjtxCqRun();
        run.Start(3, false);
        Assert.False(SendOne(run, Cq));
        Assert.False(SendOne(run, Cq));
        Assert.False(run.Update(true, Cq));   // the 3rd CQ is not cut short
        Assert.Equal(3, run.Sent);
        Assert.True(run.Update(false, Cq));   // stop once it has finished
        Assert.False(run.Active);
        Assert.False(run.Update(true, Cq));   // and only once
        Assert.False(run.Update(false, Cq));
    }

    [Fact]
    public void RepeatedStatusesWhileTransmittingCountOnce()
    {
        var run = new WsjtxCqRun();
        run.Start(6, false);
        for (int i = 0; i < 10; i++) run.Update(true, Cq);
        Assert.Equal(1, run.Sent);
    }

    [Fact]
    public void AnAnswerStartsTheCountAgainAndAQsoIsNeverCut()
    {
        var run = new WsjtxCqRun();
        run.Start(2, false);
        Assert.False(SendOne(run, Cq));
        Assert.False(SendOne(run, "G4ABC MM5AGM -10"));
        Assert.False(SendOne(run, "G4ABC MM5AGM RR73"));
        Assert.Equal(0, run.Sent);
        Assert.False(SendOne(run, Cq));
        Assert.True(SendOne(run, Cq));
    }

    [Fact]
    public void NoLimitAndCancelNeverStop()
    {
        var run = new WsjtxCqRun();
        run.Start(0, false);
        for (int i = 0; i < 20; i++) Assert.False(SendOne(run, Cq));
        run.Start(1, false);
        run.Cancel();
        Assert.False(SendOne(run, Cq));
    }

    [Fact]
    public void StartedPartWayThroughATransmissionWaitsForTheNext()
    {
        var run = new WsjtxCqRun();
        run.Start(1, true);
        Assert.False(run.Update(false, Cq));  // the one already going out isn't counted
        Assert.True(SendOne(run, Cq));
    }

    [Theory]
    [InlineData("CQ MM5AGM IO85", true)]
    [InlineData("CQ DX MM5AGM IO85", true)]
    [InlineData("  cq mm5agm io85", true)]
    [InlineData("G4ABC MM5AGM RR73", false)]
    [InlineData("CQMM5AGM", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCq(string? message, bool expected) => Assert.Equal(expected, WsjtxCqRun.IsCq(message));

    [Fact]
    public void CqLimitDefaultsToSix()
    {
        Assert.Equal(6, new Yaesu_Web_Control.Models.ApplicationSettings().Ft8TilesCqLimit);
    }

    [Fact]
    public void TxPeaks_SkipTheFirstSecondAndKeepTheLast()
    {
        var t0 = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var p = new TxPeakTracker();
        p.Update(true, 90, 9.9, t0);                    // settling: ignored
        p.Update(true, 80, 5.0, t0.AddSeconds(0.5));    // still settling
        Assert.Null(p.Current);
        p.Update(true, 5, 1.2, t0.AddSeconds(1.5));
        p.Update(true, 12, 1.4, t0.AddSeconds(5));
        p.Update(true, 3, 1.1, t0.AddSeconds(10));
        Assert.Equal(12, p.Current!.AlcPercent);
        p.Update(false, 0, 1.0, t0.AddSeconds(13));
        Assert.Null(p.Current);
        Assert.Equal(12, p.Last!.AlcPercent);
        Assert.Equal(1.4, p.Last.Swr);
        Assert.Equal(t0.AddSeconds(13), p.Last.EndedUtc);
        // A blip shorter than the settle time leaves the last one alone.
        p.Update(true, 99, 9.9, t0.AddSeconds(20));
        p.Update(false, 0, 1.0, t0.AddSeconds(20.5));
        Assert.Equal(12, p.Last.AlcPercent);
    }
}
