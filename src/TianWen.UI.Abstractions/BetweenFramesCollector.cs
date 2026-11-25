using System;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Takes the collector's pause between two frames rather than inside one.
/// </summary>
/// <remarks>
/// <para>
/// A generation-0 collection stops every thread for a few milliseconds (2 to 5 in the GUI, traced 2026-11-25), and the
/// runtime starts one at the allocation that uses up the generation's budget. The render thread allocates most of what
/// the GUI does, and it does so while it paints, so that allocation is nearly always inside a paint: 511 of the 1,489
/// paints over 8 ms in one traced session had a collection in them. A frame that has just been presented has the rest
/// of its interval to spare, so a collection asked for THERE, once most of the budget is used, costs no frame anything,
/// and the budget it hands back is one the next paints do not run out of.
/// </para>
/// <para>
/// The budget is the runtime's own: about 6 MB between collections on this machine's .NET 10, measured, and of the
/// settings that claim to raise it only the <c>DOTNET_GCgen0size</c> environment variable does, which a program cannot
/// set for itself. So the threshold is a fixed share of that figure. Where the runtime's budget is smaller it collects
/// first, this never has anything to do, and the GUI behaves as it did before.
/// </para>
/// </remarks>
/// <param name="thresholdBytes">How much is allocated, process-wide, since the last collection before one is asked
/// for.</param>
public sealed class BetweenFramesCollector(long thresholdBytes = BetweenFramesCollector.DefaultThresholdBytes)
{
    /// <summary>Two thirds of the 6 MB the runtime allows generation 0 between collections.</summary>
    public const long DefaultThresholdBytes = 4L * 1024 * 1024;

    private long _allocatedAtLastCollection = GC.GetTotalAllocatedBytes();
    private int _collectionsAtLastLook = GC.CollectionCount(0);

    /// <summary>How many collections this asked for.</summary>
    public int Collections { get; private set; }

    /// <summary>
    /// Called once a frame has been presented. Collects generation 0 when <see cref="AllocatedSinceCollection"/> has
    /// reached the threshold, and says whether it did.
    /// </summary>
    public bool AfterFrame()
    {
        var collections = GC.CollectionCount(0);
        var allocated = GC.GetTotalAllocatedBytes();
        if (collections != _collectionsAtLastLook)
        {
            // The runtime collected since the last frame: its budget starts again from about here.
            _collectionsAtLastLook = collections;
            _allocatedAtLastCollection = allocated;
            return false;
        }

        if (allocated - _allocatedAtLastCollection < thresholdBytes)
        {
            return false;
        }

        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        _collectionsAtLastLook = GC.CollectionCount(0);
        _allocatedAtLastCollection = GC.GetTotalAllocatedBytes();
        Collections++;
        return true;
    }

    /// <summary>What has been allocated, process-wide, since the last collection this saw.</summary>
    public long AllocatedSinceCollection => GC.GetTotalAllocatedBytes() - _allocatedAtLastCollection;
}
