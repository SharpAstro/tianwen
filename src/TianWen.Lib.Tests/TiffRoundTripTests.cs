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
        // given — load FITS
        var original = await SharedTestData.ExtractGZippedFitsImageAsync(name, isReadOnly: false, cancellationToken: cancellationToken);

        // when — save as TIFF via DIR.Lib and reload via TryReadImageFile
        var testDir = SharedTestData.CreateTempTestOutputDir();
        var tiffPath = Path.Combine(testDir, $"{name}.tiff");
        await original.WriteTiffAsync(tiffPath, algorithm, cancellationToken);
        testOutput.WriteLine($"Saved TIFF to {tiffPath} ({new FileInfo(tiffPath).Length:N0} bytes)");

        var loaded = Image.TryReadImageFile(tiffPath, out var reloaded);

        // then — image loads and dimensions match
        loaded.ShouldBeTrue("TryReadImageFile should succeed");
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

        // Pixel values should be close (within quantization tolerance of Q16 = 1/65535).
        // The round-trip goes through libtiff-HDRI which uses the SMaxSampleValue=65535
        // tag to remap file-side [0, 1] floats to in-memory [0, 65535] on read.
        var tolerance = 2f / 65535f; // 2 quanta tolerance for rounding
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

        var testDir = SharedTestData.CreateTempTestOutputDir();
        var tiffPath = Path.Combine(testDir, $"{name}_linear.tiff");
        await original.WriteTiffAsync(tiffPath, DebayerAlgorithm.None, cancellationToken);

        // when
        Image.TryReadImageFile(tiffPath, out var reloaded).ShouldBeTrue();
        var isPreStretched = Image.DetectPreStretched(reloaded!);

        // then
        testOutput.WriteLine($"Median-based pre-stretch detection: {isPreStretched}");
        isPreStretched.ShouldBe(expectedPreStretched);
    }

    [Fact]
    public async Task GivenUnitRangeImageWithStaleAduFullScaleWhenSavedAsTiffThenValuesAreNotDividedAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // given -- an already-[0,1] image (e.g. a third-party float FITS) that still carries an
        // ADU-domain SensorFullScaleAdu (a SATURATE card that was never rescaled with the data).
        // The write-side gate must key on the ACTUAL pixel range (MaxValue <= 1 -> no scaling),
        // mirroring ScaleFloatValuesToUnit's early-return -- otherwise the stale divisor would
        // turn the TIFF near-black (0.9 -> 0.9 / 65535).
        var data = new float[2, 2] { { 0.1f, 0.25f }, { 0.5f, 0.9f } };
        var meta = new ImageMeta { SensorFullScaleAdu = ushort.MaxValue };
        var original = new Image([new Channel(data, default, 0.1f, 0.9f, 0)], BitDepth.Float32, 0f, meta);

        // when
        var testDir = SharedTestData.CreateTempTestOutputDir();
        var tiffPath = Path.Combine(testDir, "unit_range_stale_adu.tiff");
        await original.WriteTiffAsync(tiffPath, DebayerAlgorithm.None, cancellationToken);

        // then -- values round-trip verbatim (within Q16 quantisation), not divided by the stale full-scale
        Image.TryReadImageFile(tiffPath, out var reloaded).ShouldBeTrue();
        reloaded.ShouldNotBeNull();
        var tolerance = 2f / 65535f;
        reloaded[0, 0, 0].ShouldBe(0.1f, tolerance);
        reloaded[0, 1, 1].ShouldBe(0.9f, tolerance);
    }
}
