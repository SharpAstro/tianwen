using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Says so when the whole test process stops for seconds, and what it was doing meanwhile (#2000). CI showed two unrelated tests
/// (a goto and a TUI disconnect) both silent from 01:08:26 to 01:08:43.775, then both resuming at once: one stall shared by
/// every test, which no per-test budget explains and no per-test log can attribute. A thread of its own (never a pool worker,
/// which is what a stall would starve) sleeps a short while and measures how late it wakes; a late wake is written to the
/// console with the garbage collector's pause time and the pool's state beside it, which tells a collection (or memory
/// pressure) from CPU starvation from a pool with nothing to run on.
/// </summary>
internal static class ProcessStallWatch
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Late = TimeSpan.FromSeconds(1.5);

    // A test assembly is the one place a module initializer belongs: nothing else would start the watch before the first test.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Start()
    {
        var thread = new Thread(Watch) { IsBackground = true, Name = "process stall watch" };
        thread.Start();
    }
#pragma warning restore CA2255

    // How late a work item queued on the thread pool ran: a pool with nothing to run on (its workers blocked) delays every
    // request an in-process node serves while this thread, which is not the pool's, wakes on time (#2000: a 2 ms GET took
    // 12 s for 40 s while no stall was reported). -1 while the latest probe is still waiting.
    private static long _poolProbeQueuedTicks;
    private static long _poolProbeLatencyTicks;
    private static readonly TimeSpan PoolLate = TimeSpan.FromSeconds(1);

    private static void Watch()
    {
        var clock = Stopwatch.StartNew();
        var pausedBefore = GC.GetTotalPauseDuration();
        var gen2Before = GC.CollectionCount(2);
        while (true)
        {
            var asked = clock.Elapsed;
            Thread.Sleep(Tick);
            var woke = clock.Elapsed - asked;
            var paused = GC.GetTotalPauseDuration();
            var gen2 = GC.CollectionCount(2);

            // The pool probe: report the last one's latency, or how long the one still queued has waited, then queue the next.
            var queued = Interlocked.Read(ref _poolProbeQueuedTicks);
            var latency = Interlocked.Read(ref _poolProbeLatencyTicks);
            var poolWait = latency >= 0 ? TimeSpan.FromTicks(latency) : clock.Elapsed - TimeSpan.FromTicks(queued);
            if (latency >= 0)
            {
                Interlocked.Exchange(ref _poolProbeLatencyTicks, -1);
                Interlocked.Exchange(ref _poolProbeQueuedTicks, clock.Elapsed.Ticks);
                ThreadPool.UnsafeQueueUserWorkItem(static c => Interlocked.Exchange(ref _poolProbeLatencyTicks,
                    ((Stopwatch)c!).Elapsed.Ticks - Interlocked.Read(ref _poolProbeQueuedTicks)), clock);
            }

            if (woke - Tick >= Late || poolWait >= PoolLate)
            {
                System.Console.Out.WriteLine(
                    $"{DateTime.Now:HH:mm:ss.fff} info process stall: a thread that asked for {Tick.TotalMilliseconds:0} ms of sleep woke after {woke.TotalSeconds:0.0} s; "
                    + $"a pool work item waited {poolWait.TotalSeconds:0.0} s; "
                    + $"the GC paused {(paused - pausedBefore).TotalSeconds:0.0} s of it over {gen2 - gen2Before} gen2 collection(s); {NodeWait.PoolState()}; {Machine()}");
            }
            pausedBefore = paused;
            gen2Before = gen2;
        }
    }

    // The machine around the process, where Linux says it: CPU load (a node spawned beside it competes for the runner's four
    // cores) and memory left (a runner short of it swaps). Elsewhere, nothing.
    private static string Machine()
    {
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                return "machine: not read off Linux";
            }
            var load = System.IO.File.ReadAllText("/proc/loadavg").Split(' ');
            var available = System.IO.File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemAvailable:", StringComparison.Ordinal))?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
            return $"machine: load {load[0]}/{load[1]} (1/5 min, {Environment.ProcessorCount} CPUs), {(long.TryParse(available, out var kb) ? kb / 1024 : -1)} MB available, "
                + $"this process {System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds:0} s CPU";
        }
        catch (Exception ex)
        {
            return $"machine: unread ({ex.GetType().Name})";
        }
    }
}
