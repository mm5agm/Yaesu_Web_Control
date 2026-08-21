using Microsoft.Extensions.Logging.Abstractions;
using Yaesu_Web_Control.Models;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Video;

namespace YaesuWebControl.Tests;

public sealed class VideoCaptureServiceHaltTests
{
    [Fact]
    public async Task AcquireViewerAsync_RefusesWhenHalted()
    {
        var halt = new VideoDisconnectHalt();
        halt.Set();
        var service = CreateService(halt);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AcquireViewerAsync("viewer-1", CancellationToken.None));

        Assert.Contains("disconnected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, service.ViewerCount);
    }

    [Fact]
    public void IsHaltedAfterDisconnect_ReflectsInjectedHalt()
    {
        var halt = new VideoDisconnectHalt();
        var service = CreateService(halt);
        Assert.False(service.IsHaltedAfterDisconnect);
        halt.Set();
        Assert.True(service.IsHaltedAfterDisconnect);
        service.ClearDisconnectHalt();
        Assert.False(service.IsHaltedAfterDisconnect);
    }

    private static VideoCaptureService CreateService(VideoDisconnectHalt halt) =>
        new(
            new FakeSettingsService(),
            new VideoSessionManager(),
            NullLogger<VideoCaptureService>.Instance,
            halt);

    private sealed class FakeSettingsService : ISettingsService
    {
        private ApplicationSettings _settings = new()
        {
            VideoDisplayEnabled = true,
            VideoCaptureDeviceKey = "index:0"
        };

        public Task<ApplicationSettings> GetSettingsAsync() => Task.FromResult(_settings);

        public Task SaveSettingsAsync(ApplicationSettings settings)
        {
            _settings = settings;
            return Task.CompletedTask;
        }

        public ApplicationSettings GetCachedSettings() => _settings;

        public string GetSettingsFilePath() => Path.Combine(Path.GetTempPath(), "ywc-test-settings.json");

        public void InvalidateCache() { }

        public void Dispose() { }
    }
}
