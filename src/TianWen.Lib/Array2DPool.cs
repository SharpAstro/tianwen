using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TianWen.Lib;

/// <summary>
/// Thread-safe pool for <typeparamref name="T"/>[,] arrays, bucketed by exact (height, width) dimensions.
/// Astronomical imaging uses a small number of distinct sensor resolutions, so exact-match bucketing
/// gives near-100% hit rates without wasting memory on oversized buffers.
/// </summary>
public static class Array2DPool<T>
{
    private static readonly ConcurrentDictionary<long, ConcurrentBag<T[,]>> _buckets = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Key(int height, int width) => (long)height << 32 | (long)(uint)width;

    /// <summary>
    /// Rents a <typeparamref name="T"/>[<paramref name="height"/>, <paramref name="width"/>] array.
    /// Returns a pooled array (zero-cleared) if one is available, otherwise allocates a new one.
    /// </summary>
    public static T[,] Rent(int height, int width)
    {
        var key = Key(height, width);
        if (_buckets.TryGetValue(key, out var bag) && bag.TryTake(out var array))
        {
            MemoryMarshal.CreateSpan(ref array[0, 0], array.Length).Clear();
            return array;
        }
        return new T[height, width];
    }

    /// <summary>
    /// Returns a previously rented array to the pool. The array is not cleared until next <see cref="Rent"/>.
    /// </summary>
    public static void Return(T[,] array)
    {
        var key = Key(array.GetLength(0), array.GetLength(1));
        var bag = _buckets.GetOrAdd(key, static _ => new ConcurrentBag<T[,]>());
        bag.Add(array);
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
}
