using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Tests.Helpers;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end Canon CR3 import via TianWen's production
/// <see cref="Image.TryReadImageFile"/> path. Mirrors <see cref="Cr2ImportTests"/>
/// but exercises the CR3 / CRX side of FC.SDK.Raw: the R5 fixture is
/// lossy cRAW (encType=0 levels=3 with FF13 per-position quantization),
/// which runs through CrxQpDecoder + CrxQStep + CrxWaveletPlaneDecoder
/// before producing the Bayer mosaic. The matrix assertion is omitted
/// here because the R5 isn't in <c>CanonCameraProfiles</c> or the SASP
/// spectral database, so CameraToSrgbMatrix is legitimately null for
/// this body. Coverage of the matrix-resolution dispatch lives in
/// <see cref="Cr2ImportTests"/>.
///
/// Fixture: <c>Data/CR3/Canon_EOS_R5_CRAW.CR3</c>, LFS-tracked (~7 MB).
/// Sourced from <c>raw.pixls.us</c>, CC0. Tests skip gracefully when the
/// file is missing (clones without <c>git lfs pull</c>).
/// </summary>
[Collection("Scheduling")]
public class Cr3ImportTests(ITestOutputHelper output)
{
    private static string FixturePath
        => Path.Combine(AppContext.BaseDirectory, "Data", "CR3", "Canon_EOS_R5_CRAW.CR3");

    private static bool IsFixtureUsable(string path)
    {
        if (!File.Exists(path)) return false;
        // LFS pointer files are tiny UTF-8 text; the R5 fixture is ~7 MB so
        // a length cutoff distinguishes a real CR3 from an unpulled pointer.
        return new FileInfo(path).Length > 4096;
    }

    [Fact]
    public async Task Cr3_OpensViaImageTryReadImageFile_AndRgbRenders()
    {
        var path = FixturePath;
        if (!IsFixtureUsable(path))
        {
            Assert.Skip($"CR3 fixture not present or LFS pointer at {path}. " +
                "Run `git lfs pull --include=\"*.CR3\"` to fetch.");
            return;
        }
        var ct = TestContext.Current.CancellationToken;

        // Production import path: this is the same call Image.Importer
        // makes when a user drops a .cr3 onto the GUI. Phase B + B.5 + B.6
        // in FC.SDK.Raw now make this work without the Magick.NET fallback.
        var ok = Image.TryReadImageFile(path, out var mosaicImage);
        ok.ShouldBeTrue("Image.TryReadImageFile should handle .cr3 via FC.SDK.Raw");
        mosaicImage.ShouldNotBeNull();

        var (channels, w, h) = mosaicImage.Shape;
        channels.ShouldBe(1, "CR3 import returns the Bayer mosaic as a 1-channel float Image");

        // The ACTIVE AREA, not the decoded raster: the R5 records 5248x3510 and declares
        // (144, 108)-(5231, 3499) as the picture, so the import crops 160 columns and 118 rows of
        // shielded and spare sensor off. Asserting the raster size here is what let the shielded
        // border reach the display and the frame statistics unnoticed for as long as CR3 import
        // has existed -- it looked like a photograph with a dark edge.
        w.ShouldBe(5088);
        h.ShouldBe(3392);
        mosaicImage.ImageMeta.SensorType.ShouldBe(SensorType.RGGB);
        mosaicImage.ImageMeta.Instrument.ShouldBe("Canon EOS R5");
        output.WriteLine($"Loaded {w}x{h} {mosaicImage.ImageMeta.Instrument} CR3 " +
            $"(matrix={(mosaicImage.ImageMeta.CameraToSrgbMatrix is null ? "null" : "populated")})");

        // Bayer-aware AHD debayer: exactly the same downstream call the
        // GUI's image viewer pipes the mosaic through.
        var rgbImage = await mosaicImage.DebayerAsync(DebayerAlgorithm.AHD, cancellationToken: ct);
        var (rgbChannels, rgbW, rgbH) = rgbImage.Shape;
        rgbChannels.ShouldBe(3);
        rgbW.ShouldBe(w);
        rgbH.ShouldBe(h);

        // Single PNG (no matrix, since the R5 isn't in CanonCameraProfiles
        // or SASP: the matrix render is identical to the no-matrix one
        // when matrix is null, so emitting both would be wasted disk).
        var outDir = CreateTestOutputDir(nameof(Cr3_OpensViaImageTryReadImageFile_AndRgbRenders));
        var pngPath = Path.Combine(outDir, "cr3_r5_debayered.png");
        RenderRgbToPng(rgbImage, pngPath, applyMatrix: rgbImage.ImageMeta.CameraToSrgbMatrix);
        output.WriteLine($"CR3 debayered render: {pngPath}");
        new FileInfo(pngPath).Length.ShouldBeGreaterThan(10_000);
    }

