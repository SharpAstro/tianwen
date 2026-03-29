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
    private volatile bool _released;

    /// <summary>The backing pixel data (row-major [Height, Width]).</summary>
    /// <exception cref="ObjectDisposedException">Thrown if accessed after all refs released.</exception>
    public float[,] Data => !_released ? data : throw new ObjectDisposedException(nameof(ChannelBuffer));

    /// <summary>Image height (rows).</summary>
    public int Height => data.GetLength(0);

    /// <summary>Image width (columns).</summary>
    public int Width => data.GetLength(1);

    /// <summary>Whether all references have been released.</summary>
    public bool IsReleased => _released;

    /// <summary>Current reference count (for diagnostics).</summary>
    public int RefCount => Volatile.Read(ref _refCount);

    /// <summary>
    /// Increments the reference count. Call before handing the buffer to an additional consumer.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if already fully released.</exception>
    public ChannelBuffer AddRef()
    {
        if (_released)
        {
            throw new ObjectDisposedException(nameof(ChannelBuffer), "Cannot AddRef on a released ChannelBuffer");
        }

        Interlocked.Increment(ref _refCount);
        return this;
    }

    /// <summary>
    /// Decrements the reference count. When it reaches zero, fires the <c>onRelease</c>
    /// callback so the camera can recycle the backing <c>float[,]</c>.
    /// Idempotent after reaching zero — extra calls are no-ops.
    /// </summary>
    public void Release()
    {
        if (_released) return;

        var count = Interlocked.Decrement(ref _refCount);
        if (count == 0)
        {
            _released = true;
            onRelease?.Invoke(data);
        }
        else if (count < 0)
        {
            // Over-release — clamp back to 0 and ignore
            Interlocked.Increment(ref _refCount);
        }
    }
}
