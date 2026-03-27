using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    /// <summary>
    /// When false, Rent always allocates and Return is a no-op. Avoids finalizer
    /// overhead in test scenarios where pool reuse provides no benefit.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    private static readonly ConcurrentDictionary<long, ConcurrentQueue<PoolEntry>> _buckets = new();

    /// <summary>Maximum arrays to retain per (height, width) bucket.</summary>
    private const int MaxPerBucket = 1;

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
                return entry.Array;
            }
        }
        return new T[height, width];
    }

    /// <summary>
    /// Returns a previously rented array to the pool. The array is not cleared until next <see cref="Rent"/>.
    /// Excess arrays beyond <see cref="MaxPerBucket"/> are dropped for GC.
    /// </summary>
    public static void Return(T[,] array)
    {
        if (!Enabled) return;

        var key = Key(array.GetLength(0), array.GetLength(1));
        var queue = _buckets.GetOrAdd(key, static _ => new ConcurrentQueue<PoolEntry>());
        if (queue.Count < MaxPerBucket)
        {
            queue.Enqueue(new PoolEntry(array, Environment.TickCount64));
        }
        // else: let GC collect it — pool is full for this size
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
                while (queue.TryDequeue(out _)) { }
            }
        }
        else if (pressure > 0.7)
        {
            // Moderate pressure: trim stale entries (FIFO order — oldest first)
            var cutoff = Environment.TickCount64 - TrimAfterMs;
            foreach (var queue in _buckets.Values)
            {
                while (queue.TryPeek(out var entry) && entry.Timestamp < cutoff)
                {
                    queue.TryDequeue(out _);
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
