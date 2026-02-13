using Shouldly;
using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Xunit;

namespace TianWen.Lib.Tests;

public class ImagConversionTests
{
    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 1)]
    [InlineData("RGGB_frame_bx0_by0_top_down", 3)]
    public async Task GivenFitsFileWhenConvertingToMagickImageThenItShouldSucceed(string name, uint expectedChannelCount)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: cancellationToken);
        
        // when
        var magick = image.ToMagickImage();
        
        // then
        magick.ShouldNotBeNull();
        magick.Width.ShouldBe((uint)image.Width);
        magick.Height.ShouldBe((uint)image.Height);
        magick.ChannelCount.ShouldBe(expectedChannelCount);
        var scaledBytes = magick.ToByteArray(ImageMagick.MagickFormat.Tiff);

        await File.WriteAllBytesAsync(Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"{name}_scaled.tiff"), scaledBytes, cancellationToken);

        // when further
        magick.AutoLevel();

        // then
        var autoLevelBytes = magick.ToByteArray(ImageMagick.MagickFormat.Tiff);

        await File.WriteAllBytesAsync(Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"{name}_autoLevel.tiff"), autoLevelBytes, cancellationToken);
    }

    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 1)]
    [InlineData("RGGB_frame_bx0_by0_top_down", 3)]
    public async Task GivenFitsFileWhenRotatingAndConvertingToMagickImageThenItShouldSucceed(string name, uint expectedChannelCount)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: cancellationToken);

        // when
        var rotated = image.Transform(Matrix3x2.CreateRotation(MathF.PI / 4));
        var magick = rotated.ToMagickImage();

        // then
        magick.ShouldNotBeNull();
        magick.Width.ShouldBe((uint)rotated.Width);
        magick.Height.ShouldBe((uint)rotated.Height);
        magick.ChannelCount.ShouldBe(expectedChannelCount);
        var scaledBytes = magick.ToByteArray(ImageMagick.MagickFormat.Tiff);

        await File.WriteAllBytesAsync(Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"{name}_scaled.tiff"), scaledBytes, cancellationToken);

        // when further
        magick.AutoLevel();

        // then
        var autoLevelBytes = magick.ToByteArray(ImageMagick.MagickFormat.Tiff);

        await File.WriteAllBytesAsync(Path.Combine(SharedTestData.CreateTempTestOutputDir(), $"{name}_autoLevel.tiff"), autoLevelBytes, cancellationToken);
    }
}
