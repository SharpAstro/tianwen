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
        // Three of the other four, not all four (#2440): the runtime's own one-off, the 5,336 B that began #1331, can
        // land in any pass of this test too, and a single reading asserted to be zero is the thing the helper exists to
        // avoid. Three still show that each pass has a reading of its own: a sum carried from pass to pass leaves two.
        Array.FindAll(readings, static bytes => bytes == 0L).Length
            .ShouldBeGreaterThanOrEqualTo(3, "the passes that did not allocate read zero, less one the runtime may take");
    }

    [Fact]
    public void AnAllocationEveryPassMakesIsSeenHoweverManyPasses()
    {
        var least = AllocationReadings.Least(() =>
        {
            _kept = new byte[64];
            return _kept.Length;
        }, out _, out var readings, passes: 9);

        least.ShouldBeGreaterThanOrEqualTo(64L, "a call that allocates on every pass can never read zero");
        readings.ShouldAllBe(bytes => bytes >= 64L);
    }

    [Fact]
    public void LessThanOnePassIsRefused()
        => Should.Throw<ArgumentOutOfRangeException>(() => AllocationReadings.Least(static () => 0, out _, out _, passes: 0));
}
