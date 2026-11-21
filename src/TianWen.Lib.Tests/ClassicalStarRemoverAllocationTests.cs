using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a plate's fits allocate, counted over every thread with nothing running beside it (<see cref="AllocationsCollection"/>):
/// the fits run on the pool's threads, where a per-thread count sees nothing.
/// </summary>
[Collection("Allocations")]
public class ClassicalStarRemoverAllocationTests(ITestOutputHelper output)
{
    /// <summary>
    /// #2345, two causes under one bar. A fit solves amplitude and sky some hundreds of times a star, and each solve made a
    /// work row of the window's length anew; and each window was grown as four lists and then copied, though its size is
    /// known before a pixel is read. With the rows rented and the window made at its size a plate of the test field
    /// allocates 39 percent of what it did, and the bar sits under what either cause alone brings back.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AFitRentsItsWorkRowsAndMakesItsWindowAtItsSize()
    {
        var ct = TestContext.Current.CancellationToken;
        var field = ClassicalStarRemoverTests.MonoField();
        // Once first: the pool's arrays, the compiled code and the profile's tables are made on the first plate.
        (await ClassicalStarRemover.BuildAsync(field, cancellationToken: ct)).Stars.Length.ShouldBeGreaterThan(500, "premise: a field of stars to fit");

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var plate = await ClassicalStarRemover.BuildAsync(field, cancellationToken: ct);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        var perStar = allocated / plate.Stars.Length;
        output.WriteLine($"{allocated:N0} bytes for {plate.Stars.Length} stars: {perStar:N0} a star");
        perStar.ShouldBeLessThan(BytesAStar, $"a solve's rows are rented and a window is made at its size: {perStar:N0} bytes a star");
    }

    /// <summary>
    /// The bar, in bytes a star. Measured 2026-11-21 on this field (921 stars, Release): 1,087,603 as it is now; 1,524,874
    /// with the windows grown as lists again; 2,803,274 with the solves' rows made anew as well. It is a bar on these two
    /// causes, not a budget: a plate still allocates 1.1 MB a star, and the next cut lowers this number with it.
    /// </summary>
    private const long BytesAStar = 1_300_000;
}
