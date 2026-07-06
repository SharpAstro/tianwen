using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Alpaca;

/// <summary>
/// Decoder for the ASCOM Alpaca "ImageBytes" binary image-array transfer
/// (<c>application/imagebytes</c>). The payload is a 44-byte little-endian metadata
/// header (ArrayMetadataV1) followed by the raw pixel data. This replaces the legacy
/// JSON <c>imagearray</c> transfer, which encodes every pixel as a decimal-ASCII integer
/// and is an order of magnitude slower for full-frame images.
///
/// Header layout (all <see cref="int"/>, little-endian) per ASCOMInitiative/ASCOMLibrary
/// <c>ArrayMetadataV1</c>: 0 MetadataVersion, 4 ErrorNumber, 8 ClientTransactionID,
/// 12 ServerTransactionID, 16 DataStart, 20 ImageElementType, 24 TransmissionElementType,
/// 28 Rank, 32 Dimension1, 36 Dimension2, 40 Dimension3.
/// </summary>
internal static class AlpacaImageBytes
{
    /// <summary>MIME type negotiated via the HTTP <c>Accept</c> header and returned in <c>Content-Type</c>.</summary>
    public const string MimeType = "application/imagebytes";

    internal const int MetadataV1Length = 44;
    private const int SupportedMetadataVersion = 1;

    /// <summary>
    /// ASCOM <c>ImageArrayElementTypes</c>. Used for both the image element type and the
    /// (possibly smaller) transmission element type actually sent over the wire.
    /// </summary>
    internal enum ElementType
    {
        Unknown = 0,
        Int16 = 1,
        Int32 = 2,
        Double = 3,
        Single = 4,
        UInt64 = 5,
        Byte = 6,
        Int64 = 7,
        UInt16 = 8,
        UInt32 = 9,
    }

