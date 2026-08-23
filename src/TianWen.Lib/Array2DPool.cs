using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace TianWen.Lib;

/// <summary>
/// Thread-safe pool for <typeparamref name="T"/>[,] arrays, bucketed by exact (height, width) dimensions.
/// Astronomical imaging uses a small number of distinct sensor resolutions, so exact-match bucketing
/// gives near-100% hit rates without wasting memory on oversized buffers.
/// <para>
/// Responds to memory pressure via a Gen2 GC callback: trims stale entries under moderate pressure,
/// clears all pools under high pressure (>90% memory load).
/// </para>
/// </summary>
public static class Array2DPool<T>
{
    /// <summary>When false, Rent always allocates fresh and Return is a no-op. Prevents cross-test data races in parallel test runs.</summary>
    /// <remarks>
    /// <para><b>Volatile, because it is a process-wide switch flipped from one thread and read from
    /// every other.</b> It used to be a plain auto-property while every counter beside it was a
    /// <see cref="Volatile"/> read, which is the sort of inconsistency that stays harmless only while
    /// nothing depends on it: <c>FakeExternal</c> turns pooling off once at construction and the
    /// benchmarks toggle it around a measurement, so a stale read cost at most one unpooled
    /// allocation. P3 of <c>docs/plans/frame-lifecycle.md</c> makes the pool load-bearing in
    /// production, and a switch with no barrier is the wrong shape to promote -- gap 4 of that
    /// plan.</para>
    /// <para>A <c>volatile</c> field rather than <see cref="Interlocked"/>: this is a
    /// publish-and-observe flag, never a read-modify-write, so ordering is all that was missing.
    /// It stays a field-with-property because C# does not allow <c>volatile</c> on an auto-property's
    /// generated backing store.</para>
    /// </remarks>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    private static volatile bool _enabled = true;

    private static readonly ConcurrentDictionary<long, ConcurrentQueue<PoolEntry>> _buckets = new();

    /// <summary>Number of active pool buckets (distinct array sizes).</summary>
    public static int BucketCount => _buckets.Count;

    /// <summary>Total arrays currently held across all buckets.</summary>
    public static int TotalPooled
    {
        get
        {
            var count = 0;
            foreach (var q in _buckets.Values) count += q.Count;
            return count;
        }
    }

    /// <summary>Pool hit count (reused an existing array).</summary>
    public static long HitCount => Volatile.Read(ref _hits);

    /// <summary>Pool miss count (allocated a new array).</summary>
    public static long MissCount => Volatile.Read(ref _misses);

    /// <summary>Pool return count (arrays returned to pool).</summary>
    public static long ReturnCount => Volatile.Read(ref _returns);

    private static long _hits;
    private static long _misses;
    private static long _returns;

    /// <summary>Maximum arrays to retain per (height, width) bucket.</summary>
    private const int MaxPerBucket = 8; // AHD debayer uses 6 scratch arrays of the same size

    /// <summary>
    /// Ceiling on the TOTAL bytes retained across every bucket. A per-bucket cap alone bounds
    /// nothing when the shapes vary: a five-year mixed archive hit 24 distinct frame sizes, so
    /// 24 x 8 arrays of 36-140 MB could be pinned, and a survey over it ran out of memory MORE
    /// often with pooling on (12 failures against 6) because the pool was holding arrays the GC
    /// would otherwise have reclaimed.
    ///
    /// <para>A ceiling rather than weak references, though both were on the table. Weak refs let
    /// the GC reclaim under pressure, but they also drop the buffer the camera path wants to reuse
    /// on the very next exposure -- the steady-state case the pool exists for -- and they only
    /// react once a collection runs. A byte budget keeps that hot case at a 100 % hit rate (one
    /// sensor shape fits comfortably), bounds what we hold whatever the workload, and does so
    /// deterministically. That matters because TianWen does NOT own the box: an enhance step
    /// shells out to <c>rc-astro</c>, which wants GPU and host memory of its own while a stack is
    /// still resident.</para>
    ///
    /// <para>256 MiB holds ~7 frames at 3008^2 float32, i.e. the whole working set of a normal
    /// session, while being a rounding error next to an external enhancer's footprint.</para>
    /// </summary>
    private const long MaxRetainedBytes = 256L * 1024 * 1024;

    private static long _retainedBytes;

    /// <summary>Bytes currently retained across all buckets.</summary>
    public static long RetainedBytes => Volatile.Read(ref _retainedBytes);

