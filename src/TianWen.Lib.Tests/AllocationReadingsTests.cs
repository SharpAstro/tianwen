using System;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="AllocationReadings.Least"/> (#1331): the least of several readings forgives a one-off and never forgives an
/// allocation the call makes every time, which is the whole of what a zero-allocation test needs from it.
/// </summary>
public class AllocationReadingsTests
{
    private static byte[]? _kept;

    [Fact]
    public void AnAllocationInOnePassOnlyIsNotTheCallsOwn()
    {
        // The one-off, played: the third pass allocates five kilobytes, as the runtime once did on a walk that allocates
        // nothing. Kept in a field so the allocation cannot be optimised away.
        var pass = 0;
        var least = AllocationReadings.Least(() =>
        {
            if (++pass == 3)
            {
                _kept = new byte[5_000];
            }
            return pass;
        }, out var result, out var readings);

        least.ShouldBe(0L);
        result.ShouldBe(AllocationReadings.DefaultPasses, "the last pass's result is handed back");
        readings.Length.ShouldBe(AllocationReadings.DefaultPasses);
        readings[2].ShouldBeGreaterThanOrEqualTo(5_000L, "and the one-off is in the readings, for the test's output");
        (readings[0], readings[1], readings[3], readings[4]).ShouldBe((0L, 0L, 0L, 0L));
    }

    [Fact]
    public void AnAllocationEveryPassMakesIsSeenHoweverManyPasses()
    {
        var least = AllocationReadings.Least(() =>
        {
            _kept = new byte[64];
            return _kept.Length;
        }, out _, out var readings, passes: 9);

        least.ShouldBeGreaterThanOrEqualTo(64L, "a call that allocates can never read zero");
        readings.ShouldAllBe(bytes => bytes >= 64L);
    }

    [Fact]
    public void LessThanOnePassIsRefused()
        => Should.Throw<ArgumentOutOfRangeException>(() => AllocationReadings.Least(static () => 0, out _, out _, passes: 0));
}
