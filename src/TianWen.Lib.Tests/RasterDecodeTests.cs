using System;
using System.Runtime.InteropServices;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Jpeg;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A raster decode (every Canon Live View frame, every remote preview frame) used to widen the decoded
/// samples into an interleaved RGBA float copy first and then into the planes: 16 bytes a pixel of
/// intermediate before the 12 of the planes. The samples now go straight into the planes, with exactly
/// the values <see cref="RasterImage.ExpandToFloats"/> gives; and a streaming driver decodes into planes
/// its frames hand back when released.
/// </summary>
[Collection("Allocations")]
public class RasterDecodeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(SampleFormat.UInt8, 1)]
    [InlineData(SampleFormat.UInt8, 2)]
    [InlineData(SampleFormat.UInt8, 3)]
    [InlineData(SampleFormat.UInt8, 4)]
    [InlineData(SampleFormat.UInt16, 1)]
    [InlineData(SampleFormat.UInt16, 3)]
    [InlineData(SampleFormat.Float32, 3)]
    [InlineData(SampleFormat.Float32, 4)]
    public void TheSamplesWidenToExactlyWhatExpandToFloatsGives(SampleFormat format, int channels)
    {
        const int width = 37, height = 11;
        var raster = Raster(width, height, channels, format);
        var rgba = raster.ToFloats();
        var outChannels = channels >= 3 ? 3 : 1;
        var planes = new float[outChannels][,];
        for (var c = 0; c < outChannels; c++)
        {
            planes[c] = new float[height, width];
            planes[c][0, 0] = float.NaN; // every pixel must be written, this one included
        }

        Image.WidenDecodedInto(raster, planes);

        for (var c = 0; c < outChannels; c++)
        {
            for (var p = 0; p < width * height; p++)
            {
                planes[c][p / width, p % width].ShouldBe(rgba[(p * 4) + c], $"plane {c}, pixel {p}");
            }
        }
    }

    [Fact]
    public void ADecodeAllocatesNoInterleavedFloatCopy()
    {
        const int width = 1024, height = 680;
        var jpeg = Jpeg(width, height);

        Image.TryDecodeRaster(jpeg, out _).ShouldBeTrue();

        var before = GC.GetAllocatedBytesForCurrentThread();
        Image.TryDecodeRaster(jpeg, out var image).ShouldBeTrue();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        (image.Width, image.Height, image.ChannelCount).ShouldBe((width, height, 3));
        var perPixel = (double)allocated / (width * height);
        output.WriteLine($"{width}x{height} JPEG decode: {allocated:N0} bytes, {perPixel:F1} per pixel");
        // The planes are 12 bytes a pixel and the decoder's own 8-bit raster 3; the RGBA float copy was 16 more.
        perPixel.ShouldBeLessThan(20.0, "the samples widen straight into the planes");
    }

    /// <summary>
    /// A frame decoded into recycled planes is exactly the self-owned decode, whatever the planes held, and
    /// a stream that releases each frame before the next allocates no plane after its first frame: at the
    /// EVF's 15 to 30 fps that was three new planes a frame.
    /// </summary>
    [Fact]
    public void AStreamDecodesIntoThePlanesItsFramesHandBack()
    {
        const int width = 320, height = 212;
        var jpeg = Jpeg(width, height);
        Image.TryDecodeRaster(jpeg, out var expected).ShouldBeTrue();
        var recycler = new PlaneRecycler("test");

        // Junk in the recycler first: three planes of the frame's shape, full of NaN, released back.
        var junk = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            junk[c] = recycler.Take(height, width);
            MemoryMarshal.CreateSpan(ref junk[c][0, 0], junk[c].Length).Fill(float.NaN);
        }
        new Image([recycler.Wrap(junk[0], 0f, 1f, 0), recycler.Wrap(junk[1], 0f, 1f, 1), recycler.Wrap(junk[2], 0f, 1f, 2)],
            BitDepth.Int8, 0f, expected.ImageMeta, samplesAreUnitReferred: true).Release();

        for (var frame = 0; frame < 5; frame++)
        {
            Image.TryDecodeRaster(jpeg, recycler, out var decoded).ShouldBeTrue();
            for (var c = 0; c < 3; c++)
            {
                decoded.GetChannelSpan(c).SequenceEqual(expected.GetChannelSpan(c)).ShouldBeTrue($"frame {frame}, channel {c}");
            }
            (decoded.MaxValue, decoded.MinValue).ShouldBe((expected.MaxValue, expected.MinValue));
            decoded.Release();
        }

        recycler.PlanesAllocated.ShouldBe(3, "the junk frame's three planes carried the whole stream");

        // What a steady frame still costs: the decoder's own 8-bit raster and its working state.
        var before = GC.GetAllocatedBytesForCurrentThread();
        Image.TryDecodeRaster(jpeg, recycler, out var steady).ShouldBeTrue();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        steady.Release();
        var perPixel = (double)allocated / (width * height);
        output.WriteLine($"{width}x{height} JPEG into recycled planes: {allocated:N0} bytes, {perPixel:F1} per pixel");
        perPixel.ShouldBeLessThan(8.0, "no plane and no float copy: what is left is the decoder's");
    }

    // An interleaved raster of every format, with values that exercise the endpoints and the middle.
    private static RasterImage Raster(int width, int height, int channels, SampleFormat format)
    {
        var samples = width * height * channels;
        var bytesPerSample = RasterImage.BytesPerSample(format);
        var pixels = new byte[samples * bytesPerSample];
        for (var i = 0; i < samples; i++)
        {
            switch (format)
            {
                case SampleFormat.UInt8:
                    pixels[i] = (byte)(i * 37 % 256);
                    break;
                case SampleFormat.UInt16:
                    MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan())[i] = (ushort)(i * 7919 % 65536);
                    break;
                default:
                    MemoryMarshal.Cast<byte, float>(pixels.AsSpan())[i] = (i % 97) / 64f - 0.25f; // HDR and negatives too
                    break;
            }
        }
        return new RasterImage(width, height, channels, format, pixels);
    }

    // What a Canon body streams: an 8-bit RGB JPEG.
    private static byte[] Jpeg(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)((i * 31 / 3) % 251);
        }
        return JpegEncoder.Encode(rgb, width, height, 3, new JpegEncodeOptions { Quality = 90 });
    }
}
