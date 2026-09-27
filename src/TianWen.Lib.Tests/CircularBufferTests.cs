using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Ring semantics of the lock-free <see cref="CircularBuffer{T}"/> (ImmutableArray atomic
/// replacement: readers get torn-free snapshots without a lock).
/// </summary>
public class CircularBufferTests
{
    [Fact]
    public void Keeps_the_most_recent_items_oldest_first()
    {
        var buffer = new CircularBuffer<int>(3);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(i);
        }

        buffer.Count.ShouldBe(3);
        buffer.Snapshot.ShouldBe([3, 4, 5]);
    }

    /// <summary>
    /// The window and how many were ever added are read together, and the count never goes back, Clear included: a node
    /// sends a client the guide samples after the number it holds (P5b part 7), which a count that reset would repeat.
    /// </summary>
    [Fact]
    public void The_window_counts_every_item_ever_added_and_Clear_keeps_the_count()
    {
        var buffer = new CircularBuffer<int>(3);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(i);
        }

        var (items, appended) = buffer.Window;
        items.ShouldBe([3, 4, 5]);
        appended.ShouldBe(5L, "item i of the window is the (appended - count + i)th added");

        buffer.Clear();
        buffer.Window.Items.ShouldBeEmpty();
        buffer.Window.Appended.ShouldBe(5L);
        buffer.Add(6);
        buffer.Window.Appended.ShouldBe(6L);
    }

    [Fact]
    public void Snapshot_is_stable_across_later_adds()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);

        var snapshot = buffer.Snapshot;
        buffer.Add(3);
        buffer.Add(4);

        // The snapshot taken earlier is immutable: later writes replace the backing
        // array instead of mutating it under the reader.
        snapshot.ShouldBe([1, 2]);
        buffer.Snapshot.ShouldBe([2, 3, 4]);
    }

    [Fact]
    public void Clear_empties_and_the_ring_refills()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4);

        buffer.Clear();
        buffer.Count.ShouldBe(0);
        buffer.Snapshot.ShouldBeEmpty();

        buffer.Add(7);
        buffer.Snapshot.ShouldBe([7]);
    }
}
