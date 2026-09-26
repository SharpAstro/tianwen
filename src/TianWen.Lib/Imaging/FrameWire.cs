using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging;

/// <summary>How a frame's samples cross the wire.</summary>
public enum FrameSampleFormat : byte
{
    /// <summary>Every sample a whole number from 0 to 65535, packed losslessly to 16 bits: half the bytes.</summary>
    UInt16 = 1,

    /// <summary>The float samples as they are, bit for bit.</summary>
    Single = 2,
}

/// <summary>
/// The binary shape of a LINEAR frame crossing a node's socket (P4 of docs/plans/hardware-in-the-server.md, #931,
/// "Frames are linear and binary, never the preview JPEG"): the viewer's stretch, statistics, star profile, plate solve
/// and snapshot save all assume linear floats in memory, which a stretched 8-bit JPEG cannot give back.
/// </summary>
/// <remarks>
/// <para>Little-endian throughout: the magic <c>TWFR</c>, a <c>u16</c> version, a <c>u8</c>
/// <see cref="FrameSampleFormat"/>, a reserved byte, an <c>i32</c> header length, the header as UTF-8 JSON (the image's
/// <see cref="ImageMeta"/>, bit depth, pedestal and each channel's filter and range), then each channel's plane, row
/// after row. That is the <c>[y, x]</c> order a plane is held in, so neither end transposes.</para>
/// <para><b>A frame whose every sample is a whole number from 0 to 65535 goes as 16 bits</b>, which is the usual camera
/// frame in ADU: half the bytes and still lossless. The check stops at the first sample that is not, and the frame goes
/// as floats, bit for bit.</para>
/// <para><b>The reader recycles its planes</b> (<see cref="FrameReader"/>).</para>
/// </remarks>
public static class FrameWire
{
    /// <summary>The media type a frame is served as.</summary>
    public const string ContentType = "application/x-tianwen-frame";

    internal const ushort Version = 1;
    internal const int PreambleLength = 12;

    /// <summary>How much of a plane moves per write or read: large enough to keep the copies efficient, small enough
    /// to stay out of the large-object heap's way (it is pooled either way).</summary>
    private const int BandBytes = 1 << 20;

    private static ReadOnlySpan<byte> Magic => "TWFR"u8;

