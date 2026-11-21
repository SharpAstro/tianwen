using System;
using System.Diagnostics;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The one thing about <see cref="TestCostProbeAttribute"/> another program depends on: the line it writes, which
/// <c>tools/test-cost-report.py</c> splits into seven fields by position (#2345).
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
}
