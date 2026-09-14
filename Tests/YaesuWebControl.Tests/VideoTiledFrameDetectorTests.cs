using Yaesu_Web_Control.Services.Video;

namespace YaesuWebControl.Tests;

/// <summary>
/// #132. The tiled-frame detector is the only thing that can see a capture
/// device packing several pictures into one well-formed JPEG, and its failure
/// modes are both silent: miss it and the operator watches a wrong picture with
/// nothing in the log, trip on ordinary content and the display rebuilds itself
/// for no reason. The real fault is not reproducible on demand, so these are
/// synthetic sizes built from the one occurrence I measured - 143 KB healthy,
/// 330 KB tiled.
/// </summary>
public sealed class VideoTiledFrameDetectorTests
{
    private const int Healthy = 143_000;
    private const int Tiled = 330_000;

    private static VideoTiledFrameDetector Warm(int size = Healthy, int window = 30)
    {
        var d = new VideoTiledFrameDetector(window);
        for (var i = 0; i < window; i++)
            Assert.False(d.Observe(size));
        return d;
    }

    [Fact]
    public void SaysNothingUntilItHasAMedian()
    {
        var d = new VideoTiledFrameDetector(window: 30);
        for (var i = 0; i < 29; i++)
            Assert.False(d.Observe(Tiled));

        Assert.False(d.Ready);
        Assert.Equal(0, d.Median);
    }

    [Fact]
    public void FiresOnTheMeasuredTiledRatio()
    {
        var d = Warm();

        Assert.False(d.Observe(Tiled));
        Assert.False(d.Observe(Tiled));
        Assert.True(d.Observe(Tiled));
    }

    [Fact]
    public void ReportsTheMedianItJudgedAgainst()
    {
        var d = Warm();
        Assert.Equal(Healthy, d.Median);
    }

    [Fact]
    public void IgnoresASingleLargeFrame()
    {
        var d = Warm();

        Assert.False(d.Observe(Tiled));
        Assert.False(d.Observe(Healthy));
        Assert.False(d.Observe(Tiled));
        Assert.False(d.Observe(Tiled));
    }

    /// <summary>
    /// Screen content moves the frame size around on its own - a spectrum sweep
    /// carries far more detail than an idle panel. Anything short of doubling
    /// must be left alone.
    /// </summary>
    [Theory]
    [InlineData(1.3)]
    [InlineData(1.6)]
    [InlineData(1.9)]
    public void IgnoresOrdinaryContentSwing(double factor)
    {
        var d = Warm();
        var busy = (int)(Healthy * factor);

        for (var i = 0; i < 20; i++)
            Assert.False(d.Observe(busy));
    }

    /// <summary>
    /// A median, not a mean: a sustained tiled run must not quietly become the
    /// new baseline and stop being reported. The window is 30 and the run here
    /// is longer, so a mean would have swallowed it.
    /// </summary>
    [Fact]
    public void KeepsReportingASustainedTiledRun()
    {
        var d = Warm();
        var fired = 0;

        for (var i = 0; i < 60; i++)
        {
            if (d.Observe(Tiled))
                fired++;
        }

        Assert.True(fired >= 2, $"expected repeat reports, got {fired}");
    }

    [Fact]
    public void ResetsSoAnIgnoredVerdictIsRaisedAgain()
    {
        var d = Warm();
        for (var i = 0; i < 3; i++)
            d.Observe(Tiled);

        // Caller ignored it. Three more must say so again rather than the
        // detector latching true or going quiet.
        Assert.False(d.Observe(Tiled));
        Assert.False(d.Observe(Tiled));
        Assert.True(d.Observe(Tiled));
    }

    [Fact]
    public void ScalesWithTheStreamRatherThanAByteCount()
    {
        // A small capture size at low quality: same ratio, tenth the bytes.
        var d = Warm(size: 14_300);

        Assert.False(d.Observe(33_000));
        Assert.False(d.Observe(33_000));
        Assert.True(d.Observe(33_000));
    }
}
