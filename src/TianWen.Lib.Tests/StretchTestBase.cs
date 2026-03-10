using ImageMagick;
using Shouldly;
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
    }
}
