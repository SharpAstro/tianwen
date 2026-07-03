using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Ring semantics of the lock-free <see cref="CircularBuffer{T}"/> (ImmutableArray atomic
/// replacement — readers get torn-free snapshots without a lock).
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

    [Fact]
    public void Snapshot_is_stable_across_later_adds()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);

        var snapshot = buffer.Snapshot;
        buffer.Add(3);
        buffer.Add(4);

        // The snapshot taken earlier is immutable — later writes replace the backing
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
