using System.ComponentModel;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// Issue #124. RadioStateService.SWRMeter used to carry a spike filter that
// rejected any reading more than 30 away from the previous one. On a genuinely
// high SWR the first reading of the over was 255, that became the anchor, and
// every later reading was then "a spike" and thrown away. Because a rejected
// write never reaches SetField, no PropertyChanged fired and no SignalR frame
// went out either -- so the browser did not draw a high SWR, it drew nothing at
// all and the gauge sat wherever it was, usually 0. The operator's only clue
// was a wrong needle on a real antenna fault, which is precisely the kind of
// wrong answer that is silent rather than visible.
//
// These tests pin the property to "clamp, then broadcast every change" and
// nothing else. Smoothing belongs on the client, where FTdx101Meters._processSWR
// averages the last three readings.
public sealed class SwrMeterBroadcastTests
{
    [Fact]
    public void SWRMeter_BroadcastsALargeDropAfterAHighReading()
    {
        var (state, changed) = NewState();

        state.SWRMeter = 255;   // key down into a bad load
        state.SWRMeter = 26;    // the ATU catches up -- this used to be discarded

        Assert.Equal(26, state.SWRMeter);
        Assert.Equal(2, changed.Count(p => p == nameof(state.SWRMeter)));
    }

    [Fact]
    public void SWRMeter_KeepsFollowingTheRadioAfterAHighFirstReading()
    {
        var (state, changed) = NewState();

        // The exact sequence from Colin's bench log, where every value after
        // the first was logged as "[SWRMeter] Ignored spike" and dropped.
        foreach (var reading in new[] { 255, 26, 75, 3, 22, 4 })
            state.SWRMeter = reading;

        Assert.Equal(4, state.SWRMeter);
        Assert.Equal(6, changed.Count(p => p == nameof(state.SWRMeter)));
    }

    [Fact]
    public void SWRMeter_ClampsToTheMeterRange()
    {
        var (state, _) = NewState();

        state.SWRMeter = 300;
        Assert.Equal(255, state.SWRMeter);

        state.SWRMeter = -5;
        Assert.Equal(0, state.SWRMeter);
    }

    [Fact]
    public void SWRMeter_DoesNotBroadcastAnUnchangedValue()
    {
        var (state, changed) = NewState();

        state.SWRMeter = 40;
        state.SWRMeter = 40;

        Assert.Equal(1, changed.Count(p => p == nameof(state.SWRMeter)));
    }

    // The service takes its collaborators as concrete types, so this builds the
    // real persistence service with a null environment -- its constructor does
    // not use one, and IsInitialized stays false here so nothing is ever saved.
    // Load() reads the host's radio_state.json if one exists; only the starting
    // values come from it, and SWRMeter is not among them.
    private static (RadioStateService State, List<string> Changed) NewState()
    {
        var persistence = new RadioStatePersistenceService(
            NullLogger<RadioStatePersistenceService>.Instance, null!);

        var state = new RadioStateService(
            NullLogger<RadioStateService>.Instance,
            persistence,
            new SilentHubContext(),
            new UnknownBandPlan());

        var changed = new List<string>();
        state.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        return (state, changed);
    }

    // SetField raises PropertyChanged and broadcasts on the same branch, so the
    // event count is a faithful stand-in for "a SignalR frame went out". The hub
    // itself only has to not throw.
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
