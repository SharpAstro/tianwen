using System;
using System.Buffers;
using System.Threading;

namespace TianWen.Lib.Devices.Alpaca;

/// <summary>
/// An ImageBytes response body read into a RENTED buffer (<see cref="AlpacaClient.GetImageArrayBytesAsync"/>).
/// <see cref="Span"/> is the payload until <see cref="Dispose"/> hands the buffer back to
/// <see cref="ArrayPool{T}.Shared"/>: decode it (<see cref="AlpacaImageBytes.DecodeChannel"/>), then dispose
/// it. A span kept past the dispose would read whatever the pool's next renter writes, so reading the
/// payload after it throws instead.
/// </summary>
internal sealed class ImageBytesPayload : IDisposable
{
    private byte[]? _buffer;

    /// <param name="buffer">A buffer from <see cref="ArrayPool{T}.Shared"/>'s <c>Rent</c> and nothing else:
    /// the dispose returns it there, and the shared pool refuses an array whose length is not one of its
    /// own bucket sizes.</param>
    /// <param name="length">How many of its leading bytes are the payload.</param>
    internal ImageBytesPayload(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    /// <summary>The payload's size in bytes; the rented buffer behind it is usually longer.</summary>
    public int Length { get; }

    /// <summary>The payload bytes, valid until <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> Span => _buffer is { } buffer
        ? buffer.AsSpan(0, Length)
        : throw new ObjectDisposedException(nameof(ImageBytesPayload));

    /// <summary>Returns the buffer to the pool exactly once, however often it is called: a buffer
    /// returned twice would be handed to two renters at the same time.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _buffer, null) is { } buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
