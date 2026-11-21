using System;
using System.Diagnostics;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The one thing about <see cref="TestCostProbeAttribute"/> another program depends on: the line it writes, which
/// <c>tools/test-cost-report.py</c> splits into seven fields by position, or ten when peaks are asked for (#2345).
/// And the one rule of a peak: it only rises.
/// </summary>
public class TestCostProbeTests
{
    [Fact]
    public void ALineIsTheTestThenTheSixDifferencesInTheOrderTheReportReads()
    {
        var start = new TestCostProbeAttribute.Mark(
            Timestamp: 0, Allocated: 1_000, Gen0: 3, Gen1: 2, Gen2: 1, Paused: TimeSpan.FromMilliseconds(10));
        var end = new TestCostProbeAttribute.Mark(
            Timestamp: Stopwatch.Frequency * 2, Allocated: 5_000, Gen0: 10, Gen1: 4, Gen2: 1, Paused: TimeSpan.FromMilliseconds(35));

        // Test, wall ms, bytes, gen0, gen1, gen2, pause ms: every field a different number, so a swapped pair shows.
        TestCostProbeAttribute.Line("Some.Class.ATest(dpi: 1.5)", start, end)
            .ShouldBe("Some.Class.ATest(dpi: 1.5)\t2000\t4000\t7\t2\t0\t25\n");
    }

    [Fact]
    public void WithPeaksALineHasThreeMoreFieldsTheHeapAtTheStartItsPeakAndTheWorkingSetsPeak()
    {
        var mark = new TestCostProbeAttribute.Mark(Timestamp: 0, Allocated: 0, Gen0: 0, Gen1: 0, Gen2: 0, Paused: TimeSpan.Zero);
        var peaks = new TestCostProbeAttribute.Peaks(heapAtStart: 300, workingSetAtStart: 9_000);
        peaks.Raise(workingSet: 9_500, heap: 700);

        // Three different numbers, so a swapped pair shows; the working set is last.
        TestCostProbeAttribute.Line("A.Test", mark, mark, peaks).ShouldBe("A.Test\t0\t0\t0\t0\t0\t0\t300\t700\t9500\n");
    }

    [Fact]
    public void APeakOnlyRisesAndTheStartIsKept()
    {
        var peaks = new TestCostProbeAttribute.Peaks(heapAtStart: 100, workingSetAtStart: 1_000);

        peaks.Raise(workingSet: 3_000, heap: 500);
        // Lower in both: a peak that followed the last reading would drop here.
        peaks.Raise(workingSet: 2_000, heap: 400);
        peaks.Raise(workingSet: 900, heap: 50);

        (peaks.HeapAtStart, peaks.Heap, peaks.WorkingSet).ShouldBe((100L, 500L, 3_000L));
    }

    [Fact]
    public void TheHeapAndTheWorkingSetRiseApart()
    {
        var peaks = new TestCostProbeAttribute.Peaks(heapAtStart: 100, workingSetAtStart: 1_000);

        // Each reading is the highest yet in one and not in the other.
        peaks.Raise(workingSet: 4_000, heap: 200);
        peaks.Raise(workingSet: 1_500, heap: 800);

        (peaks.Heap, peaks.WorkingSet).ShouldBe((800L, 4_000L));
    }

    [Fact]
    public void AReadingBelowTheStartLeavesTheStartAsThePeak()
    {
        var peaks = new TestCostProbeAttribute.Peaks(heapAtStart: 100, workingSetAtStart: 1_000);

        peaks.Raise(workingSet: 800, heap: 60);

        (peaks.Heap, peaks.WorkingSet).ShouldBe((100L, 1_000L));
    }
}