    /// <summary>Writes <paramref name="image"/> to <paramref name="stream"/>. The caller keeps the image, leased for the call.</summary>
    public static async Task WriteAsync(Image image, Stream stream, CancellationToken cancellationToken)
    {
        var format = PacksAsUInt16(image) ? FrameSampleFormat.UInt16 : FrameSampleFormat.Single;
        var header = JsonSerializer.SerializeToUtf8Bytes(FrameHeader.Of(image), FrameWireJsonContext.Default.FrameHeader);

        var preamble = new byte[PreambleLength];
        Magic.CopyTo(preamble);
        BinaryPrimitives.WriteUInt16LittleEndian(preamble.AsSpan(4), Version);
        preamble[6] = (byte)format;
        BinaryPrimitives.WriteInt32LittleEndian(preamble.AsSpan(8), header.Length);
        await stream.WriteAsync(preamble, cancellationToken);
        await stream.WriteAsync(header, cancellationToken);

        var band = ArrayPool<byte>.Shared.Rent(BandBytes);
        try
        {
            for (var c = 0; c < image.ChannelCount; c++)
            {
                var plane = image.GetChannelArray(c);
                for (var start = 0; start < plane.Length;)
                {
                    var (samples, bytes) = FillBand(plane, start, band, format);
                    await stream.WriteAsync(band.AsMemory(0, bytes), cancellationToken);
                    start += samples;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(band);
        }
    }

    /// <summary>
    /// Whether every sample of <paramref name="image"/> is a whole number from 0 to 65535, so it can go as 16 bits with no
    /// loss. Stops at the first sample that is not; a NaN is not.
    /// </summary>
    public static bool PacksAsUInt16(Image image)
    {
        for (var c = 0; c < image.ChannelCount; c++)
        {
            if (!PacksAsUInt16(image.GetChannelSpan(c)))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool PacksAsUInt16(ReadOnlySpan<float> samples)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var low = Vector<float>.Zero;
            var high = new Vector<float>(ushort.MaxValue);
            var vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
            foreach (var v in vectors)
            {
                // A NaN fails every comparison, so it fails here too.
                if (!Vector.EqualsAll(Vector.Floor(v), v) || !Vector.GreaterThanOrEqualAll(v, low) || !Vector.LessThanOrEqualAll(v, high))
                {
                    return false;
                }
            }
            i = vectors.Length * Vector<float>.Count;
        }
        for (; i < samples.Length; i++)
        {
            var v = samples[i];
            if (!(v >= 0 && v <= ushort.MaxValue && v == MathF.Floor(v)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Fills <paramref name="band"/> from the plane's samples at <paramref name="start"/>; answers how many samples and bytes.</summary>
    private static (int Samples, int Bytes) FillBand(float[,] plane, int start, byte[] band, FrameSampleFormat format)
    {
        var flat = MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);
        if (format is FrameSampleFormat.Single)
        {
            var count = Math.Min(flat.Length - start, band.Length / sizeof(float));
            MemoryMarshal.AsBytes(flat.Slice(start, count)).CopyTo(band);
            return (count, count * sizeof(float));
        }

        var packed = Math.Min(flat.Length - start, band.Length / sizeof(ushort));
        var destination = MemoryMarshal.Cast<byte, ushort>(band.AsSpan(0, packed * sizeof(ushort)));
        var source = flat.Slice(start, packed);
        for (var i = 0; i < packed; i++)
        {
            destination[i] = (ushort)source[i];
        }
        return (packed, packed * sizeof(ushort));
    }

    /// <summary>Reads a frame's preamble: its sample format and header length, or throws naming what was wrong.</summary>
    internal static (FrameSampleFormat Format, int HeaderLength) ReadPreamble(ReadOnlySpan<byte> preamble)
    {
        if (!preamble[..4].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a TianWen frame: the magic is missing");
        }
        var version = BinaryPrimitives.ReadUInt16LittleEndian(preamble[4..]);
        if (version != Version)
        {
            throw new InvalidDataException($"A frame of wire version {version}; this reader reads version {Version}");
        }
        var format = (FrameSampleFormat)preamble[6];
        if (format is not (FrameSampleFormat.UInt16 or FrameSampleFormat.Single))
        {
            throw new InvalidDataException($"A frame of unknown sample format {preamble[6]}");
        }
        return (format, BinaryPrimitives.ReadInt32LittleEndian(preamble[8..]));
    }

    /// <summary>Unpacks a band of wire bytes into the plane's samples at <paramref name="start"/>; answers how many samples.</summary>
    internal static int DrainBand(ReadOnlySpan<byte> band, float[,] plane, int start, FrameSampleFormat format)
    {
        var flat = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
        if (format is FrameSampleFormat.Single)
        {
            var count = band.Length / sizeof(float);
            band[..(count * sizeof(float))].CopyTo(MemoryMarshal.AsBytes(flat.Slice(start, count)));
            return count;
        }

        var packed = band.Length / sizeof(ushort);
        RawPixelConversion.WidenToSingle(MemoryMarshal.Cast<byte, ushort>(band[..(packed * sizeof(ushort))]), flat.Slice(start, packed));
        return packed;
    }

    internal static int BytesPerSample(FrameSampleFormat format) => format is FrameSampleFormat.UInt16 ? sizeof(ushort) : sizeof(float);

    internal static int BandLength => BandBytes;
}

/// <summary>
/// Reads frames off the wire (<see cref="FrameWire"/>) into planes it RECYCLES: a frame's planes come back to the reader
/// when the image is released, and the next frame of the same shape is read into them. So a client showing frame after
/// frame allocates no plane after the first; a 26 MP float plane is 104 MB, large-object-heap garbage otherwise
/// (docs/plans/hardware-in-the-server.md, P4). One reader per source, since a source's frames share a shape.
/// </summary>
public sealed class FrameReader
{
    private readonly ConcurrentDictionary<(int Height, int Width), ConcurrentBag<float[,]>> _free = new ConcurrentDictionary<(int, int), ConcurrentBag<float[,]>>();

    /// <summary>How many planes wait to be read into again.</summary>
    public int FreePlanes
    {
        get
        {
            var count = 0;
            foreach (var bag in _free.Values)
            {
                count += bag.Count;
            }
            return count;
        }
    }

    /// <summary>
    /// Reads one frame from <paramref name="stream"/>. The image is the caller's: releasing it gives its planes back to
    /// this reader.
    /// </summary>
    public async Task<Image> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var preamble = new byte[FrameWire.PreambleLength];
        await stream.ReadExactlyAsync(preamble, cancellationToken);
        var (format, headerLength) = FrameWire.ReadPreamble(preamble);
        var headerBytes = new byte[headerLength];
        await stream.ReadExactlyAsync(headerBytes, cancellationToken);
        var header = JsonSerializer.Deserialize(headerBytes, FrameWireJsonContext.Default.FrameHeader)
            ?? throw new InvalidDataException("A frame with no header");

        var planes = new float[header.Channels.Length][,];
        var band = ArrayPool<byte>.Shared.Rent(FrameWire.BandLength);
        try
        {
            var bytesPerSample = FrameWire.BytesPerSample(format);
            for (var c = 0; c < planes.Length; c++)
            {
                planes[c] = Rent(header.Height, header.Width);
                var samples = header.Height * header.Width;
                for (var start = 0; start < samples;)
                {
                    var bytes = Math.Min(samples - start, band.Length / bytesPerSample) * bytesPerSample;
                    await stream.ReadExactlyAsync(band.AsMemory(0, bytes), cancellationToken);
                    start += FrameWire.DrainBand(band.AsSpan(0, bytes), planes[c], start, format);
                }
            }
        }
        catch
        {
            foreach (var plane in planes)
            {
                if (plane is not null)
                {
                    Return(plane);
                }
            }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(band);
        }

        var channels = ImmutableArray.CreateBuilder<Channel>(planes.Length);
        for (var c = 0; c < planes.Length; c++)
        {
            var spec = header.Channels[c];
            channels.Add(new Channel(planes[c], Filter.FromName(spec.Filter), spec.MinValue, spec.MaxValue, spec.Index)
            {
                Buffer = new ChannelBuffer(planes[c], Return),
            });
        }
        return new Image(channels.MoveToImmutable(), header.BitDepth, header.Pedestal, header.Meta, header.SamplesAreUnitReferred);
    }

    private float[,] Rent(int height, int width)
        => _free.TryGetValue((height, width), out var bag) && bag.TryTake(out var plane) ? plane : new float[height, width];

    private void Return(float[,] plane)
        => _free.GetOrAdd((plane.GetLength(0), plane.GetLength(1)), static _ => new ConcurrentBag<float[,]>()).Add(plane);
}

/// <summary>What a frame's JSON header carries besides its planes.</summary>
internal sealed record FrameHeader(int Width, int Height, BitDepth BitDepth, float Pedestal, bool SamplesAreUnitReferred,
    ImmutableArray<FrameChannel> Channels, ImageMeta Meta)
{
    public static FrameHeader Of(Image image)
    {
        var channels = ImmutableArray.CreateBuilder<FrameChannel>(image.ChannelCount);
        for (var c = 0; c < image.ChannelCount; c++)
        {
            var channel = image.GetChannel(c);
            channels.Add(new FrameChannel(channel.Filter.Name, channel.MinValue, channel.MaxValue, channel.Index));
        }
        return new FrameHeader(image.Width, image.Height, image.BitDepth, image.Pedestal, image.SamplesAreUnitReferred,
            channels.MoveToImmutable(), image.ImageMeta);
    }
}

/// <summary>A channel's own description: its filter (by name), its range and its place in the image.</summary>
internal sealed record FrameChannel(string Filter, float MinValue, float MaxValue, byte Index);

// The image JSON's own options (ImageJsonSerializerContext), so the metadata crosses exactly as it is written beside a
// frame elsewhere, NaN ranges included.
[JsonSourceGenerationOptions(
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    IgnoreReadOnlyFields = false,
    IgnoreReadOnlyProperties = false,
    IncludeFields = true,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower
)]
[JsonSerializable(typeof(FrameHeader))]
internal partial class FrameWireJsonContext : JsonSerializerContext
{
}
