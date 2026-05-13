using SharpAstro.Png;
using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
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
/// spectral database — so CameraToSrgbMatrix is legitimately null for
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

        // Production import path — this is the same call Image.Importer
        // makes when a user drops a .cr3 onto the GUI. Phase B + B.5 + B.6
        // in FC.SDK.Raw now make this work without the Magick.NET fallback.
        var ok = Image.TryReadImageFile(path, out var mosaicImage);
        ok.ShouldBeTrue("Image.TryReadImageFile should handle .cr3 via FC.SDK.Raw");
        mosaicImage.ShouldNotBeNull();

        var (channels, w, h) = mosaicImage.Shape;
        channels.ShouldBe(1, "CR3 import returns the Bayer mosaic as a 1-channel float Image");
        w.ShouldBe(5248);
        h.ShouldBe(3510);
        mosaicImage.ImageMeta.SensorType.ShouldBe(SensorType.RGGB);
        mosaicImage.ImageMeta.Instrument.ShouldBe("Canon EOS R5");
        output.WriteLine($"Loaded {w}x{h} {mosaicImage.ImageMeta.Instrument} CR3 " +
            $"(matrix={(mosaicImage.ImageMeta.CameraToSrgbMatrix is null ? "null" : "populated")})");

        // Bayer-aware AHD debayer — exactly the same downstream call the
        // GUI's image viewer pipes the mosaic through.
        var rgbImage = await mosaicImage.DebayerAsync(DebayerAlgorithm.AHD, cancellationToken: ct);
        var (rgbChannels, rgbW, rgbH) = rgbImage.Shape;
        rgbChannels.ShouldBe(3);
        rgbW.ShouldBe(w);
        rgbH.ShouldBe(h);

        // Single PNG (no matrix, since the R5 isn't in CanonCameraProfiles
        // or SASP — the matrix render is identical to the no-matrix one
        // when matrix is null, so emitting both would be wasted disk).
        var outDir = CreateTestOutputDir(nameof(Cr3_OpensViaImageTryReadImageFile_AndRgbRenders));
        var pngPath = Path.Combine(outDir, "cr3_r5_debayered.png");
        RenderRgbToPng(rgbImage, pngPath, applyMatrix: rgbImage.ImageMeta.CameraToSrgbMatrix);
        output.WriteLine($"CR3 debayered render: {pngPath}");
        new FileInfo(pngPath).Length.ShouldBeGreaterThan(10_000);
    }

    /// <summary>3-channel float -> PNG with optional matrix, joint
    /// auto-stretch by global max, and sRGB gamma encode. Identical to
    /// the helper in <see cref="Cr2ImportTests"/> — kept duplicated for
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
        File.WriteAllBytes(outPath, PngWriter.Encode(rgba, w, h));
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
