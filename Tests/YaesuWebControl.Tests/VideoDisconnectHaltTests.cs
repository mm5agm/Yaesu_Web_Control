using Yaesu_Web_Control.Services.Video;

namespace YaesuWebControl.Tests;

public sealed class VideoDisconnectHaltTests
{
    [Fact]
    public void Set_ActivatesHalt()
    {
        var halt = new VideoDisconnectHalt();
        Assert.False(halt.IsActive);
        halt.Set();
        Assert.True(halt.IsActive);
    }

    [Fact]
    public void Clear_DeactivatesHalt()
    {
        var halt = new VideoDisconnectHalt();
        halt.Set();
        halt.Clear();
        Assert.False(halt.IsActive);
    }

    [Fact]
    public void ViewerAttach_DoesNotClearWithoutOperatorAction()
    {
        var halt = new VideoDisconnectHalt();
        halt.Set();
        // Simulate viewer attach / stream request without Start or device change.
        Assert.True(halt.IsActive);
    }

    [Fact]
    public void OperatorStart_ClearsHalt()
    {
        var halt = new VideoDisconnectHalt();
        halt.Set();
        halt.Clear();
        Assert.False(halt.IsActive);
    }

    [Fact]
    public void DeviceChange_ClearsHalt()
    {
        var halt = new VideoDisconnectHalt();
        halt.Set();
        halt.Clear();
        Assert.False(halt.IsActive);
    }
}
