using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// Issue #332 (Mike). The Exit page closes YWC from a browser, so another
// program (HRD) can open the radio's COM port. It must tell the browsers
// first, then stop the host exactly once, and be reachable with one click
// from the Radio menu, the Classic nav and the FT8 Tiles header.
public class AppExitTests
{
    [Fact]
    public async Task RequestExit_BroadcastsShutdownThenStopsOnce()
    {
        var lifetime = new FakeLifetime();
        var hub = new RecordingHub();
        var exit = new AppExitService(lifetime, hub, NullLogger<AppExitService>.Instance);

        exit.RequestExit("test");
        exit.RequestExit("test again");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (lifetime.StopCalls == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(700); // a second, wrong, stop would land by now

        Assert.Equal(1, lifetime.StopCalls);
        Assert.Equal(new[] { "RadioStateUpdate" }, hub.Methods);
        Assert.True(hub.SentBeforeStop);
    }

    [Fact]
    public void ExitPage_HasBigButtonsAndCallsTheEndpoint()
    {
        string root = FindRepoRoot();
        string page = File.ReadAllText(Path.Combine(root, "Pages", "Exit.cshtml"));
        Assert.Contains("@page \"/exit\"", page);
        Assert.Contains("id=\"exClose\" aria-label=\"Close YWC\"", page);
        Assert.Contains("id=\"exCancel\"", page);
        Assert.Contains("fetch(\"/api/app/exit\", { method: \"POST\" })", page);

        string controller = File.ReadAllText(Path.Combine(root, "Controllers", "AppExitController.cs"));
        Assert.Contains("[Route(\"api/app\")]", controller);
        Assert.Contains("[HttpPost(\"exit\")]", controller);
        Assert.Contains("HostRuntime.IsContainer", controller); // refused in Docker
    }

    [Theory]
    [InlineData("Pages/RadioPanel.cshtml", "href: '/exit'")]
    [InlineData("Pages/Shared/_Layout.cshtml", "href=\"/exit\"")]
    [InlineData("Pages/Ft8Tiles.cshtml", "id=\"ftCloseYwc\" href=\"/exit\" aria-label=\"Close YWC\"")]
    public void EveryMainViewLinksToTheExitPage(string file, string link)
    {
        string text = File.ReadAllText(Path.Combine(FindRepoRoot(), file));
        Assert.Contains(link, text);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public int StopCalls;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Interlocked.Increment(ref StopCalls);
    }

    private sealed class RecordingHub : IHubContext<RadioHub>, IHubClients, IClientProxy
    {
        public readonly List<string> Methods = new();
        public bool SentBeforeStop => Methods.Count > 0;
        IHubClients IHubContext<RadioHub>.Clients => this;
        IGroupManager IHubContext<RadioHub>.Groups => throw new NotSupportedException();
        public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Client(string connectionId) => this;
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => this;
        public IClientProxy Group(string groupName) => this;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => this;
        public IClientProxy User(string userId) => this;
        public IClientProxy Users(IReadOnlyList<string> userIds) => this;
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            lock (Methods) Methods.Add(method);
            return Task.CompletedTask;
        }
    }
}
