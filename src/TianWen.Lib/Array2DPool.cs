using System;
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
    /// <summary>When false, Rent always allocates fresh and Return is a no-op. Exists so a benchmark can
    /// measure pooled against unpooled; it is NOT a test-isolation switch.</summary>
    /// <remarks>
    /// <para><b>It used to be one, and that was the bug.</b> <c>FakeExternal</c> set it false in its
    /// constructor -- process-wide, never restored -- so the first fake-device test switched pooling
    /// off for every test that followed. <c>FitsPooledReadTests</c> then set it back to true for its
    /// own duration, and the two raced across collections: pooling off mid-test makes <c>Return</c> a
    /// no-op, so <c>Pool_StopsRetainingOnceTheByteBudgetIsReached</c> saw zero evictions and failed
    /// roughly one run in three. The disable was removed once the cross-test data races it was added
    /// for stopped reproducing (three consecutive unit runs at 5,031 passed plus the functional suite
    /// at 338, all green with pooling on everywhere) -- P3 of <c>docs/plans/frame-lifecycle.md</c> made
    /// the pool load-bearing in production, which is the work that fixed the sharing it guarded
    /// against. Disabling it also COST memory rather than saving it: the suite went from 2.28 GB to
    /// ~1.5 GB once pooling was left on, because unpooled runs re-allocate every frame-sized array.</para>
    /// <para><b>Volatile, because it is a process-wide switch flipped from one thread and read from
    /// every other.</b> It used to be a plain auto-property while every counter beside it was a
    /// <see cref="Volatile"/> read, which is the sort of inconsistency that stays harmless only while
    /// nothing depends on it: the only writers left are benchmarks toggling it around a measurement,
    /// so a stale read costs at most one unpooled allocation. P3 of
    /// <c>docs/plans/frame-lifecycle.md</c> makes the pool load-bearing in production, and a switch
    /// with no barrier is the wrong shape to promote -- gap 4 of that plan.</para>
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

    /// <summary>The one pool every caller shares; its policy is <see cref="Array2DPoolCore{T}"/>.</summary>
    private static readonly Array2DPoolCore<T> Shared = new Array2DPoolCore<T>(MaxRetainedBytes, MaxPerBucket);

    /// <summary>Number of active pool buckets (distinct array sizes).</summary>
    public static int BucketCount => Shared.BucketCount;

    /// <summary>Total arrays currently held across all buckets.</summary>
    public static int TotalPooled => Shared.TotalPooled;

    /// <summary>Pool hit count (reused an existing array).</summary>
    public static long HitCount => Shared.HitCount;

    /// <summary>Pool miss count (allocated a new array).</summary>
    public static long MissCount => Shared.MissCount;

    /// <summary>Pool return count (arrays returned to pool).</summary>
    public static long ReturnCount => Shared.ReturnCount;

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
    ///
    /// <para>A return that would pass the ceiling makes ROOM, evicting the arrays of other shapes
    /// returned longest ago (<see cref="Array2DPoolCore{T}.Return"/>), rather than being refused. The
    /// trim runs only above 70 % memory load, so on a roomy machine a budget one workload filled used
    /// to stay filled and turn the next workload's shape away on every frame, which is per-frame
    /// garbage the pool exists to prevent.</para>
    /// </summary>
    private const long MaxRetainedBytes = 256L * 1024 * 1024;

    /// <summary>Bytes currently retained across all buckets.</summary>
    public static long RetainedBytes => Shared.RetainedBytes;

    /// <summary>
    /// Arrays the <see cref="MaxRetainedBytes"/> budget dropped: an older array evicted to make room, or a
    /// return refused (see <see cref="Array2DPoolCore{T}.BudgetEvictionCount"/>).
    /// </summary>
    public static long BudgetEvictionCount => Shared.BudgetEvictionCount;

    static Array2DPool()
    {
        Gen2GcCallback.Register(static () => Trim());
    }

    /// <summary>
    /// Rents a <typeparamref name="T"/>[<paramref name="height"/>, <paramref name="width"/>] array: a pooled
    /// one if one is available, otherwise a new one. A pooled array holds whatever its last user left in
    /// it, so a caller that needs zeros clears it.
    /// </summary>
    public static T[,] Rent(int height, int width)
    {
        return Shared.Rent(height, width, usePool: Enabled);
    }

    /// <summary>
    /// Returns a previously rented array to the pool, as it is: nothing clears it.
    /// Excess arrays beyond <see cref="MaxPerBucket"/> are dropped for GC.
    /// </summary>
    public static void Return(T[,] array)
    {
        if (Enabled)
        {
            Shared.Return(array);
        }
    }

    /// <summary>
    /// Drops every pooled array, as a high-pressure <see cref="Trim"/> does. For a test that measures a
    /// path's OWN allocations: the trim never runs below 70 % memory load, so on a roomy machine (a CI
    /// runner) the byte budget can be full of arrays earlier work returned, which refuses the warm-up's
    /// returns and makes the measured call allocate. Safe at any time, since a concurrent renter only
    /// misses; it is the measurement that needs the pool to itself.
    /// </summary>
    internal static void Clear()
    {
        Shared.Clear();
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

        Shared.Trim(pressure);
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
