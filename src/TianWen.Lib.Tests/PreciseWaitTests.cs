using System;
using System.Diagnostics;
using System.Threading;
using Shouldly;
using TianWen.DAL;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="PreciseWait"/> ends a millisecond's wait on time where <see cref="Thread.Sleep(int)"/> waits out a whole Windows timer
/// tick (15.625 ms by default): QHY's live read polls with it (2026-11-05).
/// </summary>
/// <remarks>A wall-clock measurement, so off CI (<see cref="StressTestGate"/>): a shared runner measures the runner.</remarks>
public class PreciseWaitTests(ITestOutputHelper output)
{
    private const int Waits = 50;

    [Fact]
    public void AMillisecondsWaitEndsOnTimeWhereASleepWaitsATick()
    {
        StressTestGate.SkipOnCi();
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the 15.6 ms tick is Windows'; other systems already sleep fine-grained");

        var precise = Average(() => PreciseWait.Sleep(TimeSpan.FromMilliseconds(1)));
        var sleep = Average(() => Thread.Sleep(1));
        output.WriteLine($"{Waits} waits of 1 ms: PreciseWait {precise:F2} ms each, Thread.Sleep {sleep:F2} ms each");

        precise.ShouldBeGreaterThanOrEqualTo(0.9, "it waits at least what was asked");
        precise.ShouldBeLessThan(4, "it ends near the millisecond, not at a timer tick");
    }

    [Fact]
    public void NoWaitReturnsAtOnce()
    {
        var clock = Stopwatch.StartNew();
        PreciseWait.Sleep(TimeSpan.Zero);
        PreciseWait.Sleep(TimeSpan.FromMilliseconds(-5));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(50));
    }

    private static double Average(Action wait)
    {
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < Waits; i++)
        {
            wait();
        }

        return clock.Elapsed.TotalMilliseconds / Waits;
    }
}
