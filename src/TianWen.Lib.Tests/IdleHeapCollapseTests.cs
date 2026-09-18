using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Shell.Thumbnails;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// When the thumbnail handler gives its heap back, which is a question about COUNTING rather than
/// about memory: the collapse itself is one runtime call, and what can be wrong is WHEN it is made.
/// <para>
/// Both failure directions are silent. Collapse once per request and every thumbnail in a parallel
/// folder extraction pays for a blocking compacting collection of the others' heaps -- the pictures
/// are identical, the extraction is just slow. Collapse never and the surrogate keeps gigabytes for
/// hours, which is issue #294 itself. So these drive the tick by hand (the timer is constructed inert
/// with <see cref="Timeout.Infinite"/>) and count the collapses.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class IdleHeapCollapseTests
{
    /// <summary>A collapse policy whose "give the heap back" is a counter, driven tick by tick.</summary>
    private sealed class Probe : IDisposable
    {
        private int _collapses;

        internal int Collapses => Volatile.Read(ref _collapses);

        internal IdleHeapCollapse Policy { get; }

        internal Probe() => Policy = new IdleHeapCollapse(() => Interlocked.Increment(ref _collapses), Timeout.Infinite);

        public void Dispose() => Policy.Dispose();
    }

    [Fact]
    public void ABurstCostsOneCollapse_AfterItHasStopped_NotOnePerRequest()
    {
        using var probe = new Probe();

        for (var i = 0; i < 40; i++)
        {
            probe.Policy.RecordRender();
        }

        // The first tick after a burst cannot tell "just finished" from "still running", and the
        // expensive answer is the wrong one to guess at, so it defers.
        probe.Policy.Tick();
        probe.Collapses.ShouldBe(0, "a tick that saw the counter move is a burst still in flight");

        probe.Policy.Tick();
        probe.Collapses.ShouldBe(1);

        // And the rule this is all built on: having already collapsed at this count, there is nothing
        // to do, however many times it is asked.
        probe.Policy.Tick();
        probe.Policy.Tick();
        probe.Collapses.ShouldBe(1, "the counter has not moved, so there is no new garbage to return");
    }

    [Fact]
    public void ARequestThatArrivesDuringTheQuietWindowDefersTheCollapse()
    {
        using var probe = new Probe();
        probe.Policy.RecordRender();
        probe.Policy.Tick();

        // Explorer scrolling a folder: the burst is not over, and collapsing here would suspend the
        // threads still decoding to throw away the heap the next file is about to need.
        probe.Policy.RecordRender();
        probe.Policy.Tick();
        probe.Collapses.ShouldBe(0);

        probe.Policy.Tick();
        probe.Collapses.ShouldBe(1);
    }

    [Fact]
    public void AnIdleProcessDoesNoWorkAtAll()
    {
        using var probe = new Probe();

        probe.Policy.IsArmed.ShouldBeFalse("loading the DLL must not start a timer");
        probe.Policy.Tick();
        probe.Policy.Tick();
        probe.Collapses.ShouldBe(0);
    }

    [Fact]
    public void TheTimerStopsItselfOnceTheHeapIsBack_AndTheNextRequestStartsItAgain()
    {
        using var probe = new Probe();
        probe.Policy.RecordRender();
        probe.Policy.IsArmed.ShouldBeTrue();

        probe.Policy.Tick();
        probe.Policy.Tick();
        probe.Collapses.ShouldBe(1);

        // The surrogate in #294 lived thirteen hours after its last thumbnail. A timer still ticking
        // through that is thousands of wakeups to read the same number, so the tick that collapses is
        // also the one that stands the timer down.
        probe.Policy.IsArmed.ShouldBeFalse();

        probe.Policy.RecordRender();
        probe.Policy.IsArmed.ShouldBeTrue("a second burst has to be able to wake it");

        probe.Policy.Tick();
        probe.Policy.Tick();
        probe.Collapses.ShouldBe(2);
    }

    [Fact]
    public void EveryRenderIsCountedEvenWhenTheyRaceEachOther()
    {
        // The shell extracts a folder in parallel, so the counter is written from several threads at
        // once; a lost increment is a burst whose tail nothing collapses.
        using var probe = new Probe();

        Parallel.For(0, 500, _ => probe.Policy.RecordRender());

        probe.Policy.Renders.ShouldBe(500);
        probe.Policy.Tick();
        probe.Policy.Tick();
        probe.Collapses.ShouldBe(1);
    }

    [Fact]
    public void ACollapseThatThrowsDoesNotTakeTheProcessWithIt()
    {
        // The tick runs on a pool thread with no caller, so an escaping exception is an unhandled one:
        // the surrogate dies and Explorer loses every thumbnail it was drawing, to fail at the one job
        // that was never load-bearing in the first place.
        var attempts = 0;
        using var policy = new IdleHeapCollapse(
            () => { attempts++; throw new InvalidOperationException("the collector said no"); },
            Timeout.Infinite);

        policy.RecordRender();
        policy.Tick();
        Should.NotThrow(() => policy.Tick());
        attempts.ShouldBe(1);

        // And it must not wedge: a later burst still gets its attempt.
        policy.RecordRender();
        policy.Tick();
        Should.NotThrow(() => policy.Tick());
        attempts.ShouldBe(2);
    }

    [Fact]
    public void TheRealCollapseRuns()
    {
        // One live call against the runtime, because every test above swaps the collection for a
        // counter and would pass just as happily if CollapseHeap threw or asked for a mode this
        // target does not have.
        using var policy = new IdleHeapCollapse(IdleHeapCollapse.CollapseHeap, Timeout.Infinite);

        var before = GC.CollectionCount(GC.MaxGeneration);
        policy.RecordRender();
        policy.Tick();
        policy.Tick();

        GC.CollectionCount(GC.MaxGeneration)
            .ShouldBeGreaterThan(before, "the collapse has to reach the collector, not just the counter");
    }
}
