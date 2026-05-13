using SharpAstro.Png;
using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end Canon CR2 import via TianWen's production
/// <see cref="Image.TryReadImageFile"/> path (Phase 2b). The CR2 fixture
/// is decoded through FC.SDK.Raw's pure-managed pipeline (no Magick.NET),
/// black-subtracted, white-balanced, wrapped as a 1-channel float
/// <see cref="Image"/> with <see cref="SensorType.RGGB"/>, and the
/// per-camera CameraToSrgbMatrix is populated via spectral (SASP) lookup
/// or the dcraw factory fallback.
///
/// The test then debayers via <see cref="Image.DebayerAsync"/> and saves
/// two PNGs to the test temp dir for visual inspection:
///
/// <list type="bullet">
/// <item><b>raw_debayered.png</b> — debayered RGB + auto-stretch + sRGB
/// gamma, NO matrix applied. "Without matrix" baseline.</item>
/// <item><b>raw_debayered_matrix.png</b> — same pipeline with
/// <see cref="ImageMeta.CameraToSrgbMatrix"/> applied between debayer
/// and auto-stretch. "With matrix" preview (Phase 3 will apply this
/// automatically in the render pipeline).</item>
/// </list>
///
/// Fixture: <c>Data/CR2/_MG_7578.CR2</c>, LFS-tracked. Tests skip
/// gracefully when the file is missing (clones without <c>git lfs pull</c>).
/// </summary>
public class Cr2ImportTests(ITestOutputHelper output)
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Data", "CR2", "_MG_7578.CR2");

    /// <summary>Returns true if <paramref name="path"/> exists AND looks like
    /// an actual CR2 file (not an unpulled git-lfs pointer). Git-lfs pointer
    /// files are tiny (~150 bytes) UTF-8 text starting with
    /// <c>version https://git-lfs.github.com/spec/v1</c>; if we tried to
    /// decode that as a CR2 we'd get a misleading import failure. The CR2
    /// fixture is ~19 MB so a length cutoff is sufficient.</summary>
    private static bool IsFixtureUsable(string path)
    {
        if (!File.Exists(path)) return false;
        return new FileInfo(path).Length > 4096;
    }

    [Fact]
    public async Task Cr2_OpensViaImageTryReadImageFile_WithMatrixAndRgbRender()
    {
        var path = FixturePath;
        if (!IsFixtureUsable(path))
        {
            Assert.Skip($"CR2 fixture not present or LFS pointer at {path}. " +
                "Run `git lfs pull --include=\"*.CR2\"` to fetch.");
            return;
        }
        var ct = TestContext.Current.CancellationToken;

        // Pre-load the spectral database before the import so the SASP path is
        // available. In a real session this is loaded by SPCC startup — but in
        // a test we drive it explicitly to confirm dispatch order: spectral
        // wins when available, dcraw fills in for models SASP doesn't cover.
        await FilterCurveDatabase.LoadAsync(ct);

        // --- 1. Production import path ----------------------------------
        var ok = Image.TryReadImageFile(path, out var mosaicImage);
        ok.ShouldBeTrue("Image.TryReadImageFile should handle .cr2 via FC.SDK.Raw");
        mosaicImage.ShouldNotBeNull();

        // Image carries the Bayer mosaic as a single float channel here —
        // deliberately not pre-debayered, so drizzle / Bayer-aware stacking
        // workflows that need the mosaic can still get it. Color rendering
        // is a downstream concern (DebayerAsync below).
        var (channels, w, h) = mosaicImage.Shape;
        channels.ShouldBe(1, "CR2 import returns the Bayer mosaic as a 1-channel float Image");
        w.ShouldBe(5568);
        h.ShouldBe(3708);
        // SensorType.RGGB tells DebayerAsync which 2x2 pattern to apply. TianWen
        // only enumerates RGGB; other Bayer variants (BGGR/GBRG/GRBG) would
        // map via BayerOffsetX/Y but the CR2 import currently rejects them
        // and falls through to Magick.NET. Our fixture is RGGB so this is the
        // expected value.
        mosaicImage.ImageMeta.SensorType.ShouldBe(SensorType.RGGB);
        mosaicImage.ImageMeta.Instrument.ShouldBe("Canon EOS 6D");

        // For the EOS 6D (not in SASP's 12 Canon bodies), the dcraw fallback
        // in CanonCameraProfiles must populate the matrix. The exact dispatch
        // order is: spectral (SASP) when FilterCurveDatabase has the model,
        // else dcraw factory matrix, else null. Verified for both branches
        // in CameraColorMatrixTests; here we confirm the import wiring
        // actually flows the result onto ImageMeta.
        mosaicImage.ImageMeta.CameraToSrgbMatrix.ShouldNotBeNull(
            "EOS 6D is in CanonCameraProfiles, so the dcraw fallback fires when SASP misses");
        mosaicImage.ImageMeta.CameraToSrgbMatrix!.Length.ShouldBe(9);
        output.WriteLine($"Loaded {w}x{h} {mosaicImage.ImageMeta.Instrument} CR2 with matrix:");
        LogMatrix(output, mosaicImage.ImageMeta.CameraToSrgbMatrix!);

        // --- 2. Debayer via TianWen's existing AHD path -----------------
        var rgbImage = await mosaicImage.DebayerAsync(DebayerAlgorithm.AHD, cancellationToken: ct);
        var (rgbChannels, rgbW, rgbH) = rgbImage.Shape;
        rgbChannels.ShouldBe(3);
        rgbW.ShouldBe(w);
        rgbH.ShouldBe(h);

        // The matrix survives the `imageMeta with { SensorType = Color }`
        // copy that DebayerAsync does — important for Phase 3 wiring.
        rgbImage.ImageMeta.CameraToSrgbMatrix.ShouldNotBeNull(
            "DebayerAsync's `imageMeta with` must preserve CameraToSrgbMatrix");

        // --- 3. Render two PNGs (with/without matrix) for visual diff ---
        var outDir = CreateTestOutputDir(nameof(Cr2_OpensViaImageTryReadImageFile_WithMatrixAndRgbRender));

        var noMatrixPng = Path.Combine(outDir, "raw_debayered.png");
        RenderRgbToPng(rgbImage, noMatrixPng, applyMatrix: null);
        output.WriteLine($"Without matrix: {noMatrixPng}");

        var matrixPng = Path.Combine(outDir, "raw_debayered_matrix.png");
        RenderRgbToPng(rgbImage, matrixPng, applyMatrix: rgbImage.ImageMeta.CameraToSrgbMatrix);
        output.WriteLine($"With matrix:    {matrixPng}");

        // CI sanity: both PNGs exist and are non-trivial. Per-pixel correctness
        // of the with-matrix render is verified by visual inspection / the
        // test-image-diff skill — there's no algorithmic ground truth to
        // assert against at the file level.
        new FileInfo(noMatrixPng).Length.ShouldBeGreaterThan(10_000);
        new FileInfo(matrixPng).Length.ShouldBeGreaterThan(10_000);
    }

    [Fact]
    public void Cr2_ImportPopulatesCameraToSrgbMatrix_Eos6DMatchesDcrawHandComputed()
    {
        // Spot-check: the matrix populated on the Image must equal the
        // dcraw factory matrix for EOS 6D (which we already validate at the
        // FC.SDK.Raw level in CanonCameraProfilesTests). This keeps the
        // import wiring honest — if someone accidentally swaps the spectral
        // / dcraw branches, this test catches it for the 6D case.
        var path = FixturePath;
        if (!IsFixtureUsable(path))
        {
            Assert.Skip($"CR2 fixture not present or LFS pointer at {path}. " +
                "Run `git lfs pull --include=\"*.CR2\"` to fetch.");
            return;
        }

        // NB: deliberately NOT loading FilterCurveDatabase here — the dcraw
        // fallback must work even without spectral data loaded.
        Image.TryReadImageFile(path, out var image).ShouldBeTrue();
        var matrix = image!.ImageMeta.CameraToSrgbMatrix;
        matrix.ShouldNotBeNull();

        // Reference values from CanonCameraProfilesTests.ComputeRgbCam_Eos6D_MatchesHandComputedValues
        // — same matrix the FC.SDK.Raw test asserts against. Tolerance 0.01
        // absorbs the float-vs-double round-trip through ComputeRgbCam.
        var expected = new[]
        {
             1.913f, -1.060f,  0.147f,
            -0.225f,  1.648f, -0.422f,
             0.010f, -0.510f,  1.500f,
        };
        for (var i = 0; i < 9; i++)
            matrix![i].ShouldBe(expected[i], tolerance: 0.01f, $"matrix[{i}]");
    }

    /// <summary>Renders a 3-channel float <see cref="Image"/> to PNG with
    /// optional camera-to-sRGB matrix application, joint auto-stretch by the
    /// global max, and sRGB gamma encode. Mirrors FC.SDK.Raw's
    /// <c>CanonDemosaic.Finalize</c> pipeline — kept here as a temporary
    /// stand-in for the Phase 3 render path that will do the same via
    /// <c>StretchUniforms</c>.</summary>
    private static void RenderRgbToPng(Image rgbImage, string outPath, float[]? applyMatrix)
    {
        var (_, w, h) = rgbImage.Shape;
        var rSpan = rgbImage.GetChannelSpan(0);
        var gSpan = rgbImage.GetChannelSpan(1);
        var bSpan = rgbImage.GetChannelSpan(2);
        var pixels = w * h;

        // 1. Copy channels into a working buffer; apply matrix if provided.
        //    The matrix transform mixes the three channels (camera-RGB ->
        //    sRGB primaries) — channel-planar to interleaved layout helps
        //    the inner loop stay cache-friendly.
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

        // 2. Joint auto-stretch by global max — single divisor across all three
        //    channels so WB / matrix ratios survive and the brightest pixel
        //    lands at 1.0. Without this, WB-amplified highlights clip on the
        //    ushort conversion below.
        var max = 0f;
        for (var i = 0; i < working.Length; i++) if (working[i] > max) max = working[i];
        if (max < 1e-6f) max = 1f;

        // 3. sRGB gamma encode + 8-bit quantise to RGBA. Standard sRGB
        //    transfer function (IEC 61966-2-1) — what PngWriter / browsers
        //    / Affinity all assume on unmanaged 8-bit images.
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

    private static void LogMatrix(ITestOutputHelper output, float[] m)
    {
        output.WriteLine($"  [{m[0],7:F4}  {m[1],7:F4}  {m[2],7:F4}]");
        output.WriteLine($"  [{m[3],7:F4}  {m[4],7:F4}  {m[5],7:F4}]");
        output.WriteLine($"  [{m[6],7:F4}  {m[7],7:F4}  {m[8],7:F4}]");
    }

    private static string CreateTestOutputDir(string testName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests",
            DateTime.Now.ToString("yyyyMMdd"), testName);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
