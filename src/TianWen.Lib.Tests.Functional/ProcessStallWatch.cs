using System;
using System.Diagnostics;
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
            if (woke - Tick >= Late)
            {
                Console.Out.WriteLine(
                    $"{DateTime.Now:HH:mm:ss.fff} info process stall: a thread that asked for {Tick.TotalMilliseconds:0} ms of sleep woke after {woke.TotalSeconds:0.0} s; "
                    + $"the GC paused {(paused - pausedBefore).TotalSeconds:0.0} s of it over {gen2 - gen2Before} gen2 collection(s); {NodeWait.PoolState()}");
            }
            pausedBefore = paused;
            gen2Before = gen2;
        }
    }
}
