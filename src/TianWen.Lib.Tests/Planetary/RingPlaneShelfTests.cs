using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A disposed capture ring's planes fill the next ring of its shape (<see cref="RingPlaneShelf"/>, TIA2): never a ring of
/// another shape, never a plane a loader still reads, at most one ring's worth, and given up once the shelf has gone unused.
/// </summary>
public class RingPlaneShelfTests
{
    private const int Capacity = 4;

    private static Image Constant(int n, float value)
    {
        var plane = new float[n, n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                plane[y, x] = value;
            }
        }
        return Image.FromChannel(plane, 1f, 0f);
    }

    private static LiveCameraFrameStream Ring(int n, RingPlaneShelf shelf)
        => new LiveCameraFrameStream(n, n, PlanetaryFrameLayout.Mono, Capacity, hasTimestamps: true, shelf);

    private static void PushMany(LiveCameraFrameStream ring, int frames, float value)
    {
        var source = Constant(ring.Width, value);
        for (var frame = 0; frame < frames; frame++)
        {
            ring.Push(source);
        }
    }

    [Fact]
    public void ADisposedRingsPlanesFillTheNextRingOfItsShape()
    {
        var shelf = new RingPlaneShelf();
        var first = Ring(8, shelf);
        PushMany(first, 20, 0.25f);
        first.PlanesAllocated.ShouldBe(Capacity + 1);
        first.Dispose();
        shelf.Count.ShouldBe(Capacity + 1, "the ring's steady state, every plane back");

        using var second = Ring(8, shelf);
        PushMany(second, Capacity, 0.5f);
        (second.PlanesAllocated, second.PlanesFromShelf).ShouldBe((0, Capacity), "a new capture's ring fills from the shelf, nothing new");
    }

    [Fact]
    public void ARingOfAnotherSizeGetsFreshPlanesAndTheShelfDropsTheOldShape()
    {
        var shelf = new RingPlaneShelf();
        var first = Ring(8, shelf);
        PushMany(first, 10, 0.25f);
        first.Dispose();

        using var resized = Ring(16, shelf);
        shelf.Count.ShouldBe(0, "a new window's ring drops the old window's planes");
        PushMany(resized, Capacity, 0.5f);
        (resized.PlanesAllocated, resized.PlanesFromShelf).ShouldBe((Capacity, 0));
    }

    [Fact]
    public void AnotherLayoutNeverTakesAPlaneOfThisOne()
    {
        var shelf = new RingPlaneShelf();
        shelf.Keep((8, 8, PlanetaryFrameLayout.Mono), bound: 4);
        shelf.Put((8, 8, PlanetaryFrameLayout.Mono), new float[8, 8]);

        shelf.TryTake((8, 8, PlanetaryFrameLayout.SplitCfa), out _).ShouldBeFalse("a colour ring of the same size is another shape");
        shelf.Keep((8, 8, PlanetaryFrameLayout.SplitCfa), bound: 16);
        shelf.Count.ShouldBe(0);
        shelf.Put((8, 8, PlanetaryFrameLayout.Mono), new float[8, 8]);
        shelf.Count.ShouldBe(0, "a plane of the shape it no longer keeps goes to the collector");
    }

    [Fact]
    public async Task AFrameStillLeasedAtTheDisposalKeepsItsPlaneUntilItIsReleased()
    {
        var shelf = new RingPlaneShelf();
        var first = Ring(8, shelf);
        PushMany(first, Capacity, 0.25f);
        var leased = await first.LoadAsync(first.LatestIndex, TestContext.Current.CancellationToken);
        first.Dispose();
        var shelved = shelf.Count;

        using var second = Ring(8, shelf);
        PushMany(second, 3 * Capacity, 0.75f);
        leased.GetChannelSpan(0).ToArray().ShouldAllBe(sample => sample == 0.25f, "nothing the next ring pushed reached a plane still leased");

        leased.Release();
        shelf.Count.ShouldBeGreaterThan(0, "once released, its plane goes to the shelf");
        shelved.ShouldBeLessThan(Capacity + 1, "it was not shelved while leased");
    }

    [Fact]
    public void TheShelfHoldsAtMostOneRingsWorth()
    {
        var shelf = new RingPlaneShelf();
        var ring = Ring(8, shelf);
        PushMany(ring, 50, 0.25f);
        ring.Dispose();
        for (var extra = 0; extra < 10; extra++)
        {
            shelf.Put((8, 8, PlanetaryFrameLayout.Mono), new float[8, 8]);
        }

        shelf.Count.ShouldBe(Capacity + 1, "a ring's steady state, and not one plane more");
    }

    [Fact]
    public void AShelfIsGivenUpOnlyOnceIdle()
    {
        var shelf = new RingPlaneShelf();
        var ring = Ring(8, shelf);
        PushMany(ring, 10, 0.25f);
        ring.Dispose();

        shelf.TrimIfIdle(Environment.TickCount64);
        shelf.Count.ShouldBe(Capacity + 1, "just used: kept for the next capture");
        shelf.TrimIfIdle(Environment.TickCount64 + (long)RingPlaneShelf.IdleBeforeTrim.TotalMilliseconds + 1);
        shelf.Count.ShouldBe(0, "idle past its time: given to the collector");
    }
}
