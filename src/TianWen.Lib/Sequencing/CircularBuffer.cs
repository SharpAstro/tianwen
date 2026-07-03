using System.Collections.Immutable;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Lock-free fixed-capacity ring of the most recent <paramref name="capacity"/> items
/// (index 0 = oldest). Backed by an <see cref="ImmutableArray{T}"/> that is atomically
/// replaced on <see cref="Add"/> -- the shared-state pattern from CLAUDE.md -- so readers
/// take a torn-free snapshot with a single reference read instead of a lock-and-copy per
/// poll. Append pays the O(capacity) copy, which suits the low-rate producers this backs
/// (one guide sample per guide exposure, one frame metric per sub-exposure) polled by
/// high-rate readers (the GUI render thread reads <c>Session.GuideSamples</c> every frame).
/// </summary>
internal sealed class CircularBuffer<T>(int capacity)
{
    private ImmutableArray<T> _items = [];

    /// <summary>Torn-free snapshot of the current window, oldest first.</summary>
    public ImmutableArray<T> Snapshot => _items;

    public int Count => _items.Length;

    public void Add(T item)
    {
        // CAS loop: each instance has a single logical writer today, but this keeps a
        // second writer from silently losing an append should that ever change.
        ImmutableArray<T> current, next;
        do
        {
            current = _items;
            next = current.Length < capacity ? current.Add(item) : current.RemoveAt(0).Add(item);
        }
        while (ImmutableInterlocked.InterlockedCompareExchange(ref _items, next, current) != current);
    }

    public void Clear() => _items = [];
}
