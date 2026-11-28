using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

// W2JAZ 2026-11-27: YWC closed itself 30 s after the last browser tab went,
// cutting WSJT-X off, and the rigctld port wasn't retried when it was busy at
// start-up. These pin the counting that keeps YWC open while WSJT-X or N1MM
// is connected, and the netstat parsing that names who holds the port.
public sealed class CatClientPresenceTests
{
    [Fact]
    public void Nothing_connected_is_not_any()
    {
        var p = new CatClientPresence();
        Assert.False(p.Any);
        Assert.Equal("none", p.Describe());
    }

    [Fact]
    public void A_rigctld_client_keeps_it_busy_until_it_goes()
    {
        var p = new CatClientPresence();
        p.RigctldConnected();
        Assert.True(p.Any);
        Assert.Contains("WSJT-X", p.Describe());
        p.RigctldDisconnected();
        Assert.False(p.Any);
    }

    [Fact]
    public void N1mm_counts_too()
    {
        var p = new CatClientPresence();
        p.N1mmConnected();
        Assert.True(p.Any);
        Assert.Contains("N1MM", p.Describe());
    }

    [Fact]
    public void Extra_disconnects_never_go_below_zero()
    {
        var p = new CatClientPresence();
        p.RigctldDisconnected();
        p.N1mmDisconnected();
        p.RigctldConnected();
        Assert.Equal(1, p.Rigctld);
        Assert.Equal(0, p.N1mm);
    }

    [Fact]
    public void Netstat_listening_line_gives_the_pid()
    {
        const string netstat = """

            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1100
              TCP    0.0.0.0:4532           0.0.0.0:0              LISTENING       4242
              TCP    127.0.0.1:4532         127.0.0.1:50000        ESTABLISHED     4242
              TCP    0.0.0.0:45320          0.0.0.0:0              LISTENING       77
            """;
        Assert.Equal(new[] { 4242 }, PortOwner.ListeningPids(netstat, 4532));
    }

    [Fact]
    public void Netstat_without_the_port_gives_nothing()
    {
        Assert.Empty(PortOwner.ListeningPids("  TCP    0.0.0.0:135   0.0.0.0:0   LISTENING   1100", 4532));
    }
}
