using System;
using System.Threading;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Ref-counted owner of a <c>float[,]</c> image channel buffer.
/// When the last holder calls <see cref="Release"/>, the <paramref name="onRelease"/>
/// callback fires (typically returning the buffer to the camera for reuse).
/// <para>
/// Born with refCount=1 (creator holds it). Call <see cref="AddRef"/> before handing
/// to additional consumers. Each consumer calls <see cref="Release"/> when done.
/// </para>
/// </summary>
internal sealed class ChannelBuffer(float[,] data, Action<float[,]>? onRelease = null)
{
    private int _refCount = 1;

    /// <summary>The backing pixel data (row-major [Height, Width]).</summary>
    public float[,] Data => data;

    /// <summary>Image height (rows).</summary>
    public int Height => data.GetLength(0);

    /// <summary>Image width (columns).</summary>
    public int Width => data.GetLength(1);

    /// <summary>Current reference count (for diagnostics).</summary>
    public int RefCount => Volatile.Read(ref _refCount);

    /// <summary>
    /// Increments the reference count. Call before handing the buffer to an additional consumer.
    /// </summary>
    public ChannelBuffer AddRef()
    {
        var count = Interlocked.Increment(ref _refCount);
        if (count <= 1)
        {
            // Was already released — this is a bug
            Interlocked.Decrement(ref _refCount);
            throw new ObjectDisposedException(nameof(ChannelBuffer), "Cannot AddRef on a released ChannelBuffer");
        }
        return this;
    }

    /// <summary>
    /// Decrements the reference count. When it reaches zero, fires the <c>onRelease</c>
    /// callback so the camera can recycle the backing <c>float[,]</c>.
    /// Safe to call multiple times — only the transition to zero triggers release.
    /// </summary>
    public void Release()
    {
        var count = Interlocked.Decrement(ref _refCount);
        if (count == 0)
        {
            onRelease?.Invoke(data);
        }
    }
}
