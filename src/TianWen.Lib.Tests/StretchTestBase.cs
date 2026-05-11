using ImageMagick;
using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public abstract class StretchTestBase(ITestOutputHelper testOutputHelper)
{
    internal Task StretchTest(string fileName, DebayerAlgorithm algorithm, int stretchPct, int clippingSigma, bool linked, uint expectedChannelCount)
        => StretchTest(fileName, algorithm, stretchPct, clippingSigma, linked ? "linked" : "unlinked", expectedChannelCount);

    internal async Task StretchTest(string fileName, DebayerAlgorithm algorithm, int stretchPct, int clippingSigma, string mode, uint expectedChannelCount)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(fileName, cancellationToken: cancellationToken);

        var namePrefix = $"{fileName}_{algorithm}_f{stretchPct}_s{clippingSigma}_{mode}";
        var testDir = SharedTestData.CreateTempTestOutputDir(TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTest));

        // Pre-stretch diagnostics: show input image stats
        testOutputHelper.WriteLine($"Input: {image.Width}x{image.Height}x{image.ChannelCount}, MaxValue={image.MaxValue:F4}, MinValue={image.MinValue:F4}, BitDepth={image.BitDepth}");
        for (var c = 0; c < image.ChannelCount; c++)
        {
            var (ped, med, mad) = image.GetPedestralMedianAndMADScaledToUnit(c);
            testOutputHelper.WriteLine($"  Ch{c}: pedestal={ped:F6}, median={med:F6}, mad={mad:F6}");
            var (shadows, midtones, highlights, rescale) = Image.ComputeStretchParameters(med, mad, stretchPct * 0.01d, clippingSigma);
            testOutputHelper.WriteLine($"  Ch{c} stretch params: shadows={shadows:F6}, midtones={midtones:F6}, highlights={highlights:F6}, rescale={rescale:F6}");
        }
        if (mode == "luma" && image.ChannelCount >= 3)
        {
            var (lumaPed, lumaMed, lumaMad) = await image.GetLumaStretchStatsAsync(algorithm, cancellationToken);
            testOutputHelper.WriteLine($"  Luma: pedestal={lumaPed:F6}, median={lumaMed:F6}, mad={lumaMad:F6}");
            var (ls, lm, lh, lr) = Image.ComputeStretchParameters(lumaMed, lumaMad, stretchPct * 0.01d, clippingSigma);
            testOutputHelper.WriteLine($"  Luma stretch params: shadows={ls:F6}, midtones={lm:F6}, highlights={lh:F6}, rescale={lr:F6}");
        }

        // when
        var sw = Stopwatch.StartNew();
        var stretched = mode switch
        {
            "linked" => await image.StretchLinkedAsync(stretchPct * 0.01d, clippingSigma, debayerAlgorithm: algorithm, cancellationToken: cancellationToken),
            "luma" => await image.StretchLumaAsync(stretchPct * 0.01d, clippingSigma, debayerAlgorithm: algorithm, cancellationToken: cancellationToken),
            _ => await image.StretchUnlinkedAsync(stretchPct * 0.01d, clippingSigma, debayerAlgorithm: algorithm, cancellationToken: cancellationToken),
        };
        sw.Stop();
        testOutputHelper.WriteLine($"Debayering and stretching ({mode}) to {stretchPct}% using {algorithm} took: {sw.Elapsed}");

        // Diagnostic: show stretched image stats
        testOutputHelper.WriteLine($"Stretched: {stretched.Width}x{stretched.Height}x{stretched.ChannelCount}, MaxValue={stretched.MaxValue:F4}, BitDepth={stretched.BitDepth}");

        // Sample pixels from the stretched image: center and corner (likely background)
        var cx = stretched.Width / 2;
        var cy = stretched.Height / 2;
        var cornerX = 10;
        var cornerY = 10;
        for (var c = 0; c < stretched.ChannelCount; c++)
        {
            testOutputHelper.WriteLine($"  Stretched center [{cx},{cy}] ch{c} = {stretched[c, cy, cx]:F6}");
            testOutputHelper.WriteLine($"  Stretched corner [{cornerX},{cornerY}] ch{c} = {stretched[c, cornerY, cornerX]:F6}");
        }

        // Verify stretched image is normalized
        stretched.MaxValue.ShouldBe(1.0f, $"Stretched image MaxValue should be 1.0 but was {stretched.MaxValue}");

        // then
        sw.Restart();
        var magick = await stretched.ToMagickImageAsync(DebayerAlgorithm.None, cancellationToken);
        sw.Stop();
        testOutputHelper.WriteLine($"Converting stretched image to magick image took: {sw.Elapsed}");
        testOutputHelper.WriteLine($"Magick Quantum.Max = {Quantum.Max}");

        magick.ShouldNotBeNull();
        magick.Width.ShouldBe((uint)image.Width);
        magick.Height.ShouldBe((uint)image.Height);
        magick.ChannelCount.ShouldBe(expectedChannelCount);

        // Diagnostic: read back the same pixel from Magick and compare
        using (var pixels = magick.GetPixelsUnsafe())
        {
            var magickPixel = pixels.GetPixel(cx, cy);
            for (var c = 0; c < magickPixel.Channels; c++)
            {
                var magickVal = magickPixel.GetChannel((uint)c);
                var stretchedVal = stretched[c, cy, cx];
                var expected = stretchedVal * Quantum.Max;
                testOutputHelper.WriteLine($"  Magick pixel [{cx},{cy}] ch{c} = {magickVal:F2} (expected {expected:F2}, stretched={stretchedVal:F6}, ratio={magickVal / expected:F4})");
            }
        }

        // Verify the stretched float Image itself: bg should not collapse to zero, brightest
        // pixels should have lifted off the floor, and no channel can stay flat.
        VerifyStretchedFloatImageHasSignal(stretched, expectedChannelCount);

        // Save stretched image as-is (before AutoLevel)
        sw.Restart();
        var stretchedTiffBytes = magick.ToByteArray(MagickFormat.Tiff);
        sw.Stop();
        testOutputHelper.WriteLine($"Converting stretched image to TIFF bytes took: {sw.Elapsed}");
        await File.WriteAllBytesAsync(Path.Combine(testDir, $"{namePrefix}_stretched.tiff"), stretchedTiffBytes, cancellationToken);

        // Also save auto-leveled version for comparison
        sw.Restart();
        magick.AutoLevel();
        sw.Stop();
        testOutputHelper.WriteLine($"Auto-levelling took: {sw.Elapsed}");

        sw.Restart();
        var autoLevelTiffBytes = magick.ToByteArray(MagickFormat.Tiff);
        sw.Stop();
        testOutputHelper.WriteLine($"Converting magick image to TIFF bytes took: {sw.Elapsed}");
        await File.WriteAllBytesAsync(Path.Combine(testDir, $"{namePrefix}_autoLevel.tiff"), autoLevelTiffBytes, cancellationToken);

        // Verify the auto-levelled MagickImage: full quantum range used, mean lifted away from
        // either extreme. AutoLevel rescales to fill the dynamic range so it acts as a sanity
        // check that the stretched image had real per-channel variation.
        VerifyAutoLevelledMagickHasSignal(magick, expectedChannelCount);
    }

    /// <summary>
    /// Verifies the stretched <see cref="Image"/> has signal: every channel's range non-zero,
    /// mean strictly inside (0, MaxValue), and at least some pixels reach > 25% of max. Catches
    /// regressions where a stretch collapses a channel to a single value (per-channel WB/shadow
    /// mismatch, degenerate convergence, MAD floor bug, etc).
    /// </summary>
    private void VerifyStretchedFloatImageHasSignal(Image stretched, uint expectedChannelCount)
    {
        var (channelCount, width, height) = stretched.Shape;
        ((uint)channelCount).ShouldBe(expectedChannelCount);
        for (var c = 0; c < channelCount; c++)
        {
            var span = stretched.GetChannelSpan(c);
            float min = float.MaxValue, max = float.MinValue;
            double sum = 0;
            var sampleCount = 0;
            for (var i = 0; i < span.Length; i++)
            {
                var v = span[i];
                if (float.IsNaN(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
                sampleCount++;
            }
            sampleCount.ShouldBeGreaterThan(0, $"channel {c} should have non-NaN pixels");
            var mean = sum / sampleCount;
            testOutputHelper.WriteLine($"  Channel {c} float range: [{min:F4}, {max:F4}]  mean: {mean:F4}");

            (max - min).ShouldBeGreaterThan(0.05f, $"channel {c} should have a stretch range >5% of unit (got {(max - min):F4})");
            max.ShouldBeGreaterThan(0.25f, $"channel {c} brightest pixel should reach >25% of max after stretch");
            mean.ShouldBeInRange(0.001, 0.999, $"channel {c} mean should be in (0, 1) -- got {mean:F4}");
        }
    }

    /// <summary>
    /// Verifies the auto-levelled <see cref="MagickImage"/> uses the full quantum range and has
    /// a sensible mean. AutoLevel guarantees the rescaled image spans [0, Quantum.Max], so this
    /// is a sanity check on the post-stretch pixel distribution rather than the stretch itself.
    /// </summary>
    private void VerifyAutoLevelledMagickHasSignal(IMagickImage<float> magick, uint expectedChannelCount)
    {
        magick.ChannelCount.ShouldBe(expectedChannelCount);
        using var pixels = magick.GetPixelsUnsafe();
        var w = magick.Width;
        var h = magick.Height;
        var ch = (int)Math.Min(magick.ChannelCount, 3u);
        Span<double> chSum = stackalloc double[3];
        Span<float> chMin = stackalloc float[3] { float.MaxValue, float.MaxValue, float.MaxValue };
        Span<float> chMax = stackalloc float[3] { float.MinValue, float.MinValue, float.MinValue };
        var pixelCount = (long)w * h;
        for (uint y = 0; y < h; y++)
        {
            for (uint x = 0; x < w; x++)
            {
                var px = pixels.GetPixel((int)x, (int)y);
                for (var c = 0; c < ch; c++)
                {
                    var v = px.GetChannel((uint)c);
                    chSum[c] += v;
                    if (v < chMin[c]) chMin[c] = v;
                    if (v > chMax[c]) chMax[c] = v;
                }
            }
        }
        for (var c = 0; c < ch; c++)
        {
            var mean = chSum[c] / pixelCount;
            testOutputHelper.WriteLine($"  AutoLevel ch{c} range: [{chMin[c]:F1}, {chMax[c]:F1}]  mean: {mean:F1}  (Quantum.Max={Quantum.Max})");
            chMax[c].ShouldBeGreaterThan(Quantum.Max * 0.5f, $"channel {c} should reach >50% of Quantum.Max after AutoLevel");
            mean.ShouldBeInRange(Quantum.Max * 0.005, Quantum.Max * 0.995, $"channel {c} AutoLevel mean stays inside the quantum range");
        }
    }
}
