using Yaesu_Web_Control.Services.Video;

namespace YaesuWebControl.Tests;

/// <summary>
/// #175: the capture sizes a stream found are kept on disk, so after a
/// restart the Radio Display offers them before any stream has run. Status
/// must never open the dongle to find them (#173), so the file is the only
/// way they can be there.
/// </summary>
[Collection("VideoDeviceSizeCapsFile")]
public sealed class VideoDeviceSizeCapsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ywc-sizes-" + Guid.NewGuid().ToString("N"));

    public VideoDeviceSizeCapsFileTests()
    {
        VideoDeviceSizeCaps.FilePathOverride = Path.Combine(_dir, "video-capture-sizes.json");
        VideoDeviceSizeCaps.ResetForTests();
    }

    public void Dispose()
    {
        VideoDeviceSizeCaps.ResetForTests();
        VideoDeviceSizeCaps.FilePathOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SizesSurviveARestart()
    {
        VideoDeviceSizeCaps.Remember("uid:dongle-1", ["1920x1080", "1280x720"]);

        VideoDeviceSizeCaps.ResetForTests(); // as if YWC had been restarted

        Assert.Equal(["1920x1080", "1280x720"], VideoDeviceSizeCaps.PeekSizes("uid:dongle-1"));
        Assert.Equal(["1920x1080", "1280x720"], VideoDeviceSizeCaps.SizesFor("uid:dongle-1", allowDeviceOpen: false));
    }

    [Fact]
    public void NoFile_MeansAutoOnly()
    {
        Assert.Empty(VideoDeviceSizeCaps.PeekSizes("uid:dongle-1"));
    }

    [Fact]
    public void ANewerListReplacesTheSavedOne()
    {
        VideoDeviceSizeCaps.Remember("uid:dongle-1", ["800x600"]);
        VideoDeviceSizeCaps.Remember("uid:dongle-1", ["1920x1080"]);
        VideoDeviceSizeCaps.ResetForTests();

        Assert.Equal(["1920x1080"], VideoDeviceSizeCaps.PeekSizes("uid:dongle-1"));
    }

    [Fact]
    public void ADamagedFile_IsIgnored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(VideoDeviceSizeCaps.FilePath, "{ not json");

        Assert.Empty(VideoDeviceSizeCaps.PeekSizes("uid:dongle-1"));

        VideoDeviceSizeCaps.Remember("uid:dongle-1", ["1280x720"]);
        VideoDeviceSizeCaps.ResetForTests();
        Assert.Equal(["1280x720"], VideoDeviceSizeCaps.PeekSizes("uid:dongle-1"));
    }
}
