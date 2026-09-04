using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the "save as seen on screen" contract (P18): the file carries the DISPLAY raster at the
/// IMAGE's own size, in the container the caller asked for.
/// </summary>
/// <remarks>
/// The oracle throughout is <see cref="Image.RenderStretchedRgba16"/> itself, deliberately. Whether
/// that render is CORRECT is <c>StretchTests_NewPipeline</c>'s job and the CPU/GPU mirror's; what can
/// only be checked here is that the bytes reaching the file are the ones it produced, and that
/// nothing between the render and the encoder rescales, transposes or truncates them.
/// </remarks>
public class DisplayRasterExportTests
{
    private const int Width = 7;
    private const int Height = 5;

    // Deliberately not square and not a power of two: a transposed or stride-confused write survives
    // a square raster, and 7x5 is small enough to compare pixel by pixel.
    private static Image ColourImage()
    {
        var r = new float[Height, Width];
        var g = new float[Height, Width];
        var b = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                // Three visibly different ramps, so a channel swap is a failure rather than a tie.
                r[y, x] = (x + 1) / (float)Width;
                g[y, x] = (y + 1) / (float)Height;
                b[y, x] = 1f - (x + 1) / (float)Width;
            }
        }

        return new Image([r, g, b], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
            imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
    }

    private static StretchUniforms Uniforms() => new StretchUniforms(
        StretchMode.Linked,
        NormFactor: 1f,
        Pedestal: (0f, 0f, 0f),
        Shadows: (0.05f, 0.05f, 0.05f),
        Midtones: (0.4f, 0.4f, 0.4f),
        Highlights: (1f, 1f, 1f),
        Rescale: (1.05f, 1.05f, 1.05f));

    private static string TempPath(string extension)
        => Path.Combine(Path.GetTempPath(), $"tianwen-display-raster-{Guid.NewGuid():N}{extension}");

    /// <summary>
    /// The point of rendering rather than reading the framebuffer: the file is the IMAGE's size, not
    /// the window's. A screenshot-based implementation passes every other test here and fails this one.
    /// </summary>
    [Theory]
    [InlineData(DisplayRasterFormat.Png16, 16)]
    [InlineData(DisplayRasterFormat.Png8, 8)]
    public async Task ThePngIsTheImagesOwnSizeAndTheAskedForBitDepth(DisplayRasterFormat format, int expectedBitDepth)
    {
        var path = TempPath(".png");
        try
        {
            await DisplayRasterExport.WriteAsync(ColourImage(), path, format, Uniforms(),
                cancellationToken: TestContext.Current.CancellationToken);

            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

            // IHDR is the first chunk and is fixed-layout: 8-byte signature, 4-byte length, "IHDR",
            // then width, height (big-endian uint32), bit depth, colour type.
            bytes[..8].ShouldBe([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
            ReadBigEndianUInt32(bytes, 16).ShouldBe((uint)Width);
            ReadBigEndianUInt32(bytes, 20).ShouldBe((uint)Height);
            bytes[24].ShouldBe((byte)expectedBitDepth);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The format is the ARGUMENT's to decide, not the path extension's.</summary>
    [Fact]
    public async Task TheContainerIsTheOneAskedForEvenWhenTheExtensionDisagrees()
    {
        // A .png path asked to hold a JPEG must produce a JPEG: the caller resolved the format
        // already, and silently re-deciding from the extension would make the Save-As menu a lie.
        var path = TempPath(".png");
        try
        {
            await DisplayRasterExport.WriteAsync(ColourImage(), path, DisplayRasterFormat.Jpeg, Uniforms(),
                cancellationToken: TestContext.Current.CancellationToken);

            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            bytes[..3].ShouldBe([(byte)0xFF, (byte)0xD8, (byte)0xFF], "SOI + the first marker of a JPEG");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The end-to-end one: the pixels in the file ARE the display raster, read back through the
    /// repo's own float-TIFF convention.
    /// </summary>
    [Fact]
    public async Task TheTiffCarriesTheDisplayRasterPixelForPixel()
    {
        var image = ColourImage();
        var uniforms = Uniforms();

        var expected = new ushort[Width * Height * 4];
        image.RenderStretchedRgba16(uniforms, expected);

        var path = TempPath(".tif");
        try
        {
            await DisplayRasterExport.WriteAsync(image, path, DisplayRasterFormat.TiffFloat, uniforms,
                cancellationToken: TestContext.Current.CancellationToken);

            Image.TryReadTiff(path, out var readBack).ShouldBeTrue();
            readBack.ShouldNotBeNull();
            readBack.Shape.ChannelCount.ShouldBe(3);
            readBack.Shape.Width.ShouldBe(Width);
            readBack.Shape.Height.ShouldBe(Height);

            for (var c = 0; c < 3; c++)
            {
                var span = readBack.GetChannelSpan(c);
                for (var i = 0; i < Width * Height; i++)
                {
                    span[i].ShouldBe(expected[i * 4 + c] / 65535f, 1e-4f,
                        $"channel {c} sample {i}: the file must carry what the renderer produced");
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A single-channel view is MONO on screen -- the GPU uploads that one channel into slot 0 and
    /// samples it as grey -- so the file has to be mono too, carrying that channel and not the first.
    /// </summary>
    [Fact]
    public async Task ASingleChannelViewSavesThatChannelAsMono()
    {
        var image = ColourImage();
        var uniforms = Uniforms();

        var composite = new ushort[Width * Height * 4];
        image.RenderStretchedRgba16(uniforms, composite);

        var path = TempPath(".tif");
        try
        {
            // Channel 2 is blue, whose ramp runs opposite to red's -- so saving the wrong channel
            // does not merely shift values, it reverses the gradient.
            await DisplayRasterExport.WriteAsync(image, path, DisplayRasterFormat.TiffFloat, uniforms,
                displayedChannel: 2, cancellationToken: TestContext.Current.CancellationToken);

            Image.TryReadTiff(path, out var readBack).ShouldBeTrue();
            readBack.ShouldNotBeNull();
            readBack.Shape.ChannelCount.ShouldBe(1, "a single-channel view is grey, not a colour image");

            var span = readBack.GetChannelSpan(0);
            for (var i = 0; i < Width * Height; i++)
            {
                span[i].ShouldBe(composite[i * 4 + 2] / 65535f, 1e-4f,
                    $"sample {i}: the mono file must carry the BLUE channel's own rendered value");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// <c>.png</c> resolves to the 16-bit variant. Both PNG depths share an extension, so a path
    /// cannot distinguish them and the lossless one is what a bare "Save" should land on.
    /// </summary>
    [Theory]
    [InlineData("shot.png", DisplayRasterFormat.Png16)]
    [InlineData("shot.PNG", DisplayRasterFormat.Png16)]
    [InlineData("shot.jpg", DisplayRasterFormat.Jpeg)]
    [InlineData("shot.jpeg", DisplayRasterFormat.Jpeg)]
    [InlineData("shot.tif", DisplayRasterFormat.TiffFloat)]
    [InlineData("shot.tiff", DisplayRasterFormat.TiffFloat)]
    public void AnExtensionResolvesToItsFormat(string fileName, DisplayRasterFormat expected)
        => DisplayRasterExport.FromExtension(fileName).ShouldBe(expected);

    [Theory]
    [InlineData("shot.fits")]
    [InlineData("shot.exr")]
    [InlineData("shot")]
    public void AnExtensionWeDoNotWriteResolvesToNothing(string fileName)
        => DisplayRasterExport.FromExtension(fileName).ShouldBeNull();

    private static uint ReadBigEndianUInt32(byte[] bytes, int offset)
        => ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
}
