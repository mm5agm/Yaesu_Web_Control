using Yaesu_Web_Control.Services.Video;

namespace YaesuWebControl.Tests;

/// <summary>
/// The Radio Display capture-size selector. Its failure mode is silent: a
/// stale or impossible size must fall back to automatic rather than leave the
/// panel unable to open, and nothing here shows on screen when it goes wrong.
/// </summary>
public sealed class VideoSizeOptionsTests
{
    [Theory]
    [InlineData("800x600", 800, 600)]
    [InlineData("1280X960", 1280, 960)]
    [InlineData("  1920x1080  ", 1920, 1080)]
    public void TryParse_AcceptsWxH(string spec, int width, int height)
    {
        Assert.True(VideoSizeOptions.TryParse(spec, out var w, out var h));
        Assert.Equal(width, w);
        Assert.Equal(height, h);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("800")]
    [InlineData("800x")]
    [InlineData("800x600x30")]
    [InlineData("0x600")]
    [InlineData("99999x1080")]
    [InlineData("eight hundred")]
    public void TryParse_RejectsAnythingElse(string? spec) =>
        Assert.False(VideoSizeOptions.TryParse(spec, out _, out _));

    [Fact]
    public void FromPins_KeepsCompressedSizesOnlyAndDeduplicates()
    {
        VideoCapturePinRank.Pin[] pins =
        [
            new(1280, 960, 30, true),
            new(800, 600, 30, true),
            new(800, 600, 15, true),   // same size, different rate
            new(1024, 768, 30, false), // uncompressed — the USB2 fps trap
        ];

        Assert.Equal(["800x600", "1280x960"], VideoSizeOptions.FromPins(pins));
    }

    [Fact]
    public void Normalize_KeepsASizeTheDeviceStillOffers() =>
        Assert.Equal("800x600", VideoSizeOptions.Normalize("800x600", ["800x600", "1280x960"]));

    [Fact]
    public void Normalize_FallsBackToAutoWhenTheDeviceNoLongerOffersIt() =>
        Assert.Equal(VideoSizeOptions.Auto, VideoSizeOptions.Normalize("1600x1200", ["800x600", "1280x960"]));

    [Fact]
    public void Normalize_AcceptsAnythingParseableWhenTheListIsUnknown() =>
        Assert.Equal("800x600", VideoSizeOptions.Normalize("800x600", []));

    [Fact]
    public void EncodeWidthFor_UsesTheChosenWidthSoAnExplicitSizeIsNotDownscaled() =>
        Assert.Equal(1280, VideoSizeOptions.EncodeWidthFor("1280x960", maxWidth: 800));

    [Fact]
    public void EncodeWidthFor_LeavesMaxWidthAloneOnAuto() =>
        Assert.Equal(800, VideoSizeOptions.EncodeWidthFor(VideoSizeOptions.Auto, maxWidth: 800));

    [Fact]
    public void PickRequestedSize_ReturnsTheCompressedPinNearestTheRequestedRate()
    {
        VideoCapturePinRank.Pin[] pins =
        [
            new(800, 600, 60, true),
            new(800, 600, 15, true),
            new(1280, 960, 30, true),
        ];

        var pick = VideoCapturePinRank.PickRequestedSize(pins, "800x600", requestedFps: 30);

        Assert.NotNull(pick);
        Assert.Equal(800, pick!.Value.Width);
        Assert.Equal(600, pick.Value.Height);
        Assert.Equal(15, pick.Value.Fps); // |15-30| = 15 beats |60-30| = 30
    }

    [Fact]
    public void PickRequestedSize_IgnoresUncompressedPinsAtTheSameSize()
    {
        VideoCapturePinRank.Pin[] pins = [new(1024, 768, 30, false)];

        Assert.Null(VideoCapturePinRank.PickRequestedSize(pins, "1024x768", requestedFps: 30));
    }

    [Fact]
    public void PickRequestedSize_IsNullOnAutoSoTheRankedPickStillRuns()
    {
        VideoCapturePinRank.Pin[] pins = [new(800, 600, 30, true)];

        Assert.Null(VideoCapturePinRank.PickRequestedSize(pins, VideoSizeOptions.Auto, requestedFps: 30));
    }
}
