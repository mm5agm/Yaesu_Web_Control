using Yaesu_Web_Control.Services.Video;

namespace YaesuWebControl.Tests;

public sealed class VideoJpegSofTests
{
    [Fact]
    public void TryReadSize_ReadsSof0Dimensions()
    {
        // SOI + SOF0 800×600
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x02, 0x58, 0x03, 0x20, 0x03, 0x01,
            0xFF, 0xD9
        ];

        Assert.True(VideoJpegSof.TryReadSize(jpeg, out var w, out var h));
        Assert.Equal(800, w);
        Assert.Equal(600, h);
    }

    [Fact]
    public void TryReadSize_SkipsAppAndDqtBeforeSof()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03, 0x01
        ];

        Assert.True(VideoJpegSof.TryReadSize(jpeg, out var w, out var h));
        Assert.Equal(640, w);
        Assert.Equal(480, h);
    }

    [Fact]
    public void TryReadSize_RejectsMissingSoi()
    {
        byte[] jpeg = [0x00, 0x00, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x02, 0x58, 0x03, 0x20, 0x03];
        Assert.False(VideoJpegSof.TryReadSize(jpeg, out _, out _));
    }
}

public sealed class VideoCapturePinRankFpsTests
{
    [Fact]
    public void MeetingFps_Prefers640At30Over800At20()
    {
        VideoCapturePinRank.Pin[] pins =
        [
            new(800, 600, 20, Jpeg: true),
            new(640, 480, 30, Jpeg: true)
        ];

        var pick = VideoCapturePinRank.PickMjpegCaptureMeetingFps(pins, requestedFps: 30, maxWidth: 800);
        Assert.NotNull(pick);
        Assert.Equal(640, pick.Value.Width);
        Assert.Equal(480, pick.Value.Height);
        Assert.Equal(30, pick.Value.Fps);
    }

    [Fact]
    public void MeetingFps_Keeps800WhenItCanDo30()
    {
        VideoCapturePinRank.Pin[] pins =
        [
            new(800, 600, 30, Jpeg: true),
            new(640, 480, 30, Jpeg: true)
        ];

        var pick = VideoCapturePinRank.PickMjpegCaptureMeetingFps(pins, requestedFps: 30, maxWidth: 800);
        Assert.NotNull(pick);
        Assert.Equal(800, pick.Value.Width);
        Assert.Equal(600, pick.Value.Height);
    }

    [Fact]
    public void MeetingFps_FallsBackWhenNothingCanDoRequest()
    {
        VideoCapturePinRank.Pin[] pins =
        [
            new(800, 600, 20, Jpeg: true)
        ];

        var pick = VideoCapturePinRank.PickMjpegCaptureMeetingFps(pins, requestedFps: 30, maxWidth: 800);
        Assert.NotNull(pick);
        Assert.Equal(800, pick.Value.Width);
        Assert.Equal(20, pick.Value.Fps);
    }
}