    /// <summary>
    /// Decodes an ImageBytes payload into a single monochrome/Bayer <see cref="Channel"/> in
    /// row-major <c>[Height, Width]</c> layout holding raw ADU values.
    ///
    /// ImageBytes transmits a 2D image as <c>[Dimension1 = Width(X), Dimension2 = Height(Y)]</c>
    /// in row-major order (last index fastest) — i.e. column-major in image terms, so the flat
    /// index of pixel <c>(x, y)</c> is <c>y + x*Height</c>. This method transposes that into
    /// <see cref="Channel"/>'s <c>[y, x]</c> layout. Pixels are read host-endian (all supported
    /// targets are little-endian, matching ASCOM's own transfer assumption).
    /// </summary>
    /// <exception cref="AlpacaException">The server reported a non-zero error number; the body carries a UTF-8 message.</exception>
    /// <exception cref="NotSupportedException">Unsupported metadata version, rank, element type, or malformed dimensions.</exception>
    /// <param name="payload">The raw <c>application/imagebytes</c> response body.</param>
    /// <param name="recycled">Optional buffer to decode into (the driver's recycle bag, see
    /// <c>DALCameraDriver._freeBuffers</c>); reused when its shape matches the frame, otherwise
    /// dropped to GC (ROI/bin change) and a fresh array is allocated.</param>
    public static Channel DecodeChannel(ReadOnlySpan<byte> payload, float[,]? recycled = null)
    {
        if (payload.Length < MetadataV1Length)
        {
            throw new NotSupportedException($"ImageBytes payload too small ({payload.Length} bytes; need at least {MetadataV1Length}).");
        }

        var metadataVersion = BinaryPrimitives.ReadInt32LittleEndian(payload[0..]);
        if (metadataVersion != SupportedMetadataVersion)
        {
            throw new NotSupportedException($"Unsupported ImageBytes metadata version {metadataVersion} (expected {SupportedMetadataVersion}).");
        }

        var errorNumber = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        var dataStart = BinaryPrimitives.ReadInt32LittleEndian(payload[16..]);

        if (errorNumber != 0)
        {
            // On error the bytes from DataStart (>= the 44-byte header) are a UTF-8 encoded
            // message rather than pixels; null falls back to a generic message.
            var message = dataStart >= MetadataV1Length && dataStart < payload.Length
                ? Encoding.UTF8.GetString(payload[dataStart..])
                : null;
            throw new AlpacaException(errorNumber, message);
        }

        var transmissionElementType = (ElementType)BinaryPrimitives.ReadInt32LittleEndian(payload[24..]);
        var rank = BinaryPrimitives.ReadInt32LittleEndian(payload[28..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(payload[32..]);  // Dimension1 = X
        var height = BinaryPrimitives.ReadInt32LittleEndian(payload[36..]); // Dimension2 = Y

        if (rank != 2)
        {
            // 3D (colour-plane) frames are out of scope: the session treats raw frames as mono/Bayer.
            throw new NotSupportedException($"ImageBytes rank {rank} is not supported (only 2D monochrome/Bayer frames).");
        }

        if (width <= 0 || height <= 0)
        {
            throw new NotSupportedException($"ImageBytes reported non-positive dimensions {width}x{height}.");
        }

        if (dataStart < MetadataV1Length || dataStart > payload.Length)
        {
            throw new NotSupportedException($"ImageBytes DataStart {dataStart} is out of range (payload {payload.Length} bytes).");
        }

        var pixels = payload[dataStart..];
        var channel = recycled is not null && recycled.GetLength(0) == height && recycled.GetLength(1) == width
            ? recycled
            : new float[height, width];
        float min, max;

        switch (transmissionElementType)
        {
            case ElementType.Byte: (min, max) = FillTransposed<byte>(pixels, width, height, channel); break;
            case ElementType.Int16: (min, max) = FillTransposed<short>(pixels, width, height, channel); break;
            case ElementType.UInt16: (min, max) = FillTransposed<ushort>(pixels, width, height, channel); break;
            case ElementType.Int32: (min, max) = FillTransposed<int>(pixels, width, height, channel); break;
            case ElementType.UInt32: (min, max) = FillTransposed<uint>(pixels, width, height, channel); break;
            case ElementType.Int64: (min, max) = FillTransposed<long>(pixels, width, height, channel); break;
            case ElementType.UInt64: (min, max) = FillTransposed<ulong>(pixels, width, height, channel); break;
            case ElementType.Single: (min, max) = FillTransposed<float>(pixels, width, height, channel); break;
            case ElementType.Double: (min, max) = FillTransposed<double>(pixels, width, height, channel); break;
            default: throw new NotSupportedException($"Unsupported ImageBytes transmission element type {transmissionElementType}.");
        }

        return new Channel(channel, default, min, max, 0);
    }

    /// <summary>
    /// Reads <paramref name="pixels"/> as little-endian <typeparamref name="T"/> elements in the
    /// ASCOM column-major order and writes them transposed into <paramref name="channel"/>
    /// (<c>[y, x]</c>), returning the (min, max) sample values.
    /// </summary>
    private static (float Min, float Max) FillTransposed<T>(ReadOnlySpan<byte> pixels, int width, int height, float[,] channel)
        where T : unmanaged, INumber<T>
    {
        var typed = MemoryMarshal.Cast<byte, T>(pixels);
        var expected = checked(width * height);
        if (typed.Length < expected)
        {
            throw new NotSupportedException($"ImageBytes pixel data too short: {typed.Length} {typeof(T).Name} elements for a {width}x{height} image (need {expected}).");
        }

        var min = float.MaxValue;
        var max = float.MinValue;
        for (var x = 0; x < width; x++)
        {
            // Flat index of pixel (x, 0); Y is the fastest-varying dimension.
            var columnBase = x * height;
            for (var y = 0; y < height; y++)
            {
                var value = float.CreateTruncating(typed[columnBase + y]);
                channel[y, x] = value;
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }

        return (min, max);
    }
}
