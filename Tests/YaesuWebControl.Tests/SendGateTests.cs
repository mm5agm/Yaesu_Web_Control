using Xunit;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

public class SendGateTests
{
    private long _now = 10_000;
    private SendGate Gate() => new(() => _now);

    [Fact]
    public void One_send_at_a_time()
    {
        var g = Gate();
        Assert.True(g.TryBegin(out var t, out var stuck));
        Assert.False(stuck);
        _now += 80;
        Assert.False(g.TryBegin(out _, out _));   // dropped while one is in flight
        g.End(t);
        Assert.True(g.TryBegin(out _, out _));
    }

    [Fact]
    public void A_send_that_never_finishes_is_given_up_after_two_seconds()
    {
        var g = Gate();
        Assert.True(g.TryBegin(out var first, out _));
        _now += SendGate.StuckMs - 1;
        Assert.False(g.TryBegin(out _, out _));
        _now += 1;
        Assert.True(g.TryBegin(out var second, out var stuck));
        Assert.True(stuck);
        // The old send finishing late must not free the new one's slot.
        g.End(first);
        Assert.False(g.TryBegin(out _, out _));
        g.End(second);
        Assert.True(g.TryBegin(out _, out _));
    }

    [Fact]
    public void Both_frame_pumps_use_it()
    {
        static string Read(string p) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", p));
        foreach (var p in new[] { "Services/Audio/FilterSpectrumService.cs", "Services/Sdr/AccAudioService.cs" })
        {
            var src = Read(p);
            Assert.Contains("_sendGate.TryBegin(", src);
            Assert.DoesNotContain("_sendInFlight", src);
        }
    }
}
