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
    internal async Task StretchTest(string fileName, DebayerAlgorithm algorithm, int stretchPct, int clippingSigma, bool linked, uint expectedChannelCount)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(fileName, cancellationToken: cancellationToken);

        var linkedStr = linked ? "linked" : "unlinked"; 
        var namePrefix = $"{fileName}_{algorithm}_{stretchPct}s_{linkedStr}";
        var testDir = SharedTestData.CreateTempTestOutputDir(TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTest));

        // when
        var sw = Stopwatch.StartNew();
        var stretched = linked
            ? await image.StretchLinkedAsync(stretchPct * 0.01d, clippingSigma, debayerAlgorithm: algorithm, cancellationToken: cancellationToken)
            : await image.StretchUnlinkedAsync(stretchPct * 0.01d, clippingSigma, debayerAlgorithm: algorithm, cancellationToken: cancellationToken);
        sw.Stop();
        testOutputHelper.WriteLine($"Debayering and stretching ({(linked ? "linked" : "unlinked")}) to {stretchPct}% using {algorithm} took: {sw.Elapsed}");

        // then
        sw.Restart();
        var magick = await stretched.ToMagickImageAsync(DebayerAlgorithm.None, cancellationToken);
        sw.Stop();
        testOutputHelper.WriteLine($"Converting stretched image to magick image took: {sw.Elapsed}");

        magick.ShouldNotBeNull();
        magick.Width.ShouldBe((uint)image.Width);
        magick.Height.ShouldBe((uint)image.Height);
        magick.ChannelCount.ShouldBe(expectedChannelCount);

        // when creating an auto-leveled image for comparision
        sw.Restart();
        magick.AutoLevel();
        sw.Stop();
        testOutputHelper.WriteLine($"Auto-levelling took: {sw.Elapsed}");

        // then
        sw.Restart();
        var autoLevelTiffBytes = magick.ToByteArray(MagickFormat.Tiff);
        sw.Stop();
        testOutputHelper.WriteLine($"Converting magick image to TIFF bytes took: {sw.Elapsed}");

        await File.WriteAllBytesAsync(Path.Combine(testDir, $"{namePrefix}_autoLevel.tiff"), autoLevelTiffBytes, cancellationToken);
    }
}
