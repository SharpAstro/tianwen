using DIR.Lib;
using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Tests.Helpers;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Shared test-driver for the four mono/color/debayered/luma stretch matrices
/// (<see cref="StretchTests_MonoImage"/>, <see cref="StretchTests_DebayeredImageUnlinked"/>,
/// <see cref="StretchTests_ColorImagelinked"/>, <see cref="StretchTests_ColorImageLuma"/>).
///
/// Drives the production stretch pipeline end-to-end:
/// FITS → <see cref="AstroImageDocument.CreateFromImageAsync"/> → star detection →
/// <see cref="AstroImageDocument.ComputeStretchUniforms"/> →
/// <see cref="Image.RenderStretchedRgba"/> → RGBA8 byte buffer → PNG on disk for
/// visual inspection. Asserts per-channel byte-level signal so per-channel
/// regressions (WB/shadow coordinate-space drift, MAD floor bugs, degenerate
/// convergence) show up immediately.
/// </summary>
public abstract class StretchTestBase(ITestOutputHelper testOutputHelper)
{
    internal Task StretchTest(string fileName, DebayerAlgorithm algorithm, int stretchPct, int clippingSigma, bool linked, uint expectedChannelCount)
        => StretchTest(fileName, algorithm, stretchPct, clippingSigma, linked ? "linked" : "unlinked", expectedChannelCount);

    internal async Task StretchTest(string fileName, DebayerAlgorithm algorithm, int stretchPct, int clippingSigma, string mode, uint expectedChannelCount)
    {
        // ---------- given ----------
        var ct = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(fileName, cancellationToken: ct);

        var namePrefix = $"{fileName}_{algorithm}_f{stretchPct}_s{clippingSigma}_{mode}";
        var testDir = SharedTestData.CreateTempTestOutputDir(TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTest));

        testOutputHelper.WriteLine($"Input: {image.Width}x{image.Height}x{image.ChannelCount}, MaxValue={image.MaxValue:F4}, MinValue={image.MinValue:F4}, BitDepth={image.BitDepth}, SensorType={image.ImageMeta.SensorType}");
        for (var c = 0; c < image.ChannelCount; c++)
        {
            var (ped, med, mad) = image.GetPedestralMedianAndMADScaledToUnit(c);
            testOutputHelper.WriteLine($"  Ch{c}: pedestal={ped:F6}, median={med:F6}, mad={mad:F6}");
            var (shadows, midtones, highlights, rescale) = Image.ComputeStretchParameters(med, mad, stretchPct * 0.01d, clippingSigma);
            testOutputHelper.WriteLine($"  Ch{c} stretch params: shadows={shadows:F6}, midtones={midtones:F6}, highlights={highlights:F6}, rescale={rescale:F6}");
        }
        if (mode == "luma" && image.ChannelCount >= 3)
        {
            var (lumaPed, lumaMed, lumaMad) = await image.GetLumaStretchStatsAsync(algorithm, ct);
            testOutputHelper.WriteLine($"  Luma: pedestal={lumaPed:F6}, median={lumaMed:F6}, mad={lumaMad:F6}");
            var (ls, lm, lh, lr) = Image.ComputeStretchParameters(lumaMed, lumaMad, stretchPct * 0.01d, clippingSigma);
            testOutputHelper.WriteLine($"  Luma stretch params: shadows={ls:F6}, midtones={lm:F6}, highlights={lh:F6}, rescale={lr:F6}");
        }

        var stretchMode = mode switch
        {
            "linked" => StretchMode.Linked,
            "luma" => StretchMode.Luma,
            _ => StretchMode.Unlinked,
        };

        // ---------- when ----------
        var sw = Stopwatch.StartNew();
        // Debayer first (no-op for mono / 3-channel images) so the document is built on the
        // same 1- or 3-channel image we render against. AstroImageDocument.CreateFromImageAsync
        // would otherwise keep raw Bayer (1-channel mosaic) and compute stats from the mosaic
        // histogram — those uniforms don't match a CPU-debayered 3-channel render.
        var renderImage = await image.DebayerAsync(algorithm, cancellationToken: ct);
        ((uint)renderImage.ChannelCount).ShouldBe(expectedChannelCount, "renderImage channel count after debayer");

        var doc = await AstroImageDocument.CreateFromImageAsync(renderImage, DebayerAlgorithm.None, cancellationToken: ct);
        // Star detection populates the star mask and a star-masked PerChannelBackground —
        // required so background neutralisation / converged stretches behave realistically.
        await doc.DetectStarsAsync(ct);

