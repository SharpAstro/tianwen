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

    private readonly record struct PoolEntry(T[,] Array, long Timestamp);

    private readonly ConcurrentDictionary<long, ConcurrentQueue<PoolEntry>> _buckets = new();

    private long _hits;
    private long _misses;
    private long _returns;
    private long _retainedBytes;
    private long _budgetEvictions;

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

    /// <summary>Arrays dropped because the pool was already at its byte budget.</summary>
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

        // Budget first: a heterogeneous workload never fills a single bucket, so the per-bucket
        // cap alone would let the pool grow without bound across shapes.
        var bytes = BytesOf(array);
        if (Volatile.Read(ref _retainedBytes) + bytes > maxRetainedBytes)
        {
            Interlocked.Increment(ref _budgetEvictions);
            return;
        }

        var key = Key(array.GetLength(0), array.GetLength(1));
        var queue = _buckets.GetOrAdd(key, static _ => new ConcurrentQueue<PoolEntry>());
        if (queue.Count < maxPerBucket)
        {
            queue.Enqueue(new PoolEntry(array, Environment.TickCount64));
            Interlocked.Add(ref _retainedBytes, bytes);
        }
        // else: let GC collect it; pool is full for this size
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
