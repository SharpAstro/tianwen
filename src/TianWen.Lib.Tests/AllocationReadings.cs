using System;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a call allocates on the thread that makes it, read so that a one-off from elsewhere does not count (#1331).
/// </summary>
/// <remarks>
/// <para><b>The least of several readings, never one.</b> An allocation the call makes on every pass shows in every
/// reading, so the least is never below that cost and a call that allocates each time can never read zero. A one-off
/// that lands on the measuring thread from elsewhere shows in one pass. A single reading cannot tell the two apart:
/// <c>RaDecCellEnumerationTests.ANineCellWalkAllocatesNothing</c> read 5,336 B once in 87 full-suite runs for a walk
/// with no allocating path, and 0 B in the other 86 and in every run alone.</para>
/// <para><b>The body does the same work on every pass, or the rule does not hold</b> (#2440). A call that allocates on
/// some calls only (a scratch buffer that grows, a pool refilled every Nth call) can read zero in all the passes: the
/// least forgives it exactly as it forgives a one-off. The nine-cell walk makes the same nine probes each pass, so its
/// test is sound. A body that allocates now and then makes enough calls inside ONE pass to include the occasion, and
/// then it is in every reading.</para>
/// <para><b>What the one-off is, is not known.</b> It is not tiered compilation's promotion, which was the first guess:
/// an allocation-free walk called 60,000 times in a fresh process crossed every tier and never read anything but 0 B.
/// So every reading is handed back, and a test writes them to its output: the next one leaves its size and its
/// position in the run's TRX.</para>
/// <para>This is for a bound a few kilobytes would break (zero, or under a kilobyte). A test that asserts "less than
/// half a frame" has room for a one-off and needs none of this. It reads the calling thread only; work a call fans out
/// to the pool is <c>AllocationsCollection</c>'s to measure.</para>
/// </remarks>
internal static class AllocationReadings
{
    /// <summary>How many passes <see cref="Least"/> makes when it is not told.</summary>
    public const int DefaultPasses = 5;

    /// <summary>
    /// Runs <paramref name="body"/> <paramref name="passes"/> times and returns the least it allocated on this thread in
    /// one pass, with every reading in <paramref name="readings"/> and the last pass's result in <paramref name="result"/>.
    /// The caller warms the body first: a first-call cost is in every caller's first pass, never in the least.
    /// </summary>
    public static long Least(Func<int> body, out int result, out long[] readings, int passes = DefaultPasses)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);
        // Made before the first reading, so that the passes themselves allocate nothing.
        readings = new long[passes];
        result = 0;
        var least = long.MaxValue;
        for (var pass = 0; pass < passes; pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            result = body();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            readings[pass] = bytes;
            least = Math.Min(least, bytes);
        }

        return least;
    }
}
