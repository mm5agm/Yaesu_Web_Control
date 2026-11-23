using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Watches, from its own thread, whether YWC is getting time to run (ported from IWC
    /// Discussion #51). Every second it hands the thread pool a tiny job and
    /// times how long the job waits. When the wait passes 2 s it logs what the
    /// pool and the PC were doing, then logs again when things recover. It also
    /// logs when the whole PC has been flat out for a while, and when YWC's own
    /// clock jumps because the PC was asleep. Its own thread keeps running
    /// when every pool thread is stuck, so a freeze can explain itself.
    /// </summary>
    public sealed class StarvationMonitor : IHostedService, IDisposable
    {
        internal const int ProbeEveryMs = 1000;
        internal const int LagWarnMs = 2000;
        internal const int PcBusyPercent = 90;
        internal const int PcBusyAfterSamples = 10;
        internal const int PcCalmPercent = 70;
        internal const int PcCalmAfterSamples = 5;
        internal const int AsleepGapMs = 10000;
        internal const int EventsKept = 50;

        private readonly ILogger<StarvationMonitor> _logger;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _lock = new();
        private readonly Queue<LoadEvent> _events = new();
        private readonly DateTime _startedAt = DateTime.Now;
        private Thread? _thread;
        private long _probeDoneAt;
        private double? _pcPercent;
        private double? _appPercent;
        private long _poolWaitMs;
        private long _longestWaitMs;

        public StarvationMonitor(ILogger<StarvationMonitor> logger) => _logger = logger;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "YWC starvation monitor" };
            _thread.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stop.Cancel();
            return Task.CompletedTask;
        }

        public void Dispose() => _stop.Dispose();

        /// <summary>
        /// Logs a load event and keeps it for the Diagnostics page, so a user
        /// can copy the freezes without finding the log (ported from IWC Discussion #51).
        /// </summary>
        public void Record(bool warning, string source, string text)
        {
            if (warning) _logger.LogWarning("[{Source}] {Text}", source, text);
            else _logger.LogInformation("[{Source}] {Text}", source, text);
            lock (_lock)
            {
                _events.Enqueue(new LoadEvent(DateTime.Now, warning, text));
                while (_events.Count > EventsKept) _events.Dequeue();
            }
        }

        /// <summary>The figures and events the Diagnostics page shows.</summary>
        public LoadSnapshot Snapshot()
        {
            ThreadPool.GetAvailableThreads(out int freeWorkers, out _);
            ThreadPool.GetMaxThreads(out int maxWorkers, out _);
            lock (_lock)
            {
                return new LoadSnapshot(
                    Environment.ProcessorCount, _pcPercent, _appPercent, _poolWaitMs, _longestWaitMs,
                    ThreadPool.ThreadCount, maxWorkers - freeWorkers, ThreadPool.PendingWorkItemCount,
                    _startedAt, _events.ToArray());
            }
        }

        private void Run()
        {
            try
            {
                ThreadPool.GetMinThreads(out int minWorkers, out _);
                _logger.LogInformation("[Starvation] Monitor on: {Cpus} processors, thread pool minimum {Min}",
                    Environment.ProcessorCount, minWorkers);

                var lag = new EpisodeTracker(startAfter: 1, endAfter: 1);
                var busy = new EpisodeTracker(startAfter: PcBusyAfterSamples, endAfter: PcCalmAfterSamples);
                var cpu = new CpuSampler();
                long worstLag = 0;
                long probeQueuedAt = 0;
                long lastTick = Environment.TickCount64;

                while (!_stop.Token.WaitHandle.WaitOne(ProbeEveryMs))
                {
                    long now = Environment.TickCount64;
                    long gap = now - lastTick;
                    lastTick = now;
                    cpu.Sample();
                    lock (_lock) { _pcPercent = cpu.PcPercent; _appPercent = cpu.AppPercent; }

                    if (gap >= AsleepGapMs)
                    {
                        // Even this thread didn't run: the PC was asleep or paused.
                        Record(true, "Starvation", $"YWC didn't run at all for {gap / 1000} s; the PC was asleep or paused");
                        probeQueuedAt = 0;
                        continue;
                    }

                    // Thread pool: one probe at a time; it waits as long as the pool does.
                    long lagMs = -1;
                    if (probeQueuedAt == 0)
                    {
                        Interlocked.Exchange(ref _probeDoneAt, 0);
                        probeQueuedAt = now;
                        ThreadPool.UnsafeQueueUserWorkItem(_ => Interlocked.Exchange(ref _probeDoneAt, Environment.TickCount64), null);
                    }
                    else
                    {
                        long doneAt = Interlocked.Read(ref _probeDoneAt);
                        if (doneAt != 0)
                        {
                            lagMs = doneAt - probeQueuedAt;
                            probeQueuedAt = 0;
                        }
                        else
                        {
                            lagMs = now - probeQueuedAt;
                        }
                    }

                    if (lagMs >= 0)
                    {
                        lock (_lock) { _poolWaitMs = lagMs; _longestWaitMs = Math.Max(_longestWaitMs, lagMs); }
                        worstLag = lag.Active ? Math.Max(worstLag, lagMs) : lagMs;
                        switch (lag.Observe(lagMs >= LagWarnMs, now))
                        {
                            case EpisodeChange.Started:
                                Record(true, "Starvation", $"YWC's thread pool is starved: a job has waited {lagMs} ms. {ThreadPoolSnapshot.Describe()}. {cpu.Describe()}");
                                break;
                            case EpisodeChange.Ended:
                                Record(false, "Starvation", $"Thread pool running again after {lag.LastDurationMs / 1000} s (longest wait {worstLag} ms). {ThreadPoolSnapshot.Describe()}. {cpu.Describe()}");
                                break;
                        }
                    }

                    if (cpu.PcPercent is double pc)
                    {
                        bool high = busy.Active ? pc >= PcCalmPercent : pc >= PcBusyPercent;
                        switch (busy.Observe(high, now))
                        {
                            case EpisodeChange.Started:
                                Record(true, "Starvation", $"The PC has been busy for {PcBusyAfterSamples} s. {cpu.Describe()}");
                                break;
                            case EpisodeChange.Ended:
                                Record(false, "Starvation", $"The PC is calm again after {busy.LastDurationMs / 1000} s. {cpu.Describe()}");
                                break;
                        }
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Starvation] Monitor stopped");
            }
        }
    }

    public sealed record LoadEvent(DateTime Time, bool Warning, string Text);

    public sealed record LoadSnapshot(
        int Processors, double? PcPercent, double? AppPercent, long PoolWaitMs, long LongestWaitMs,
        int PoolThreads, int BusyWorkers, long JobsWaiting, DateTime StartedAt, LoadEvent[] Events);


    internal enum EpisodeChange { None, Started, Ended }

    /// <summary>
    /// Turns a stream of yes/no samples into episodes: it starts after
    /// <c>startAfter</c> yeses in a row and ends after <c>endAfter</c> noes in
    /// a row, so each episode is logged once at each end.
    /// </summary>
    internal sealed class EpisodeTracker
    {
        private readonly int _startAfter;
        private readonly int _endAfter;
        private int _run;
        private long _startedAt;

        public EpisodeTracker(int startAfter, int endAfter)
        {
            _startAfter = Math.Max(1, startAfter);
            _endAfter = Math.Max(1, endAfter);
        }

        public bool Active { get; private set; }
        public long LastDurationMs { get; private set; }

        public EpisodeChange Observe(bool bad, long nowMs)
        {
            if (Active == bad) { _run = 0; return EpisodeChange.None; }
            _run++;
            if (!Active && _run >= _startAfter)
            {
                Active = true;
                _run = 0;
                _startedAt = nowMs;
                return EpisodeChange.Started;
            }
            if (Active && _run >= _endAfter)
            {
                Active = false;
                _run = 0;
                LastDurationMs = nowMs - _startedAt;
                return EpisodeChange.Ended;
            }
            return EpisodeChange.None;
        }
    }

    /// <summary>The thread pool's numbers, for the log.</summary>
    internal static class ThreadPoolSnapshot
    {
        public static string Describe()
        {
            ThreadPool.GetAvailableThreads(out int freeWorkers, out _);
            ThreadPool.GetMaxThreads(out int maxWorkers, out _);
            ThreadPool.GetMinThreads(out int minWorkers, out _);
            int busyWorkers = maxWorkers - freeWorkers;
            string process = "";
            try
            {
                using var p = Process.GetCurrentProcess();
                process = $", YWC threads {p.Threads.Count}, memory {p.WorkingSet64 / (1024 * 1024)} MB";
            }
            catch { }
            return $"Pool threads {ThreadPool.ThreadCount} ({busyWorkers} busy, minimum {minWorkers}), jobs waiting {ThreadPool.PendingWorkItemCount}" +
                   $"{process}, full GCs {GC.CollectionCount(2)}";
        }
    }

    /// <summary>
    /// How busy the whole PC and YWC itself are, measured between two samples.
    /// </summary>
    internal sealed class CpuSampler
    {
        private (ulong Idle, ulong Total)? _lastPc;
        private TimeSpan _lastProc;
        private long _lastWall;

        public double? PcPercent { get; private set; }
        public double? AppPercent { get; private set; }

        public void Sample()
        {
            long wall = Environment.TickCount64;
            var pc = ReadPcTimes();
            if (pc is { } now && _lastPc is { } before)
                PcPercent = CpuMath.BusyPercent(before.Idle, before.Total, now.Idle, now.Total);
            _lastPc = pc;

            try
            {
                using var p = Process.GetCurrentProcess();
                TimeSpan proc = p.TotalProcessorTime;
                if (_lastWall != 0)
                    AppPercent = CpuMath.ProcessPercent(proc - _lastProc, wall - _lastWall, Environment.ProcessorCount);
                _lastProc = proc;
                _lastWall = wall;
            }
            catch { }
        }

        public string Describe() =>
            $"PC CPU {Format(PcPercent)}, YWC CPU {Format(AppPercent)} of all {Environment.ProcessorCount} processors";

        private static string Format(double? v) => v is double d ? $"{d:0}%" : "unknown";

        private static (ulong Idle, ulong Total)? ReadPcTimes()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;
                    // Kernel time includes idle time.
                    return ((ulong)idle, (ulong)kernel + (ulong)user);
                }
                if (OperatingSystem.IsLinux())
                    return CpuMath.ParseProcStat(File.ReadLines("/proc/stat").FirstOrDefault());
            }
            catch { }
            return null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
    }

    internal static class CpuMath
    {
        /// <summary>Busy share of all processors between two idle/total readings.</summary>
        public static double? BusyPercent(ulong idleBefore, ulong totalBefore, ulong idleNow, ulong totalNow)
        {
            if (totalNow <= totalBefore || idleNow < idleBefore) return null;
            double total = totalNow - totalBefore;
            double idle = idleNow - idleBefore;
            return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
        }

        /// <summary>One process's share of all processors over a wall-clock interval.</summary>
        public static double? ProcessPercent(TimeSpan cpuUsed, long wallMs, int processors)
        {
            if (wallMs <= 0 || processors <= 0) return null;
            return Math.Clamp(100.0 * cpuUsed.TotalMilliseconds / (wallMs * (double)processors), 0, 100);
        }

        /// <summary>Linux /proc/stat "cpu" line → idle (idle + iowait) and total jiffies.</summary>
        public static (ulong Idle, ulong Total)? ParseProcStat(string? line)
        {
            if (line == null || !line.StartsWith("cpu ")) return null;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return null;
            ulong total = 0, idle = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                if (!ulong.TryParse(parts[i], out ulong v)) return null;
                // guest and guest_nice (fields 9, 10) are already counted in user and nice.
                if (i <= 8) total += v;
                if (i == 4 || i == 5) idle += v;
            }
            return (idle, total);
        }
    }
}
