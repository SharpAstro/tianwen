using System;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Png;
using SharpAstro.Tiff;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end: a star field written to an INTEGER-sample file, read back through the importer, must
/// still detect its stars.
/// </summary>
/// <remarks>
/// <para>The importer normalises integer samples to <c>[0, 1]</c> but records the SOURCE container
/// width as <see cref="Image.BitDepth"/> -- so a 16-bit PNG arrives as unit-referred samples labelled
/// <see cref="BitDepth.Int16"/>. <c>Image.Histogram</c> decides its bin count from
/// <c>IsUnitScaledFloat</c>, which requires <see cref="BitDepth.Float32"/>, so that image bins into
/// TWO buckets, <c>Background</c> answers nonsense, and <c>FindStarsAsync</c> returns an EMPTY list
/// for every PNG, JPEG and 8/16-bit TIFF the viewer opens.</para>
/// <para>Nothing reports it: the star overlay, HFD/FWHM, Boost, Calibrate and SPCC are all gated on
/// <c>Stars is { Count: &gt; 0 }</c>, so they simply go quiet.</para>
/// </remarks>
[Collection("Imaging")]
public class UnitReferredImportStarDetectionTests(ITestOutputHelper output)
{
    private const int Width = 512;
    private const int Height = 512;
    private const int PlantedStars = 40;

    [Fact]
    public async Task A16BitPngStarFieldStillDetectsItsStarsAfterImport()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"tw-unitref-{Guid.NewGuid():N}.png");
        try
        {
            WriteStarFieldPng(path);

            Image.TryReadImageFile(path, out var image).ShouldBeTrue();
            image.ShouldNotBeNull();
            output.WriteLine($"imported bitDepth={image.BitDepth} maxValue={image.MaxValue}");

            var bg = image.Background(image.ReferenceStarChannel);
            output.WriteLine($"background={bg.background:G4} starLevel={bg.starLevel:G4} noise={bg.noise_level:G4} threshold={bg.threshold:G4}");

            var stars = await image.FindStarsAsync(image.ReferenceStarChannel, snrMin: 10f, minStars: 20,
                logger: new XunitLogger(output), cancellationToken: ct);
            output.WriteLine($"detected {stars.Count} of {PlantedStars} planted");

            // Not a count pin -- the point is that it is not ZERO. A detector that finds most of a
            // clean synthetic field is working; one that finds none is the two-bin histogram.
            stars.Count.ShouldBeGreaterThan(PlantedStars / 2);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>
    /// The reachable path, end to end through the real document open. <c>.png</c> is not in
    /// <see cref="AstroImageDocument.SupportedExtensions"/> but <c>.tif</c> is, and an 8-bit TIFF is
    /// exactly the document class that motivated this work (an architectural drawing exported from
    /// CAD), so this is the test that says the viewer overlay was actually dead rather than only
    /// theoretically so.
    /// </summary>
    [Fact]
    public async Task An8BitTiffOpenedAsADocumentDetectsItsStars()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"tw-unitref-{Guid.NewGuid():N}.tif");
        try
        {
            // AddPngPageAsync re-frames PNG rows as a Deflate TIFF page, which is the cheapest route
            // to a genuine 8-bit TIFF from here -- the float writer this repo normally uses would
            // produce Float32 samples and miss the case entirely.
            var png = PngWriter.EncodeGray8(StarField8Bit(), Width, Height);
            await using (var writer = TiffWriter.Create(path))
            {
                await writer.AddPngPageAsync(png, ct: ct);
                await writer.FlushAsync(ct);
            }

            var document = await AstroImageDocument.OpenAsync(path, cancellationToken: ct);
            document.ShouldNotBeNull();
            output.WriteLine($"document bitDepth={document.UnstretchedImage.BitDepth} " +
                $"maxValue={document.UnstretchedImage.MaxValue} " +
                $"unitReferred={document.UnstretchedImage.SamplesAreUnitReferred}");

            await document.DetectStarsAsync(ct);
            output.WriteLine($"document.Stars = {document.Stars?.Count ?? -1}, HFR={document.AverageHFR:F2}");

            // Every toolbar affordance downstream is gated on this being non-empty: the star overlay,
            // the HFD/FWHM readout, Boost, Calibrate and SPCC.
            document.Stars.ShouldNotBeNull();
            document.Stars.Count.ShouldBeGreaterThan(PlantedStars / 2);
            document.AverageHFR.ShouldBeGreaterThan(0f);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>The same field as <see cref="WriteStarFieldPng"/>, quantised to 8 bits.</summary>
    private static byte[] StarField8Bit()
    {
        var pixels = new byte[Width * Height];
        var rng = new Random(42);
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(24 + rng.Next(-3, 4));
        }

        var step = Width / 8;
        var planted = 0;
        for (var gy = 1; gy < 8 && planted < PlantedStars; gy++)
        {
            for (var gx = 1; gx < 8 && planted < PlantedStars; gx++)
            {
                var cx = gx * step;
                var cy = gy * step;
                var peak = 90f + (float)rng.NextDouble() * 160f;
                for (var dy = -5; dy <= 5; dy++)
                {
                    for (var dx = -5; dx <= 5; dx++)
                    {
                        var v = peak * MathF.Exp(-(dx * dx + dy * dy) / 4.0f);
                        var idx = (cy + dy) * Width + (cx + dx);
                        pixels[idx] = (byte)Math.Clamp(pixels[idx] + v, 0f, 255f);
                    }
                }
                planted++;
            }
        }

        return pixels;
    }
    /// <summary>Low background, mild noise, well-separated Gaussian stars -- unambiguous for any
    /// correctly calibrated detector, written as 16-bit gray so the importer takes its integer path.</summary>
    private static void WriteStarFieldPng(string path)
    {
        var pixels = new ushort[Width * Height];
        var rng = new Random(42);
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)(2000 + rng.Next(-60, 61));
        }

        var step = Width / 8;
        var planted = 0;
        for (var gy = 1; gy < 8 && planted < PlantedStars; gy++)
        {
            for (var gx = 1; gx < 8 && planted < PlantedStars; gx++)
            {
                var cx = gx * step;
                var cy = gy * step;
                var peak = 15000f + (float)rng.NextDouble() * 40000f;
                for (var dy = -5; dy <= 5; dy++)
                {
                    for (var dx = -5; dx <= 5; dx++)
                    {
                        var v = peak * MathF.Exp(-(dx * dx + dy * dy) / 4.0f);
                        var idx = (cy + dy) * Width + (cx + dx);
                        pixels[idx] = (ushort)Math.Clamp(pixels[idx] + v, 0f, 65535f);
                    }
                }
                planted++;
            }
        }

        File.WriteAllBytes(path, PngWriter.EncodeGray16(pixels, Width, Height));
    }
}
