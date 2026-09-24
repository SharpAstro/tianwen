using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TianWen.Lib;

/// <summary>
/// The policy behind <see cref="Array2DPool{T}"/>: arrays bucketed by exact (height, width), at most
/// <c>maxPerBucket</c> of one shape, and a byte budget across every bucket.
/// </summary>
/// <remarks>
/// An instance, so the policy can be driven with a budget of its own, away from the process-wide pool
/// and its Gen2 trim. That trim empties the shared pool whenever memory load passes 90 %, so a test
/// filling the SHARED budget measured the machine it ran on as much as the pool: on a loaded dev box
/// the trim emptied the pool mid-fill, on a roomy CI runner it never ran at all.
/// </remarks>
internal sealed class Array2DPoolCore<T>(long maxRetainedBytes, int maxPerBucket)
{
    /// <summary>Arrays unused for longer than this are trimmed under moderate pressure.</summary>
    private const long TrimAfterMs = 30_000;

    // Timestamp is for the trim's age test, in milliseconds; Sequence orders eviction, since many
    // returns share a millisecond and the victim must be the array returned longest ago.
    private readonly record struct PoolEntry(T[,] Array, long Timestamp, long Sequence);

    private readonly ConcurrentDictionary<long, ConcurrentQueue<PoolEntry>> _buckets = new();

    private long _hits;
    private long _misses;
    private long _returns;
    private long _retainedBytes;
    private long _budgetEvictions;
    private long _sequence;

    /// <summary>Number of active pool buckets (distinct array sizes).</summary>
    public int BucketCount => _buckets.Count;

    /// <summary>Total arrays currently held across all buckets.</summary>
    public int TotalPooled
    {
        get
        {
            var count = 0;
            foreach (var q in _buckets.Values) count += q.Count;
            return count;
        }
    }

    /// <summary>Pool hit count (reused an existing array).</summary>
    public long HitCount => Volatile.Read(ref _hits);

    /// <summary>Pool miss count (allocated a new array).</summary>
    public long MissCount => Volatile.Read(ref _misses);

    /// <summary>Pool return count (arrays returned to pool).</summary>
    public long ReturnCount => Volatile.Read(ref _returns);

    /// <summary>Bytes currently retained across all buckets.</summary>
    public long RetainedBytes => Volatile.Read(ref _retainedBytes);

    /// <summary>
    /// Arrays the byte budget dropped: an older array evicted to make room for a return, or a return
    /// refused because it could never fit, or because everything held is its own shape.
    /// </summary>
    public long BudgetEvictionCount => Volatile.Read(ref _budgetEvictions);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Key(int height, int width) => (long)height << 32 | (long)(uint)width;

    private static long BytesOf(T[,] array) => (long)array.Length * Unsafe.SizeOf<T>();

    /// <summary>
    /// A pooled array of the shape when one is held (and <paramref name="usePool"/>), else a new one. A
    /// pooled array holds whatever its last user left in it: a caller that needs zeros clears it.
    /// </summary>
    public T[,] Rent(int height, int width, bool usePool = true)
    {
        if (usePool)
        {
            var key = Key(height, width);
            if (_buckets.TryGetValue(key, out var queue) && queue.TryDequeue(out var entry))
            {
                Interlocked.Increment(ref _hits);
                Interlocked.Add(ref _retainedBytes, -BytesOf(entry.Array));
                return entry.Array;
            }
        }
        Interlocked.Increment(ref _misses);
        return new T[height, width];
    }

    /// <summary>
    /// Hands an array back for the next <see cref="Rent"/> of its shape, as it is: nothing clears it.
    /// Excess arrays beyond <c>maxPerBucket</c> are dropped for GC.
    /// </summary>
    public void Return(T[,] array)
    {
        Interlocked.Increment(ref _returns);

        // An array larger than the whole budget can never fit: refuse it, and evict nothing for it,
        // since everything evicted would be dropped for no gain.
        var bytes = BytesOf(array);
        if (bytes > maxRetainedBytes)
        {
            Interlocked.Increment(ref _budgetEvictions);
            return;
        }

        var key = Key(array.GetLength(0), array.GetLength(1));
        var queue = _buckets.GetOrAdd(key, static _ => new ConcurrentQueue<PoolEntry>());
        if (queue.Count >= maxPerBucket)
        {
            return; // let GC collect it; pool is full for this size
        }

        // The budget bounds the pool across shapes, which the per-bucket cap alone never would: a
        // heterogeneous workload fills no single bucket. A return that would pass it makes ROOM,
        // evicting the arrays of other shapes returned longest ago, rather than being refused. The
        // shared pool trims only above 70 % memory load, so on a roomy machine a budget one workload
        // filled used to stay filled and turn the next workload's shape away on every frame (found
        // on #759's CI). Oldest first keeps the shape in use, since each of its rents takes an entry
        // out and each return enqueues a newer one.
        while (Volatile.Read(ref _retainedBytes) + bytes > maxRetainedBytes)
        {
            if (!TryEvictOldest(except: queue))
            {
                // Everything held is this shape's own, and evicting one to keep another buys nothing.
                Interlocked.Increment(ref _budgetEvictions);
                return;
            }
        }

        queue.Enqueue(new PoolEntry(array, Environment.TickCount64, Interlocked.Increment(ref _sequence)));
        Interlocked.Add(ref _retainedBytes, bytes);
    }

    /// <summary>
    /// Evicts the entry returned longest ago from any bucket but <paramref name="except"/>, and says
    /// whether there was one. Losing the chosen entry to a concurrent rent still counts as progress,
    /// since that rent lowered the retained total itself.
    /// </summary>
    private bool TryEvictOldest(ConcurrentQueue<PoolEntry> except)
    {
        ConcurrentQueue<PoolEntry>? victim = null;
        var oldest = long.MaxValue;
        foreach (var (_, queue) in _buckets)
        {
            if (!ReferenceEquals(queue, except) && queue.TryPeek(out var head) && head.Sequence < oldest)
            {
                oldest = head.Sequence;
                victim = queue;
            }
        }

        if (victim is null)
        {
            return false;
        }

        if (victim.TryDequeue(out var evicted))
        {
            Interlocked.Add(ref _retainedBytes, -BytesOf(evicted.Array));
            Interlocked.Increment(ref _budgetEvictions);
        }
        return true;
    }

    /// <summary>Drops every pooled array.</summary>
    public void Clear()
    {
        foreach (var queue in _buckets.Values)
        {
            while (queue.TryDequeue(out var dropped)) { Interlocked.Add(ref _retainedBytes, -BytesOf(dropped.Array)); }
        }
    }

    /// <summary>
    /// Trims by memory pressure, a fraction of the memory available: above 90 % it drops everything,
    /// above 70 % the entries unused for 30 s, oldest first.
    /// </summary>
    public void Trim(double pressure)
    {
        if (pressure > 0.9)
        {
            // High pressure: drop everything
            Clear();
        }
        else if (pressure > 0.7)
        {
            // Moderate pressure: trim stale entries (FIFO order, oldest first)
            var cutoff = Environment.TickCount64 - TrimAfterMs;
            foreach (var queue in _buckets.Values)
            {
                while (queue.TryPeek(out var entry) && entry.Timestamp < cutoff)
                {
                    if (queue.TryDequeue(out var dropped))
                    {
                        Interlocked.Add(ref _retainedBytes, -BytesOf(dropped.Array));
                    }
                }
            }
        }
    }
}
