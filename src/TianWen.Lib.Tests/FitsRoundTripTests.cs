using Shouldly;
using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class FitsRoundTripTests(ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData("PlateSolveTestFile")]
    [InlineData("image_file-snr-20_stars-28_1280x960x16")]
    public async Task GivenFitsFileWhenSavedAndReloadedThenImageMetaSurvives(string name)
    {
        // given — load original FITS
        var original = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: TestContext.Current.CancellationToken);
        var testDir = SharedTestData.CreateTempTestOutputDir();
        var fitsPath = Path.Combine(testDir, $"{name}_roundtrip.fits");

        // when — write and re-read
        original.WriteToFitsFile(fitsPath);
        var loaded = Image.TryReadFitsFile(fitsPath, out var reloaded);

        // then
        loaded.ShouldBeTrue("TryReadFitsFile should succeed");
        reloaded.ShouldNotBeNull();

        var orig = original.ImageMeta;
        var rt = reloaded.ImageMeta;

        testOutput.WriteLine($"Instrument: {orig.Instrument} -> {rt.Instrument}");
        testOutput.WriteLine($"Telescope: {orig.Telescope} -> {rt.Telescope}");
        testOutput.WriteLine($"FocalLength: {orig.FocalLength} -> {rt.FocalLength}");
        testOutput.WriteLine($"FocusPos: {orig.FocusPos} -> {rt.FocusPos}");
        testOutput.WriteLine($"Filter: {orig.Filter.Name} -> {rt.Filter.Name}");
        testOutput.WriteLine($"PixelSizeX: {orig.PixelSizeX} -> {rt.PixelSizeX}");
        testOutput.WriteLine($"BinX: {orig.BinX} -> {rt.BinX}");
        testOutput.WriteLine($"CCDTemp: {orig.CCDTemperature} -> {rt.CCDTemperature}");
        testOutput.WriteLine($"FrameType: {orig.FrameType} -> {rt.FrameType}");
        testOutput.WriteLine($"RowOrder: {orig.RowOrder} -> {rt.RowOrder}");
        testOutput.WriteLine($"Latitude: {orig.Latitude} -> {rt.Latitude}");
        testOutput.WriteLine($"Longitude: {orig.Longitude} -> {rt.Longitude}");
        testOutput.WriteLine($"ObjectName: {orig.ObjectName} -> {rt.ObjectName}");
        testOutput.WriteLine($"MaxValue: {original.MaxValue} -> {reloaded.MaxValue}");
        testOutput.WriteLine($"MinValue: {original.MinValue} -> {reloaded.MinValue}");

        // Core image properties
        reloaded.ChannelCount.ShouldBe(original.ChannelCount);
        reloaded.Width.ShouldBe(original.Width);
        reloaded.Height.ShouldBe(original.Height);

        // ImageMeta fields that should survive roundtrip
        rt.Instrument.ShouldBe(orig.Instrument);
        rt.Telescope.ShouldBe(orig.Telescope);
        rt.BinX.ShouldBe(orig.BinX);
        rt.BinY.ShouldBe(orig.BinY);
        rt.FrameType.ShouldBe(orig.FrameType);
        rt.RowOrder.ShouldBe(orig.RowOrder);
        rt.SensorType.ShouldBe(orig.SensorType);
        rt.ObjectName.ShouldBe(orig.ObjectName);

        if (!float.IsNaN(orig.PixelSizeX))
        {
            rt.PixelSizeX.ShouldBe(orig.PixelSizeX, 0.01f);
        }
        if (!float.IsNaN(orig.PixelSizeY))
        {
            rt.PixelSizeY.ShouldBe(orig.PixelSizeY, 0.01f);
        }
        if (!float.IsNaN(orig.CCDTemperature))
        {
            rt.CCDTemperature.ShouldBe(orig.CCDTemperature, 0.1f);
        }
        if (!float.IsNaN(orig.Latitude))
        {
            rt.Latitude.ShouldBe(orig.Latitude, 0.001f);
        }
        if (!float.IsNaN(orig.Longitude))
        {
            rt.Longitude.ShouldBe(orig.Longitude, 0.001f);
        }
        if (orig.FocalLength > 0)
        {
            rt.FocalLength.ShouldBe(orig.FocalLength);
        }
        if (orig.FocusPos >= 0)
        {
            rt.FocusPos.ShouldBe(orig.FocusPos);
        }

        // Exposure metadata
        rt.ExposureDuration.TotalSeconds.ShouldBe(orig.ExposureDuration.TotalSeconds, 0.01);
    }

    [Fact]
    public void GivenSyntheticImageWithFullMetaWhenRoundTrippedThenAllFieldsSurvive()
    {
        // given — build an image with every ImageMeta field populated
        var width = 64;
        var height = 48;
        var data = new float[height, width];
        var rng = new Random(123);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = (float)(100.0 + rng.NextDouble() * 3900.0);
            }
        }

        var imageMeta = new ImageMeta(
            Instrument: "Test Camera",
            ExposureStartTime: new DateTimeOffset(2025, 12, 1, 22, 30, 0, TimeSpan.Zero),
            ExposureDuration: TimeSpan.FromSeconds(120),
            FrameType: FrameType.Light,
            Telescope: "Test Scope",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 1000,
            FocusPos: 12345,
            Filter: Filter.HydrogenAlpha,
            BinX: 2,
            BinY: 2,
            CCDTemperature: -10.5f,
            SensorType: SensorType.Monochrome,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: 48.2f,
            Longitude: 16.3f,
            ObjectName: "M42",
            Gain: 120,
            Offset: 30,
            SetCCDTemperature: -15.0f,
            TargetRA: 5.5883,
            TargetDec: -5.3911,
            ElectronsPerADU: 1.2f,
            SWCreator: "TianWen"
        );

        var image = new Image(
            [data],
            BitDepth.Int16,
            maxValue: 4000f,
            minValue: 100f,
            blackLevel: 0f,
            imageMeta
        );

        var testDir = SharedTestData.CreateTempTestOutputDir();
        var fitsPath = Path.Combine(testDir, "synthetic_roundtrip.fits");

        // when
        image.WriteToFitsFile(fitsPath);
        var loaded = Image.TryReadFitsFile(fitsPath, out var reloaded);

        // then
        loaded.ShouldBeTrue();
        reloaded.ShouldNotBeNull();

        var rt = reloaded.ImageMeta;

        testOutput.WriteLine($"DerivedPixelScale: {imageMeta.DerivedPixelScale:F3} arcsec/px");

        rt.Instrument.ShouldBe("Test Camera");
        rt.Telescope.ShouldBe("Test Scope");
        rt.FocalLength.ShouldBe(1000);
        rt.FocusPos.ShouldBe(12345);
        rt.Filter.Name.ShouldBe("HydrogenAlpha");
        rt.BinX.ShouldBe(2);
        rt.BinY.ShouldBe(2);
        rt.CCDTemperature.ShouldBe(-10.5f, 0.1f);
        rt.PixelSizeX.ShouldBe(3.76f, 0.01f);
        rt.PixelSizeY.ShouldBe(3.76f, 0.01f);
        rt.FrameType.ShouldBe(FrameType.Light);
        rt.RowOrder.ShouldBe(RowOrder.TopDown);
        rt.Latitude.ShouldBe(48.2f, 0.01f);
        rt.Longitude.ShouldBe(16.3f, 0.01f);
        rt.ObjectName.ShouldBe("M42");
        rt.ExposureDuration.TotalSeconds.ShouldBe(120.0, 0.01);
        rt.SensorType.ShouldBe(SensorType.Monochrome);

        // New fields
        rt.Gain.ShouldBe((short)120);
        rt.Offset.ShouldBe(30);
        rt.SetCCDTemperature.ShouldBe(-15.0f, 0.1f);
        rt.ElectronsPerADU.ShouldBe(1.2f, 0.01f);
        rt.SWCreator.ShouldBe("TianWen");

        // DerivedPixelScale should be consistent
        rt.DerivedPixelScale.ShouldBe(imageMeta.DerivedPixelScale, 0.001);

        // Min/max values
        reloaded.MaxValue.ShouldBeGreaterThan(0);
        reloaded.MinValue.ShouldBeGreaterThanOrEqualTo(0);

        // Dimensions
        reloaded.Width.ShouldBe(width);
        reloaded.Height.ShouldBe(height);
    }
}
