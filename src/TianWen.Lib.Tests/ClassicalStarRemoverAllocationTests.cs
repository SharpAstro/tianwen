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
    /// #2345: a fit solves amplitude and sky some hundreds of times a star, and each solve made a work row of the window's
    /// length anew. Rented, a plate of the test field allocates about half of what it did; the bar sits between the two.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AFitsSolvesRentTheirWorkRows()
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
        perStar.ShouldBeLessThan(BytesAStar, $"a solve's work rows are rented, not made: {perStar:N0} bytes a star");
    }

    /// <summary>
    /// The bar, in bytes a star. Measured 2026-11-21 on this field (921 stars, Release): 1,524,874 and 1,524,125 bytes a
    /// star with the rows rented, 2,803,169 with them made anew. It is a bar on this one cause, not a budget: a plate still
    /// allocates 1.5 MB a star, and the next cut lowers this number with it.
    /// </summary>
    private const long BytesAStar = 2_000_000;
}