    /// <summary>
    /// The crop is applied at the right OFFSET, not merely to the right SIZE.
    /// </summary>
    /// <remarks>
    /// <para>A size assertion alone passes on a crop taken from the wrong corner, which would shift
    /// every pixel coordinate the viewer, the star detector and any WCS agree on -- and look
    /// completely normal, because the wrong corner of a photograph is still a photograph.</para>
    /// <para>The test SEARCHES for a pixel where a cropped and an uncropped read disagree rather
    /// than picking one, and that is the point rather than fussiness: this fixture is almost
    /// entirely zero after black subtraction, so the corner, the centre and any fixed block all
    /// match on both sides and would have let an uncropped import pass. Failing to find such a pixel
    /// is itself a failure, because then the test could not have detected anything.</para>
    /// </remarks>
    [Fact]
    public void Cr3_CropsFromTheDeclaredOrigin()
    {
        var path = FixturePath;
        if (!IsFixtureUsable(path))
        {
            Assert.Skip($"CR3 fixture not present or LFS pointer at {path}.");
            return;
        }

        Image.TryReadImageFile(path, out var imported).ShouldBeTrue();
        imported.ShouldNotBeNull();

        var raw = FC.SDK.Raw.CanonRaw.Open(path);
        var area = raw.ActiveArea;
        var mosaic = FC.SDK.Raw.CanonRaw.PreprocessMosaic(raw);

        area.Left.ShouldBe(144);
        area.Top.ShouldBe(108);

        // Exact mapping at three corners, so a transposed offset -- (Top, Left) read as (Left, Top)
        // -- cannot slip through: it matches at none of them.
        imported[0, 0, 0].ShouldBe(Raster(0, 0));
        imported[0, 0, area.Width - 1].ShouldBe(Raster(area.Width - 1, 0));
        imported[0, area.Height - 1, 0].ShouldBe(Raster(0, area.Height - 1));

        var (probeX, probeY) = FindDiscriminatingPixel();
        probeY.ShouldBeGreaterThanOrEqualTo(0,
            "no pixel where a cropped and an uncropped read differ: this fixture cannot detect the bug");

        output.WriteLine($"discriminating pixel ({probeX}, {probeY}): cropped {Raster(probeX, probeY)}, " +
            $"uncropped {mosaic[(probeY * raw.Width) + probeX]}");
        imported[0, probeY, probeX].ShouldBe(Raster(probeX, probeY));

        float Raster(int x, int y) => mosaic[((y + area.Top) * raw.Width) + x + area.Left];

        (int X, int Y) FindDiscriminatingPixel()
        {
            // Coarse stride: any pixel will do, and a full scan of 17 megapixels to find one is
            // waste. Both reads must be in bounds, which the active area guarantees for the cropped
            // one and the smaller extents guarantee for the uncropped one.
            for (var y = 0; y < area.Height; y += 13)
            {
                for (var x = 0; x < area.Width; x += 13)
                {
                    if (Raster(x, y) != mosaic[(y * raw.Width) + x])
                    {
                        return (x, y);
                    }
                }
            }

            return (-1, -1);
        }
    }

    /// <summary>3-channel float -> PNG with optional matrix, joint
    /// auto-stretch by global max, and sRGB gamma encode. Identical to
    /// the helper in <see cref="Cr2ImportTests"/>: kept duplicated for
    /// now since both helpers are stop-gaps that disappear when the
    /// Phase 3 render path (StretchUniforms) ships.</summary>
    private static void RenderRgbToPng(Image rgbImage, string outPath, float[]? applyMatrix)
    {
        var (_, w, h) = rgbImage.Shape;
        var rSpan = rgbImage.GetChannelSpan(0);
        var gSpan = rgbImage.GetChannelSpan(1);
        var bSpan = rgbImage.GetChannelSpan(2);
        var pixels = w * h;

        var working = new float[pixels * 3];
        for (var p = 0; p < pixels; p++)
        {
            var r = rSpan[p];
            var g = gSpan[p];
            var b = bSpan[p];
            if (applyMatrix is not null)
            {
                working[p * 3]     = applyMatrix[0] * r + applyMatrix[1] * g + applyMatrix[2] * b;
                working[p * 3 + 1] = applyMatrix[3] * r + applyMatrix[4] * g + applyMatrix[5] * b;
                working[p * 3 + 2] = applyMatrix[6] * r + applyMatrix[7] * g + applyMatrix[8] * b;
            }
            else
            {
                working[p * 3] = r;
                working[p * 3 + 1] = g;
                working[p * 3 + 2] = b;
            }
        }

        var max = 0f;
        for (var i = 0; i < working.Length; i++) if (working[i] > max) max = working[i];
        if (max < 1e-6f) max = 1f;

        var rgba = new byte[pixels * 4];
        for (var p = 0; p < pixels; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                var v = working[p * 3 + c] / max;
                if (v < 0) v = 0; else if (v > 1) v = 1;
                rgba[p * 4 + c] = (byte)(SrgbEncode(v) * 255f + 0.5f);
            }
            rgba[p * 4 + 3] = 0xFF;
        }
        File.WriteAllBytes(outPath, DisplayImageWriter.EncodePng(rgba, w, h));
    }

    private static float SrgbEncode(float linear)
        => linear <= 0.0031308f
            ? 12.92f * linear
            : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;

    private static string CreateTestOutputDir(string testName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests",
            DateTime.Now.ToString("yyyyMMdd"), testName);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
