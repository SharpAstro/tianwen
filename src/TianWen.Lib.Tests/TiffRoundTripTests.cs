using ImageMagick;
using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class TiffRoundTripTests(ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData("PlateSolveTestFile", DebayerAlgorithm.None)]
    [InlineData("Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop", DebayerAlgorithm.None)]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.VNG)]
    public async Task GivenFitsFileWhenSavedAsTiffAndReloadedThenPixelDataIsPreserved(string name, DebayerAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        // given — load FITS and convert to MagickImage
        var original = await SharedTestData.ExtractGZippedFitsImageAsync(name, isReadOnly: false, cancellationToken: cancellationToken);
        var magick = await original.ToMagickImageAsync(algorithm, cancellationToken);

        // when — save as TIFF and reload via TryReadTiffFile
        var testDir = SharedTestData.CreateTempTestOutputDir();
        var tiffPath = Path.Combine(testDir, $"{name}.tiff");
        await File.WriteAllBytesAsync(tiffPath, magick.ToByteArray(MagickFormat.Tiff), cancellationToken);
        testOutput.WriteLine($"Saved TIFF to {tiffPath} ({new FileInfo(tiffPath).Length:N0} bytes)");

        var loaded = Image.TryReadTiffFile(tiffPath, out var reloaded);

        // then — image loads and dimensions match
        loaded.ShouldBeTrue("TryReadTiffFile should succeed");
        reloaded.ShouldNotBeNull();

        var expectedChannels = algorithm is DebayerAlgorithm.None && original.ImageMeta.SensorType is not SensorType.RGGB
            ? original.ChannelCount
            : 3; // debayered → RGB
        reloaded.ChannelCount.ShouldBe(expectedChannels);

        // Use the debayered/converted image for dimension comparison
        Image reference;
        if (original.ImageMeta.SensorType is SensorType.RGGB && algorithm is not DebayerAlgorithm.None)
        {
            reference = await original.DebayerAsync(algorithm, normalizeToUnit: true, cancellationToken);
        }
        else
        {
            reference = original.ScaleFloatValuesToUnit();
        }

        reloaded.Width.ShouldBe(reference.Width);
        reloaded.Height.ShouldBe(reference.Height);

        // Pixel values should be close (within quantization tolerance of Q16 = 1/65535)
        var tolerance = 2f / Quantum.Max; // 2 quanta tolerance for rounding
        for (var c = 0; c < reloaded.ChannelCount; c++)
        {
            var refSpan = reference.GetChannelSpan(c);
            var loadedSpan = reloaded.GetChannelSpan(c);

            // Sample pixels to check (every 1000th pixel for speed)
            var step = Math.Max(1, refSpan.Length / 1000);
            var maxDiff = 0f;
            for (var i = 0; i < refSpan.Length; i += step)
            {
                var diff = MathF.Abs(refSpan[i] - loadedSpan[i]);
                maxDiff = MathF.Max(maxDiff, diff);
            }

            testOutput.WriteLine($"Channel {c}: max pixel difference = {maxDiff:E3} (tolerance = {tolerance:E3})");
            maxDiff.ShouldBeLessThan(tolerance, $"Channel {c} pixel values should be within quantization tolerance");
        }
    }

    [Theory]
    [InlineData("PlateSolveTestFile", false)]
    [InlineData("Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop", false)]
    public async Task GivenUnstretchedFitsWhenSavedAsTiffThenDetectedAsNotPreStretched(string name, bool expectedPreStretched, CancellationToken cancellationToken = default)
    {
        // given — load FITS, save as TIFF (unstretched, linear data)
        var original = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: cancellationToken);
        var magick = await original.ToMagickImageAsync(DebayerAlgorithm.None, cancellationToken);

        var testDir = SharedTestData.CreateTempTestOutputDir();
        var tiffPath = Path.Combine(testDir, $"{name}_linear.tiff");
        await File.WriteAllBytesAsync(tiffPath, magick.ToByteArray(MagickFormat.Tiff), cancellationToken);

        // when
        Image.TryReadTiffFile(tiffPath, out var reloaded).ShouldBeTrue();
        var isPreStretched = Image.DetectPreStretched(reloaded!);

        // then
        testOutput.WriteLine($"Median-based pre-stretch detection: {isPreStretched}");
        isPreStretched.ShouldBe(expectedPreStretched);
    }
}