    /// <summary>Arrays dropped because the pool was already at <see cref="MaxRetainedBytes"/>.</summary>
    public static long BudgetEvictionCount => Volatile.Read(ref _budgetEvictions);

    private static long _budgetEvictions;

    private static long BytesOf(T[,] array) => (long)array.Length * Unsafe.SizeOf<T>();

    /// <summary>Arrays unused for longer than this are trimmed on Gen2 GC under moderate pressure.</summary>
    private const long TrimAfterMs = 30_000;

    private readonly record struct PoolEntry(T[,] Array, long Timestamp);

    static Array2DPool()
    {
        Gen2GcCallback.Register(static () => Trim());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Key(int height, int width) => (long)height << 32 | (long)(uint)width;

    /// <summary>
    /// Rents a <typeparamref name="T"/>[<paramref name="height"/>, <paramref name="width"/>] array.
    /// Returns a pooled array (zero-cleared) if one is available, otherwise allocates a new one.
    /// </summary>
    public static T[,] Rent(int height, int width)
    {
        if (Enabled)
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
    /// Returns a previously rented array to the pool. The array is not cleared until next <see cref="Rent"/>.
    /// Excess arrays beyond <see cref="MaxPerBucket"/> are dropped for GC.
    /// </summary>
    public static void Return(T[,] array)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _returns);

        // Budget first: a heterogeneous workload never fills a single bucket, so the per-bucket
        // cap alone would let the pool grow without bound across shapes.
        var bytes = BytesOf(array);
        if (Volatile.Read(ref _retainedBytes) + bytes > MaxRetainedBytes)
        {
            Interlocked.Increment(ref _budgetEvictions);
            return;
        }

        var key = Key(array.GetLength(0), array.GetLength(1));
        var queue = _buckets.GetOrAdd(key, static _ => new ConcurrentQueue<PoolEntry>());
        if (queue.Count < MaxPerBucket)
        {
            queue.Enqueue(new PoolEntry(array, Environment.TickCount64));
            Interlocked.Add(ref _retainedBytes, bytes);
        }
        // else: let GC collect it; pool is full for this size
    }

    /// <summary>
    /// Trims pooled arrays based on memory pressure. Called from Gen2 GC callback.
    /// High pressure (>90%): clear all pools. Moderate (>70%): trim entries older than 30s.
    /// </summary>
    private static void Trim()
    {
        var info = GC.GetGCMemoryInfo();
        var pressure = info.TotalAvailableMemoryBytes > 0
            ? (double)info.MemoryLoadBytes / info.TotalAvailableMemoryBytes
            : 0;

        if (pressure > 0.9)
        {
            // High pressure: drop everything
            foreach (var queue in _buckets.Values)
            {
                while (queue.TryDequeue(out var dropped)) { Interlocked.Add(ref _retainedBytes, -BytesOf(dropped.Array)); }
            }
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

    /// <summary>
    /// Rents a <typeparamref name="T"/>[,] wrapped in a disposable <see cref="Lease"/> that returns it on dispose.
    /// </summary>
    public static Lease RentScoped(int height, int width) => new Lease(Rent(height, width));

    /// <summary>
    /// Disposable wrapper that returns the array to the pool on dispose.
    /// </summary>
    public readonly struct Lease(T[,] array) : IDisposable
    {
        public T[,] Array
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
        } = array;

        public int Height
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
        } = array.GetLength(0);

        public int Width
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
        } = array.GetLength(1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ReadOnlySpan<T> AsSpan() => MemoryMarshal.CreateReadOnlySpan(ref Array[0, 0], Array.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly Span<T> AsMutableSpan() => MemoryMarshal.CreateSpan(ref Array[0, 0], Array.Length);

        public readonly void Dispose() => Return(Array);
    }

    /// <summary>
    /// Weak-reference + destructor pattern to receive Gen2 GC notifications.
    /// On each Gen2 collection, the finalizer fires and calls the registered callback,
    /// then re-registers for the next collection.
    /// </summary>
    private sealed class Gen2GcCallback
    {
        private readonly Action _callback;

        private Gen2GcCallback(Action callback)
        {
            _callback = callback;
        }

        public static void Register(Action callback)
        {
            new Gen2GcCallback(callback);
        }

        ~Gen2GcCallback()
        {
            _callback();

            if (!Environment.HasShutdownStarted)
            {
                GC.ReRegisterForFinalize(this);
            }
        }
    }
}
