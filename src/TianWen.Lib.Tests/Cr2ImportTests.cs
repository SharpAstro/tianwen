using FC.SDK.Raw;
using SharpAstro.Png;
using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end Canon CR2 -> TianWen Image pipeline visualisation. Loads the
/// LFS-tracked moon CR2 fixture, runs it through the same Bayer-mosaic-aware
/// path the production import will use in Phase 2b
/// (<see cref="CanonRaw.Open"/> → <see cref="CanonRaw.PreprocessMosaic"/> → 1-channel
/// float <see cref="Image"/> with <see cref="SensorType.RGGB"/> →
/// <see cref="Image.DebayerAsync"/>), then renders two PNGs:
///
/// <list type="bullet">
/// <item><b>raw_debayered.png</b> — debayered RGB with auto-stretch + sRGB
/// gamma, NO colour matrix applied. The "what TianWen sees today" baseline.</item>
/// <item><b>raw_debayered_matrix.png</b> — same pipeline but with the
/// <see cref="ImageMeta.CameraToSrgbMatrix"/> applied between debayer and
/// auto-stretch. The "what TianWen will see when Phase 3 lands" preview.</item>
/// </list>
///
/// The visual diff between the two confirms the spectral matrix derived from
/// SASP curves (Phase 1) actually does something useful when wired in. CI
/// asserts plausible per-channel signal; the user inspects the PNGs.
///
/// Fixture lives at <c>Data/CR2/_MG_7578.CR2</c>, LFS-tracked. Tests skip
/// gracefully when the file is missing (clones without <c>git lfs pull</c>).
/// </summary>
public class Cr2ImportTests(ITestOutputHelper output)
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Data", "CR2", "_MG_7578.CR2");

    [Fact]
    public async Task Cr2_RoundTripsThroughTianWenImagePipeline_WithAndWithoutMatrix()
    {
        var path = FixturePath;
        if (!File.Exists(path))
        {
            Assert.Skip($"CR2 fixture not present at {path}. " +
                "Run `git lfs pull` to fetch.");
            return;
        }
        var ct = TestContext.Current.CancellationToken;

        // --- 1. Load the CR2 via FC.SDK.Raw ----------------------------------
        var raw = CanonRaw.Open(path);
        output.WriteLine($"Loaded CR2: {raw.Width}x{raw.Height} {raw.BitDepth}-bit, model={raw.Exif?.Model ?? "?"}, CFA={raw.CfaPattern}");

        // --- 2. Black-subtract + WB into a float[] -------------------------
        var wbMosaic = CanonRaw.PreprocessMosaic(raw);
        wbMosaic.Length.ShouldBe(raw.Width * raw.Height);

        // --- 3. Wrap into a 1-channel float Image with SensorType.RGGB ---
        // Image expects float[][,] (channel-planar, 2D each). The mosaic is
        // row-major flat; reshape once.
        var channel = new float[raw.Height, raw.Width];
        for (var y = 0; y < raw.Height; y++)
        for (var x = 0; x < raw.Width; x++)
            channel[y, x] = wbMosaic[y * raw.Width + x];

        // TianWen's SensorType only enumerates RGGB; the other 2x2 Bayer
        // patterns (BGGR/GBRG/GRBG) are represented as RGGB + BayerOffsetX/Y.
        // Our moon fixture is RGGB so the offset is (0, 0); a real CR2
        // importer (Phase 2b) would compute the offsets per CFA variant.
        if (raw.CfaPattern != CanonCfaPattern.Rggb)
            throw new NotSupportedException(
                $"Phase 2a test only handles RGGB; got {raw.CfaPattern}. Phase 2b will add BayerOffset mapping.");
        var sensorType = SensorType.RGGB;

        // --- 4. Resolve camera->sRGB matrix: try spectral (SASP) first,
        //        fall back to dcraw factory table via FC.SDK.Raw's
        //        CanonCameraProfiles. This is the exact dispatch order
        //        Phase 2b will wire into Image.Import.cs.
        await FilterCurveDatabase.LoadAsync(ct);
        float[]? matrix = null;
        string? matrixSource = null;
        if (FilterCurveDatabase.TryComputeCameraToSrgbMatrix(raw.Exif?.Model ?? "", out var spectralMatrix))
        {
            matrix = spectralMatrix;
            matrixSource = "spectral (SASP)";
        }
        else if (CanonCameraProfiles.ResolveProfile(raw.Exif?.Model)?.ComputeRgbCam() is { } dcrawMatrix)
        {
            matrix = dcrawMatrix;
            matrixSource = "dcraw (CanonCameraProfiles)";
        }
        if (matrix is not null)
        {
            output.WriteLine($"Matrix source: {matrixSource} for '{raw.Exif?.Model}'");
            LogMatrix(output, matrix);
        }
        else
        {
            output.WriteLine($"No matrix available for '{raw.Exif?.Model}' (neither SASP spectral nor dcraw factory).");
        }

        var meta = MakeMosaicMeta(raw, sensorType, matrix);
        // MaxValue=2 covers the WB-amplified mosaic range; daylight WB peaks
        // around 1.4 (B channel) so 2 leaves comfortable headroom for Debayer
        // to interpolate without clipping. Pedestal=0 because PreprocessMosaic
        // already subtracted the camera black.
        var mosaicImage = new Image([channel], BitDepth.Float32,
            maxValue: 2f, minValue: 0f, pedestal: 0f, meta);

        // --- 5. Debayer (AHD) ----------------------------------------------
        var rgbImage = await mosaicImage.DebayerAsync(DebayerAlgorithm.AHD, cancellationToken: ct);
        var (channels, w, h) = rgbImage.Shape;
        channels.ShouldBe(3);
        w.ShouldBe(raw.Width);
        h.ShouldBe(raw.Height);

        // --- 6. Render both versions to PNG --------------------------------
        var outDir = CreateTestOutputDir(nameof(Cr2_RoundTripsThroughTianWenImagePipeline_WithAndWithoutMatrix));

        var noMatrixPng = Path.Combine(outDir, "raw_debayered.png");
        RenderRgbToPng(rgbImage, noMatrixPng, applyMatrix: null);
        output.WriteLine($"Without matrix: {noMatrixPng}");

        if (matrix is not null)
        {
            var matrixPng = Path.Combine(outDir, "raw_debayered_matrix.png");
            RenderRgbToPng(rgbImage, matrixPng, applyMatrix: matrix);
            output.WriteLine($"With matrix ({matrixSource}): {matrixPng}");
        }

        // CI sanity: the no-matrix PNG exists and is non-trivial.
        new FileInfo(noMatrixPng).Length.ShouldBeGreaterThan(10_000);
    }

    /// <summary>Build an <see cref="ImageMeta"/> for the mosaic stage. Most
    /// fields are placeholders since FC.SDK.Raw only provides a subset of
    /// EXIF; the Phase 2b production importer will populate more.</summary>
    private static ImageMeta MakeMosaicMeta(CanonRawFile raw, SensorType sensorType, float[]? matrix)
    {
        var captureTime = raw.Exif?.CaptureTime is { } ct
            ? new DateTimeOffset(DateTime.SpecifyKind(ct, DateTimeKind.Utc), TimeSpan.Zero)
            : DateTimeOffset.UnixEpoch;
        var exposure = raw.Exif?.ExposureTime is { } et && et.Denominator != 0
            ? TimeSpan.FromSeconds((double)et.Numerator / et.Denominator)
            : TimeSpan.Zero;
        return new ImageMeta(
            Instrument: raw.Exif?.Model ?? "Unknown Canon",
            ExposureStartTime: captureTime,
            ExposureDuration: exposure,
            FrameType: FrameType.Light,
            Telescope: "",
            PixelSizeX: 0, PixelSizeY: 0,
            FocalLength: -1, FocusPos: -1,
            Filter: Filter.Unknown,
            BinX: 1, BinY: 1,
            CCDTemperature: float.NaN,
            SensorType: sensorType,
            BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN, Longitude: float.NaN
        ) { CameraToSrgbMatrix = matrix };
    }

    /// <summary>Renders a 3-channel float <see cref="Image"/> to PNG with
    /// optional camera-to-sRGB matrix application, joint auto-stretch by the
    /// global max, and sRGB gamma encode. Mirrors the pipeline FC.SDK.Raw's
    /// <c>CanonDemosaic.Finalize</c> uses, so the visual output is comparable
    /// (with the bonus that here the matrix is the SASP-derived spectral
    /// matrix instead of dcraw's factory table).</summary>
    private static void RenderRgbToPng(Image rgbImage, string outPath, float[]? applyMatrix)
    {
        var (_, w, h) = rgbImage.Shape;
        var rSpan = rgbImage.GetChannelSpan(0);
        var gSpan = rgbImage.GetChannelSpan(1);
        var bSpan = rgbImage.GetChannelSpan(2);
        var pixels = w * h;

        // 1. Copy channels into a working buffer; apply matrix if provided.
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

        // 2. Joint auto-stretch by global max (preserves matrix/WB ratios).
        var max = 0f;
        for (var i = 0; i < working.Length; i++) if (working[i] > max) max = working[i];
        if (max < 1e-6f) max = 1f;

        // 3. sRGB gamma + 8-bit quantise.
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
