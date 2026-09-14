using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// On single-receiver radios (FTdx10 / FT-710 / FTDX3000) receive-controls
// (GT, PA, SH, CO, …) are P1=0-Fixed and address whichever VFO is active.
// MD is not in that set: the FTdx10 CAT manual documents P1 as 0=MAIN /
// VFO-A, 1=SUB / VFO-B, same as FA/FB. Using VfoP1 / SetPerVfo for mode
// made an inactive-VFO mode change rewrite the active VFO as well.
public sealed class ModeVfoRoutingTests
{
    [Theory]
    [InlineData("A", "0")]
    [InlineData("a", "0")]
    [InlineData("B", "1")]
    [InlineData("b", "1")]
    public void ModeP1_FollowsTheRequestedVfo(string receiver, string expected)
        => Assert.Equal(expected, RadioCapabilities.ModeP1(receiver));

    [Fact]
    public void ModeP1_IsNotCollapsedToZeroOnSingleReceiver_UnlikeVfoP1()
    {
        Assert.Equal("0", RadioCapabilities.VfoP1(isSingleReceiver: true, "B"));
        Assert.Equal("1", RadioCapabilities.ModeP1("B"));
    }

    [Fact]
    public void FormatMode_AddressesVfoBIndependently()
    {
        Assert.Equal("MD02;", CatCommands.FormatMode("USB", isSubVfo: false));
        Assert.Equal("MD12;", CatCommands.FormatMode("USB", isSubVfo: true));
        Assert.Equal("MD0C;", CatCommands.FormatMode("DATA-U", isSubVfo: false));
        Assert.Equal("MD1C;", CatCommands.FormatMode("DATA-U", isSubVfo: true));
    }

    [Fact]
    public void Dispatcher_OnSingleReceiver_RoutesMd1ToModeB_EvenWhenVfoAIsActive()
    {
        var (state, dispatcher) = NewDispatcher(singleReceiver: true, activeVfo: 0);
        state.ModeA = "USB";
        state.ModeB = "LSB";

        dispatcher.DispatchMessage("MD1C;");

        Assert.Equal("USB", state.ModeA);
        Assert.Equal("DATA-U", state.ModeB);
    }

    [Fact]
    public void Dispatcher_OnSingleReceiver_RoutesMd0ToModeA_EvenWhenVfoBIsActive()
    {
        var (state, dispatcher) = NewDispatcher(singleReceiver: true, activeVfo: 1);
        state.ModeA = "USB";
        state.ModeB = "LSB";

        dispatcher.DispatchMessage("MD03;");

        Assert.Equal("CW-U", state.ModeA);
        Assert.Equal("LSB", state.ModeB);
    }

    [Fact]
    public void Dispatcher_OnDualReceiver_StillRoutesByP1()
    {
        var (state, dispatcher) = NewDispatcher(singleReceiver: false, activeVfo: 0);
        state.ModeA = "USB";
        state.ModeB = "LSB";

        dispatcher.DispatchMessage("MD14;");

        Assert.Equal("USB", state.ModeA);
        Assert.Equal("FM", state.ModeB);
    }

    private static (RadioStateService State, CatMessageDispatcher Dispatcher) NewDispatcher(
        bool singleReceiver, int activeVfo)
    {
        var persistence = new RadioStatePersistenceService(
            NullLogger<RadioStatePersistenceService>.Instance, null!);

        var state = new RadioStateService(
            NullLogger<RadioStateService>.Instance,
            persistence,
            new SilentHubContext(),
            new UnknownBandPlan())
        {
            IsSingleReceiver = singleReceiver,
            ActiveVfo = activeVfo
        };

        var dispatcher = new CatMessageDispatcher(
            state, NullLogger<CatMessageDispatcher>.Instance);

        return (state, dispatcher);
    }

    private sealed class SilentHubContext : IHubContext<RadioHub>
    {
        public IHubClients Clients { get; } = new SilentClients();
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class SilentClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new SilentProxy();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class SilentProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class UnknownBandPlan : IBandPlanService
    {
        public string CurrentRegion => "Region1";
        public IReadOnlyList<BandEdge> CurrentEdges => Array.Empty<BandEdge>();
        public string BandForFrequency(long hz) => "Unknown";
    }
}
