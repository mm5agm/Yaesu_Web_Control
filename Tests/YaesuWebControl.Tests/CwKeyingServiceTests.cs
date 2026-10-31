using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Models;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Cw;

namespace YaesuWebControl.Tests;

/// <summary>
/// Choosing the paddle key line. What matters for safety: it starts as the
/// dry run, it never opens the CAT port (which YWC holds with DTR raised),
/// and outside Windows it refuses. No test here opens a real port: the CAT
/// port and the missing port are names no PC has.
/// </summary>
public sealed class CwKeyingServiceTests
{
    private const string CatPort = "COM199";

    [Fact]
    public void Starts_as_the_dry_run()
    {
        using var service = CreateService();
        var status = service.LineStatus();
        Assert.Null(Prop(status, "keyPort"));
        Assert.Equal(CatPort, Prop(status, "catPort"));
    }

    [Fact]
    public void Refuses_the_CAT_port()
    {
        using var service = CreateService();
        var error = service.SetKeyPort(CatPort.ToLowerInvariant());
        Assert.NotNull(error);
        Assert.Contains(CwKeyingService.Supported ? "CAT port" : "Windows only", error);
        Assert.Null(Prop(service.LineStatus(), "keyPort"));
    }

    [Fact]
    public void Refuses_a_port_that_is_not_there()
    {
        using var service = CreateService();
        var error = service.SetKeyPort("COM198");
        Assert.NotNull(error);
        Assert.Contains(CwKeyingService.Supported ? "not on this PC" : "Windows only", error);
    }

    [Fact]
    public void Choosing_the_dry_run_always_works_on_Windows()
    {
        using var service = CreateService();
        var error = service.SetKeyPort(null);
        if (CwKeyingService.Supported) Assert.Null(error);
        else Assert.Contains("Windows only", error);
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    private static CwKeyingService CreateService() =>
        new(new NoHub(), NullLogger<CwKeyingService>.Instance, new FakeSettingsService());

    // Reports are sent from a background task that catches failures, so a
    // hub with no clients is enough here.
    private sealed class NoHub : IHubContext<RadioHub>
    {
        public IHubClients Clients => null!;
        public IGroupManager Groups => null!;
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        private ApplicationSettings _settings = new() { SerialPort = CatPort };
        public Task<ApplicationSettings> GetSettingsAsync() => Task.FromResult(_settings);
        public Task SaveSettingsAsync(ApplicationSettings settings) { _settings = settings; return Task.CompletedTask; }
        public ApplicationSettings GetCachedSettings() => _settings;
        public string GetSettingsFilePath() => Path.Combine(Path.GetTempPath(), "ywc-test-settings.json");
        public void InvalidateCache() { }
        public void Dispose() { }
    }
}
