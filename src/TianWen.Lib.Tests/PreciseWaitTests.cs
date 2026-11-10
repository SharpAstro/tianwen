using System;
using System.Diagnostics;
using System.Threading;
using Shouldly;
using TianWen.DAL;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="PreciseWait"/> ends a millisecond's wait on time, where <see cref="Thread.Sleep(int)"/> waits out a whole Windows
/// timer tick (15.625 ms by default): QHY's live read polls with it (2026-11-05).
/// </summary>
/// <remarks>
/// Wall-clock measurements, so opt-in (<see cref="TimingTestGate"/>, <c>TIANWEN_TIMING_TESTS=1</c>, <c>Category=Timing</c>), run
/// alone in <see cref="TimingCollection"/>, and never on CI: a loaded machine fails the bars whatever the code does (#2031).
/// <see cref="PreciseWait"/> lives in the TianWen.DAL package, so what it does cannot be pinned from here; only its latency is
/// measured. Only <see cref="PreciseWait"/> is asserted. What a <see cref="Thread.Sleep(int)"/> of 1 ms
/// costs is the process's timer resolution, which anything in the test host may have raised (<c>timeBeginPeriod</c>), so it
/// is printed beside the measurement for the reader and never a bar.
/// </remarks>
[Collection(TimingCollection.Name)]
public class PreciseWaitTests(ITestOutputHelper output)
{
    private const int Waits = 50;

    [Fact]
    [Trait("Category", "Timing")]
    public void AMillisecondsWaitEndsNearTheMillisecondNotAtATimerTick()
    {
        TimingTestGate.SkipUnlessOptedIn();
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the 15.6 ms tick is Windows'; other systems already sleep fine-grained");

        var precise = Average(() => PreciseWait.Sleep(TimeSpan.FromMilliseconds(1)));
        var sleep = Average(() => Thread.Sleep(1));
        output.WriteLine($"{Waits} waits of 1 ms: PreciseWait {precise:F2} ms each, Thread.Sleep {sleep:F2} ms each (for contrast, not asserted)");

        precise.ShouldBeGreaterThanOrEqualTo(0.9, "it waits at least what was asked");
        precise.ShouldBeLessThan(4, "it ends near the millisecond, not at a timer tick");
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void NoWaitReturnsAtOnce()
    {
        // A wall-clock bar too (50 ms for three calls that must not wait), so it is opt-in with the others.
        TimingTestGate.SkipUnlessOptedIn();

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