        var uniforms = doc.ComputeStretchUniforms(stretchMode, new StretchParameters(stretchPct * 0.01d, clippingSigma));
        testOutputHelper.WriteLine($"  Uniforms: Mode={uniforms.Mode}  NormFactor={uniforms.NormFactor:F4}");
        testOutputHelper.WriteLine($"    Pedestal=({uniforms.Pedestal.R:F4},{uniforms.Pedestal.G:F4},{uniforms.Pedestal.B:F4})");
        testOutputHelper.WriteLine($"    Shadows =({uniforms.Shadows.R:F4},{uniforms.Shadows.G:F4},{uniforms.Shadows.B:F4})");
        testOutputHelper.WriteLine($"    Midtones=({uniforms.Midtones.R:F4},{uniforms.Midtones.G:F4},{uniforms.Midtones.B:F4})");
        testOutputHelper.WriteLine($"    Rescale =({uniforms.Rescale.R:F4},{uniforms.Rescale.G:F4},{uniforms.Rescale.B:F4})");

        var rgba = new byte[renderImage.Width * renderImage.Height * 4];
        renderImage.RenderStretchedRgba(uniforms, rgba);
        sw.Stop();
        testOutputHelper.WriteLine($"Stretch+render ({mode}) at {stretchPct}% via {algorithm} took: {sw.Elapsed}");

        // Sample center + corner pixel for the test log so a regression author can correlate
        // visual results with byte values quickly.
        var cx = renderImage.Width / 2;
        var cy = renderImage.Height / 2;
        var centerIdx = (cy * renderImage.Width + cx) * 4;
        var cornerIdx = (10 * renderImage.Width + 10) * 4;
        testOutputHelper.WriteLine($"  RGBA center [{cx},{cy}] = ({rgba[centerIdx]},{rgba[centerIdx + 1]},{rgba[centerIdx + 2]})");
        testOutputHelper.WriteLine($"  RGBA corner [10,10] = ({rgba[cornerIdx]},{rgba[cornerIdx + 1]},{rgba[cornerIdx + 2]})");

        // ---------- then ----------
        VerifyRgbaHasSignal(rgba, expectedChannelCount);

        // Save the rendered RGBA as a PNG for visual inspection. Lossless, browser-viewable,
        // ~order-of-magnitude smaller than the prior TIFF intermediate.
        var pngPath = Path.Combine(testDir, $"{namePrefix}.png");
        var pngBytes = DisplayImageWriter.EncodePng(rgba, renderImage.Width, renderImage.Height);
        await File.WriteAllBytesAsync(pngPath, pngBytes, ct);
        testOutputHelper.WriteLine($"Wrote {pngBytes.Length} bytes -> {pngPath}");
    }

    /// <summary>
    /// Verifies the RGBA8 buffer has per-channel signal — catches the same kinds of regressions
    /// the old VerifyStretchedFloatImageHasSignal did (per-channel collapse, all-zero output,
    /// all-saturated output), but on the production byte buffer that's actually displayed.
    ///
    /// For mono input (<paramref name="expectedChannelCount"/> == 1) <see cref="Image.RenderStretchedRgba"/>
    /// broadcasts to R == G == B so only one channel needs to be checked.
    /// </summary>
    private void VerifyRgbaHasSignal(ReadOnlySpan<byte> rgba, uint expectedChannelCount)
    {
        var pixelCount = rgba.Length / 4;
        pixelCount.ShouldBeGreaterThan(0);

        var checkChannels = expectedChannelCount >= 3 ? 3 : 1;
        Span<long> chSum = stackalloc long[3];
        Span<int> chMin = stackalloc int[3] { 255, 255, 255 };
        Span<int> chMax = stackalloc int[3] { 0, 0, 0 };
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var c = 0; c < checkChannels; c++)
            {
                int v = rgba[i + c];
                chSum[c] += v;
                if (v < chMin[c]) chMin[c] = v;
                if (v > chMax[c]) chMax[c] = v;
            }
        }

        for (var c = 0; c < checkChannels; c++)
        {
            var mean = chSum[c] / (double)pixelCount;
            testOutputHelper.WriteLine($"  Channel {c} byte range: [{chMin[c]}, {chMax[c]}]  mean: {mean:F2}");

            // Range > 12/255 ≈ 5% — same threshold the old float test used.
            (chMax[c] - chMin[c]).ShouldBeGreaterThan(12,
                $"channel {c} should have a stretch range >12/255 (got {chMax[c] - chMin[c]})");
            // Brightest pixel ≥ 64/255 ≈ 25% — same threshold as old test (max > 0.25f).
            chMax[c].ShouldBeGreaterThanOrEqualTo(64,
                $"channel {c} brightest pixel should reach ≥25% of byte range");
            // Mean strictly inside (0, 255) — catches all-zero / fully-saturated outputs.
            mean.ShouldBeInRange(0.25, 254.75,
                $"channel {c} mean should be in (0, 255) -- got {mean:F2}");
        }
    }
}
