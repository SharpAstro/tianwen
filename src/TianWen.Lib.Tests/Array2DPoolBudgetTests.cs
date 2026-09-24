using System;
using System.Collections.Generic;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What the pool does once its byte budget is full. The shared pool trims only above 70 % memory load,
/// so on a roomy machine a budget one workload filled stays filled, and a new workload's shape used to
/// be turned away on every return: a live capture started after a session's masters paid a new plane
/// per frame, the garbage the frame-path work removed. Found on #759's CI, where the frame-path
/// allocation tests met a budget that earlier tests had filled.
/// </summary>
/// <remarks>
/// Driven on an <see cref="Array2DPoolCore{T}"/> of its own with a 1 MiB budget, never the shared pool:
/// that pool's Gen2 trim empties it above 90 % memory load, which on a loaded box emptied a 256 MiB fill
/// halfway. Every array here is under the large-object threshold, and nothing but these tests can see
/// the instance.
/// </remarks>
public class Array2DPoolBudgetTests(ITestOutputHelper output)
{
    private const long Budget = 1L << 20;
    private const int MaxPerBucket = 8;

    // 80 x 256 floats is 81,920 bytes, more than the 7,168 the fill below leaves free.
    private const int Height = 80, Width = 256;

    [Fact]
    public void AFullBudgetMakesRoomForTheShapeBeingReturned()
    {
        var pool = new Array2DPoolCore<float>(Budget, MaxPerBucket);
        var idle = FillWithIdleShapes(pool);
        var plane = (long)Height * Width * sizeof(float);
        (Budget - pool.RetainedBytes).ShouldBeLessThan(plane, "premise: the budget has no room for the new shape");

        // The new workload rents and returns its shape every frame. Its first frame misses whatever the
        // policy; every frame after it is what this is about.
        pool.Return(pool.Rent(Height, Width));

        const int frames = 10;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < frames; i++)
        {
            pool.Return(pool.Rent(Height, Width));
        }
        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;

        output.WriteLine($"{Width}x{Height} after a full budget: {perFrame:N0} bytes per frame, against a {plane:N0}-byte plane");
        perFrame.ShouldBeLessThan(plane / 2, "a full budget must make room for the shape in use, not turn it away every frame");
        pool.RetainedBytes.ShouldBeLessThanOrEqualTo(Budget, "making room must not pass the ceiling");

        // The room came from the arrays returned longest ago, and the latest idle array is still there.
        var newest = idle[^1];
        pool.Rent(newest.GetLength(0), newest.GetLength(1)).ShouldBeSameAs(newest, "the most recently returned idle array is kept");
        var oldest = idle[0];
        pool.Rent(oldest.GetLength(0), oldest.GetLength(1)).ShouldNotBeSameAs(oldest, "the array returned longest ago is the one evicted");
    }

    [Fact]
    public void AnArrayLargerThanTheWholeBudgetIsRefusedAndEvictsNothing()
    {
        var pool = new Array2DPoolCore<float>(Budget, MaxPerBucket);
        FillWithIdleShapes(pool);
        var retained = pool.RetainedBytes;
        var evictions = pool.BudgetEvictionCount;

        pool.Return(new float[1100, 256]); // 1,126,400 bytes: it could never fit

        pool.BudgetEvictionCount.ShouldBe(evictions + 1, "the oversize return itself is the one refusal");
        pool.RetainedBytes.ShouldBe(retained, "nothing may be evicted for an array that cannot fit anyway");
    }

    [Fact]
    public void ABudgetFullOfTheReturningShapeRefusesItRatherThanEvictingItsOwn()
    {
        // Three 300 KB planes of one shape: a fourth would pass the budget, and the only arrays there
        // are to evict are its own kind, so evicting one to keep another would buy nothing.
        var pool = new Array2DPoolCore<float>(Budget, MaxPerBucket);
        var kept = new List<float[,]>();
        for (var i = 0; i < 3; i++)
        {
            var array = new float[300, 256];
            pool.Return(array);
            kept.Add(array);
        }
        var retained = pool.RetainedBytes;
        var evictions = pool.BudgetEvictionCount;

        pool.Return(new float[300, 256]);

        pool.BudgetEvictionCount.ShouldBe(evictions + 1);
        pool.RetainedBytes.ShouldBe(retained);
        pool.TotalPooled.ShouldBe(3, "the three already held stay");
    }

    /// <summary>
    /// One workload's shapes, returned oldest first until the budget is full, then left idle; all distinct,
    /// so no bucket cap intervenes. 18 of them, 49,152 to 66,560 bytes, leave 7,168 of the 1 MiB free.
    /// </summary>
    private static List<float[,]> FillWithIdleShapes(Array2DPoolCore<float> pool)
    {
        var returned = new List<float[,]>();
        for (var i = 0; pool.RetainedBytes + (long)(48 + i) * 256 * sizeof(float) <= Budget; i++)
        {
            var array = new float[48 + i, 256];
            pool.Return(array);
            returned.Add(array);
        }
        return returned;
    }
}
