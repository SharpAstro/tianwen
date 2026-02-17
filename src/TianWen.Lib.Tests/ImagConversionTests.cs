using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class ImagConversionTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", DebayerAlgorithm.None, 1)]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.VNG, 3)]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.BilinearMono, 1)]
    public async Task GivenFitsFileWhenConvertingToMagickImageThenItShouldBeAValidImage(string name, DebayerAlgorithm algorithm, uint expectedChannelCount)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: cancellationToken);

        // when
        var sw = Stopwatch.StartNew();
        var magick = await image.ToMagickImageAsync(algorithm, cancellationToken);
        sw.Stop();
        testOutputHelper.WriteLine($"Debayering using {algorithm} and conversion to MagickImage took: {sw.Elapsed}");

        // then
        magick.ShouldNotBeNull();
        magick.Width.ShouldBe((uint)image.Width);
        magick.Height.ShouldBe((uint)image.Height);
        magick.ChannelCount.ShouldBe(expectedChannelCount);
        var scaledBytes = magick.ToByteArray(ImageMagick.MagickFormat.Tiff);

        await File.WriteAllBytesAsync(Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"{name}_{algorithm}_scaled.tiff"), scaledBytes, cancellationToken);

        // when further
        magick.AutoLevel();

        // then
        var autoLevelBytes = magick.ToByteArray(ImageMagick.MagickFormat.Tiff);

        await File.WriteAllBytesAsync(Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"{name}_{algorithm}_autoLevel.tiff"), autoLevelBytes, cancellationToken);
    }

    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", DebayerAlgorithm.None, 1)]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.VNG, 3)]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.BilinearMono, 1)]
    public async Task GivenFitsFileWhenRotatingAndConvertingToMagickImageThenItShouldBeAValidImage(string name, DebayerAlgorithm algorithm, uint expectedChannelCount)
    {
        // given
        var sw = Stopwatch.StartNew();
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: cancellationToken);
        testOutputHelper.WriteLine($"Extracting fits file {name} took: {sw.Elapsed}");

        // when
        sw.Restart();
        var debayered = await image.DebayerAsync(algorithm, cancellationToken);
        testOutputHelper.WriteLine($"Debayering using {algorithm} took: {sw.Elapsed}");
        
        sw.Restart();
        var rotated = await debayered.TransformAsync(Matrix3x2.CreateRotation(MathF.PI / 4), cancellationToken);
        testOutputHelper.WriteLine($"Rotation took: {sw.Elapsed}");

        sw.Restart();
        var magick = await rotated.ToMagickImageAsync(DebayerAlgorithm.None, cancellationToken);
        testOutputHelper.WriteLine($"Conversion to MagickImage took: {sw.Elapsed}");
        sw.Stop();

        // then
        magick.ShouldNotBeNull();
        magick.Width.ShouldBe((uint)rotated.Width);
        magick.Height.ShouldBe((uint)rotated.Height);
        magick.ChannelCount.ShouldBe(expectedChannelCount);
        var scaledBytes = magick.ToByteArray(ImageMagick.MagickFormat.Tiff);

        await File.WriteAllBytesAsync(Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"{name}_{algorithm}_scaled.tiff"), scaledBytes, cancellationToken);

        // when further
        magick.AutoLevel();

        // then
        var autoLevelBytes = magick.ToByteArray(ImageMagick.MagickFormat.Tiff);

        await File.WriteAllBytesAsync(Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"{name}_{algorithm}_autoLevel.tiff"), autoLevelBytes, cancellationToken);
    }
}
