using ImageMagick;
using Shouldly;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end tests for the new stretch pipeline (star-masked stats + iterative convergence
/// + Tycho-2/SPCC WB + background neutralization + Fritsch-Carlson curve LUT + HDR knee).
///
/// Drives the pipeline through <see cref="AstroImageDocument.ComputeStretchUniforms"/> and
/// <see cref="Image.RenderStretchedRgba"/> — the CPU mirror of the GLSL fragment shader.
/// Writes a TIFF per case to the temp test output dir so the visual result of each
/// pipeline feature can be inspected.
/// </summary>
[Collection("Imaging")]
public class StretchTests_NewPipeline(ITestOutputHelper output)
{
    private const string Fixture = "Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop";

    [Theory]
    // mode    , wb   , bgNeut, conv , curvesMode, hdrAmount, label
    [InlineData("linked", false, false, false, 0, 0f, "01_baseline")]
    [InlineData("linked", true,  false, false, 0, 0f, "02_wb")]
    [InlineData("linked", false, true,  false, 0, 0f, "03_bgneut")]
    [InlineData("linked", true,  true,  false, 0, 0f, "04_wb_bgneut")]
    [InlineData("linked", true,  true,  true,  0, 0f, "05_wb_bgneut_converged")]
    [InlineData("linked", true,  true,  true,  1, 0f, "06_wb_bgneut_converged_curveLut")]
    [InlineData("linked", true,  true,  true,  1, 0.8f, "07_wb_bgneut_converged_curveLut_hdr")]
    [InlineData("luma",   true,  false, false, 0, 0f, "08_luma_wb")]
    public async Task GivenColorFitsWhenRenderingThroughCpuPipelineThenWritesTiff(
        string mode, bool applyWb, bool applyBgNeut, bool useConvergence,
        int curvesMode, float hdrAmount, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        var fitsImage = await SharedTestData.ExtractGZippedFitsImageAsync(Fixture, cancellationToken: ct);
        var doc = await AstroImageDocument.CreateFromImageAsync(fitsImage, DebayerAlgorithm.None, cancellationToken: ct);

        // Star detection populates StarMaskedStats and a star-masked PerChannelBackground —
        // both required for convergence + background neutralization to behave realistically.
        await doc.DetectStarsAsync(ct);

        var img = doc.UnstretchedImage;
        output.WriteLine($"Image: {img.Width}x{img.Height}x{img.ChannelCount}  stars={doc.Stars?.Count ?? 0}  HFR={doc.AverageHFR:F2}");
        output.WriteLine($"PerChannelBg: R={doc.PerChannelBackground[0]:F4} G={doc.PerChannelBackground[1]:F4} B={doc.PerChannelBackground[2]:F4}");

        if (useConvergence)
        {
            doc.UseIterativeConvergence = true;
        }

        var stretchMode = mode == "luma" ? StretchMode.Luma : StretchMode.Linked;
        var uniforms = doc.ComputeStretchUniforms(stretchMode, new StretchParameters(0.15, -3));

        if (applyWb)
        {
            // Synthetic WB: boost red+blue, leave green as anchor — the absolute values don't
            // matter; the test just needs a non-identity multiplier so the WB code path runs.
            uniforms = uniforms with { WhiteBalance = (1.4f, 1.0f, 1.2f) };
        }
        if (applyBgNeut)
        {
            // Report the real gains for diagnostic, but apply synthetic non-identity gains —
            // the Vela fixture has uniform per-channel background so ComputeGains returns
            // (1,1,1). The test wants to exercise the code path with a visible effect.
            var realGains = BackgroundNeutralization.ComputeGains(doc.PerChannelBackground);
            output.WriteLine($"BG-neut gains (computed): R={realGains.R:F3} G={realGains.G:F3} B={realGains.B:F3}");
            var syntheticGains = (R: 0.85f, G: 1.05f, B: 0.95f);
            uniforms = uniforms with { BackgroundNeutralization = syntheticGains };
            output.WriteLine($"BG-neut gains (applied):  R={syntheticGains.R:F3} G={syntheticGains.G:F3} B={syntheticGains.B:F3}");
        }

        ImmutableArray<float> curveKnots = default;
        if (curvesMode == 1)
        {
            // Same S-curve preset the viewer uses (Shift+B)
            var spline = new FritschCarlsonSpline(
                [(0f, 0f), (0.15f, 0.22f), (0.4f, 0.5f), (0.7f, 0.72f), (1f, 1f)]);
            curveKnots = spline.ComputeKnots33();
        }

        output.WriteLine($"Mode={uniforms.Mode}  NormFactor={uniforms.NormFactor:F4}");
        output.WriteLine($"  WB       = {Triple(uniforms.WhiteBalance)}");
        output.WriteLine($"  BgNeut   = {Triple(uniforms.BackgroundNeutralization)}");
        output.WriteLine($"  Pedestal = {Triple(uniforms.Pedestal)}");
        output.WriteLine($"  Shadows  = {Triple(uniforms.Shadows)}");
        output.WriteLine($"  Midtones = {Triple(uniforms.Midtones)}");
        output.WriteLine($"  Rescale  = {Triple(uniforms.Rescale)}");

        var rgba = new byte[img.Width * img.Height * 4];
        var sw = Stopwatch.StartNew();
        img.RenderStretchedRgba(
            uniforms,
            rgba,
            curvesMode: curvesMode,
            curveLut: curveKnots.IsDefault ? default : curveKnots.AsSpan(),
            hdrAmount: hdrAmount);
        sw.Stop();
        output.WriteLine($"RenderStretchedRgba ({img.Width}x{img.Height}): {sw.Elapsed}");

        // Sanity: bytes should vary across the image. Not all-zero (broken pipeline produces no
        // signal) and not all-255 (broken clamp). Brightness varies wildly across legitimate
        // stretches (convergence on this fixture is dark by design — midtones -> 0.9996),
        // so the assertion can't be "must be bright"; it's "must have signal".
        byte minByte = 255, maxByte = 0;
        long sum = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var b = rgba[i + c];
                if (b < minByte) minByte = b;
                if (b > maxByte) maxByte = b;
                sum += b;
            }
        }
        var avg = sum / (double)(rgba.Length / 4 * 3);
        output.WriteLine($"RGB byte range: [{minByte}, {maxByte}]  mean: {avg:F2}");
        maxByte.ShouldBeGreaterThan((byte)0, "pipeline produced pure black — no signal");
        minByte.ShouldBeLessThan((byte)255, "pipeline produced pure white — clamp broken or all pixels saturated");
        (maxByte - minByte).ShouldBeGreaterThan(10, "RGB output should have some dynamic range");

        // Wrap RGBA bytes as a MagickImage and write TIFF for visual inspection.
        var settings = new PixelReadSettings((uint)img.Width, (uint)img.Height, StorageType.Char, PixelMapping.RGBA);
        using var magick = new MagickImage(rgba, settings);
        magick.Settings.Compression = CompressionMethod.Zip;

        var testDir = SharedTestData.CreateTempTestOutputDir(
            TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTests_NewPipeline));
        var outPath = Path.Combine(testDir, $"{Fixture}_{label}.tiff");
        var bytes = magick.ToByteArray(MagickFormat.Tiff);
        await File.WriteAllBytesAsync(outPath, bytes, ct);
        output.WriteLine($"Wrote {bytes.Length} bytes -> {outPath}");
    }

    private static string Triple((float R, float G, float B) v) => $"R={v.R:F4} G={v.G:F4} B={v.B:F4}";
}
