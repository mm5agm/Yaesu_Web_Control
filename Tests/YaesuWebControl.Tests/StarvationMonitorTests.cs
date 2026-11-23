using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

/// <summary>
/// The pure parts of the starvation monitor (ported from IWC Discussion #51): episodes are
/// logged once at each end, and the CPU sums are right.
/// </summary>
public class StarvationMonitorTests
{
    [Fact]
    public void Episode_starts_once_and_ends_once()
    {
        var t = new EpisodeTracker(startAfter: 1, endAfter: 1);
        Assert.Equal(EpisodeChange.None, t.Observe(false, 0));
        Assert.Equal(EpisodeChange.Started, t.Observe(true, 1000));
        Assert.Equal(EpisodeChange.None, t.Observe(true, 2000));
        Assert.Equal(EpisodeChange.None, t.Observe(true, 3000));
        Assert.Equal(EpisodeChange.Ended, t.Observe(false, 4500));
        Assert.Equal(3500, t.LastDurationMs);
        Assert.False(t.Active);
    }

    [Fact]
    public void Episode_needs_a_run_to_start_and_a_run_to_end()
    {
        var t = new EpisodeTracker(startAfter: 3, endAfter: 2);
        Assert.Equal(EpisodeChange.None, t.Observe(true, 0));
        Assert.Equal(EpisodeChange.None, t.Observe(true, 1));
        Assert.Equal(EpisodeChange.None, t.Observe(false, 2)); // run broken
        Assert.Equal(EpisodeChange.None, t.Observe(true, 3));
        Assert.Equal(EpisodeChange.None, t.Observe(true, 4));
        Assert.Equal(EpisodeChange.Started, t.Observe(true, 5));
        Assert.Equal(EpisodeChange.None, t.Observe(false, 6));
        Assert.Equal(EpisodeChange.None, t.Observe(true, 7)); // run broken
        Assert.Equal(EpisodeChange.None, t.Observe(false, 8));
        Assert.Equal(EpisodeChange.Ended, t.Observe(false, 9));
        Assert.Equal(4, t.LastDurationMs);
    }

    [Theory]
    [InlineData(0UL, 0UL, 750UL, 1000UL, 25.0)]
    [InlineData(100UL, 1000UL, 100UL, 2000UL, 100.0)]
    [InlineData(100UL, 1000UL, 1100UL, 2000UL, 0.0)]
    public void Pc_busy_percent(ulong idle0, ulong total0, ulong idle1, ulong total1, double expected)
    {
        Assert.Equal(expected, CpuMath.BusyPercent(idle0, total0, idle1, total1)!.Value, 3);
    }

    [Fact]
    public void Pc_busy_percent_is_unknown_when_the_counters_go_backwards()
    {
        Assert.Null(CpuMath.BusyPercent(0, 1000, 0, 1000));
        Assert.Null(CpuMath.BusyPercent(500, 1000, 400, 2000));
    }

    [Fact]
    public void Process_percent_is_a_share_of_all_processors()
    {
        // One core flat out for 1 s on a 4-core PC is 25%.
        Assert.Equal(25.0, CpuMath.ProcessPercent(TimeSpan.FromMilliseconds(1000), 1000, 4)!.Value, 3);
        Assert.Null(CpuMath.ProcessPercent(TimeSpan.FromSeconds(1), 0, 4));
    }

    [Fact]
    public void Proc_stat_line_is_parsed()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        var r = CpuMath.ParseProcStat("cpu  100 0 50 800 50 0 0 0 30 0");
        Assert.Equal((850UL, 1000UL), r);
        Assert.Null(CpuMath.ParseProcStat("cpu0 1 2 3 4"));
        Assert.Null(CpuMath.ParseProcStat(null));
    }

    [Fact]
    public void Events_are_kept_for_the_diagnostics_page_newest_last_and_capped()
    {
        var m = new StarvationMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<StarvationMonitor>.Instance);
        Assert.Empty(m.Snapshot().Events);

        for (int i = 0; i < StarvationMonitor.EventsKept + 5; i++)
            m.Record(i % 2 == 0, "Test", $"event {i}");

        var s = m.Snapshot();
        Assert.Equal(StarvationMonitor.EventsKept, s.Events.Length);
        Assert.Equal("event 5", s.Events[0].Text);
        Assert.Equal($"event {StarvationMonitor.EventsKept + 4}", s.Events[^1].Text);
        Assert.Equal(Environment.ProcessorCount, s.Processors);
    }

    [Fact]
    public void Thread_pool_snapshot_names_the_numbers()
    {
        string s = ThreadPoolSnapshot.Describe();
        Assert.Contains("Pool threads", s);
        Assert.Contains("jobs waiting", s);
    }
}
