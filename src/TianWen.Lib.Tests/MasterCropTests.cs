using System;
using System.Collections.Generic;
using System.IO;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class MasterCropTests : IDisposable
{
    private const int W = 40;
    private const int H = 30;

    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    private string CreateTempDir([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => _folders.Create((name ?? "x") + "-").FullName;

    private static Image Frame()
    {
        var arr = new float[H, W];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                arr[y, x] = 0.001f * (y * W + x);
        var meta = new ImageMeta(
            Instrument: "synthetic", ExposureStartTime: DateTimeOffset.UnixEpoch,
            ExposureDuration: TimeSpan.FromSeconds(60), FrameType: FrameType.Light,
            Telescope: "TestScope", PixelSizeX: 3.76f, PixelSizeY: 3.76f, FocalLength: 400, FocusPos: FocuserPositions.None,
            Filter: Filter.Luminance, BinX: 1, BinY: 1, CCDTemperature: -10f,
            SensorType: SensorType.Monochrome, BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown, Latitude: float.NaN, Longitude: float.NaN);
        return new Image([arr], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }

    private static WCS TestWcs() => new WCS(10.0 / 15.0, 20.0)
    {
        CRPix1 = 19.5,
        CRPix2 = 14.5,
        CD1_1 = -0.001,
        CD1_2 = 0,
        CD2_1 = 0,
        CD2_2 = 0.001,
    };

    private string WriteMaster(AlignmentProvenance? alignment)
    {
        var path = Path.Combine(CreateTempDir(), "master.fits");
        var result = Integrator.Integrate(
            new List<Image> { Frame(), Frame(), Frame() },
            new IntegrationOptions(Rejector: new MinMaxClipRejector(0, 1), ApplyNormalization: false));
        IntegrationFitsWriter.Write(path, result, TestWcs(), IntegrationStrategyKind.InRamAllFrames, alignment: alignment);
        return path;
    }

    private static nom.tam.fits.Header ReadHeader(string path)
    {
        using var fits = Image.OpenFitsHeader(path);
        return fits.ReadFirstImageHduHeaderOnly()!.Header;
    }

    [Fact]
    public void CropFile_ShiftsOriginAndWcsAndKeepsProvenance()
    {
        var alignment = AlignmentProvenance.Sidereal with { CanvasOriginX = -63, CanvasOriginY = 25, ReferenceFrame = "ref_0033.fits" };
        var input = WriteMaster(alignment);
        var output = Path.Combine(Path.GetDirectoryName(input)!, "cropped.fits");
        var rect = new PixelRect(7, 4, 20, 15);

        var result = MasterCrop.CropFile(input, rect, output);

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Width.ShouldBe(20);
        result.Height.ShouldBe(15);
        result.SourceWidth.ShouldBe(40);
        result.SourceHeight.ShouldBe(30);

        Image.TryReadFitsFile(input, out var before, out var wcsBefore).ShouldBeTrue();
        Image.TryReadFitsFile(output, out var after, out var wcsAfter).ShouldBeTrue();
        after!.Width.ShouldBe(20);
        after.Height.ShouldBe(15);
        for (var y = 0; y < 15; y++)
            for (var x = 0; x < 20; x++)
                after[0, y, x].ShouldBe(before![0, y + 4, x + 7]);

        var header = ReadHeader(output);
        header.GetIntValue("CANVASX0", int.MinValue).ShouldBe(-56);
        header.GetIntValue("CANVASY0", int.MinValue).ShouldBe(29);
        header.GetStringValue("REFFRAME").ShouldBe("ref_0033.fits");
        header.GetStringValue("ALIGNMNT").ShouldBe("Sidereal");
        header.GetStringValue("SWMODIFY").ShouldNotBeNullOrEmpty();
        AlignmentProvenance.FromHeader(header).ShouldBe(alignment.ForCrop(7, 4));

        // The integration cards describe the stack, which a crop does not change.
        var source = ReadHeader(input);
        header.GetIntValue("STACK_N", -1).ShouldBe(3);
        header.GetIntValue("SNAPSHOT", -1).ShouldBe(3);
        source.GetLongValue("REJ_TOT", -1L).ShouldBeGreaterThan(0L);
        header.GetStringValue("STRATEGY").ShouldBe("InRamAllFrames");
        header.GetLongValue("REJ_TOT", -1L).ShouldBe(source.GetLongValue("REJ_TOT", -2L));
        header.GetDoubleValue("REJ_RATE", -1.0).ShouldBe(source.GetDoubleValue("REJ_RATE", -2.0));

        // The same sky is at the shifted pixel.
        wcsBefore.ShouldNotBeNull();
        wcsAfter.ShouldNotBeNull();
        var sky = wcsBefore.Value.PixelToSky(12.0, 9.0);
        sky.ShouldNotBeNull();
        var shifted = wcsAfter.Value.SkyToPixel(sky.Value.RA, sky.Value.Dec);
        shifted.ShouldNotBeNull();
        shifted.Value.X.ShouldBe(12.0 - 7, 1e-6);
        shifted.Value.Y.ShouldBe(9.0 - 4, 1e-6);
    }

    [Fact]
    public void Crop_MasterWithoutProvenanceGetsNone()
    {
        var path = Path.Combine(CreateTempDir(), "plain.fits");
        Frame().WriteToFitsFile(path, TestWcs());
        var output = Path.Combine(Path.GetDirectoryName(path)!, "cropped.fits");

        MasterCrop.CropFile(path, new PixelRect(0, 0, 10, 10), output).Succeeded.ShouldBeTrue();

        var header = ReadHeader(output);
        AlignmentProvenance.FromHeader(header).ShouldBeNull();
        header.ContainsKey("ALIGNMNT").ShouldBeFalse();
        header.ContainsKey("CANVASX0").ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1, 5, 10, 10, "starts outside the image")]
    [InlineData(5, -3, 10, 10, "starts outside the image")]
    [InlineData(35, 0, 10, 10, "reaches x=45, past the image width 40")]
    [InlineData(0, 25, 10, 10, "reaches y=35, past the image height 30")]
    [InlineData(0, 0, 0, 10, "is empty")]
    [InlineData(0, 0, 10, 0, "is empty")]
    public void CropFile_OutOfBoundsIsRefusedInWordsAndWritesNoFile(int x, int y, int w, int h, string expected)
    {
        var input = WriteMaster(AlignmentProvenance.Sidereal);
        var output = Path.Combine(Path.GetDirectoryName(input)!, "cropped.fits");

        var result = MasterCrop.CropFile(input, new PixelRect(x, y, w, h), output);

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldContain(expected);
        result.Message.ShouldContain($"{w}x{h} at ({x},{y})");
        result.Message.ShouldContain("40");
        result.Message.ShouldContain("30");
        File.Exists(output).ShouldBeFalse();
    }

    // ---- the map sidecars are cropped with the master (#2274) ---------------------------------

    /// <summary>A map on the master's grid whose value at (x, y) is <c>scale * (x + 100 y)</c>, so a crop's origin shows in it.</summary>
    private static Image MapPlane(float scale, float maxValue)
    {
        var plane = new float[H, W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                plane[y, x] = scale * (x + (100 * y));
            }
        }

        return new Image([plane], BitDepth.Float32, maxValue: maxValue, minValue: 0f, pedestal: 0f,
            imageMeta: new ImageMeta { SensorType = SensorType.Monochrome });
    }

    [Fact]
    public void CropFile_CropsTheMapsThatDescribeTheMastersPixelsWithIt()
    {
        var input = WriteMaster(AlignmentProvenance.Sidereal);
        // Whole counts for the two count maps, a fraction for rejection, the master's own units for the standard error.
        IntegrationFitsWriter.WriteCoverageMap(input, MapPlane(1f, 3000f), frameCount: 24);
        IntegrationFitsWriter.WriteSaturationMap(input, MapPlane(1f, 3000f), frameCount: 24);
        IntegrationFitsWriter.WriteRejectionMap(input, MapPlane(1f / 4000f, 1f), frameCount: 24, meanRejectionRate: 0.4);
        IntegrationFitsWriter.WriteStandardErrorMap(input, MapPlane(1e-5f, 0.04f), frameCount: 24);
        var output = Path.Combine(Path.GetDirectoryName(input)!, "cropped.fits");
        var rect = new PixelRect(7, 4, 20, 15);

        var result = MasterCrop.CropFile(input, rect, output);

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Sidecars.ShouldBe(["coverage", "rejection", "saturation", "standard error"]);
        result.SidecarsLeftOut.ShouldBeEmpty();

        // Each map beside the output is the crop's size and starts at the crop's origin: its first pixel is the
        // source's pixel at (7, 4).
        var origin = 7 + (100 * 4);
        IntegrationFitsWriter.TryReadCoverageMap(output, out var coverage).ShouldBeTrue();
        (coverage!.Width, coverage.Height).ShouldBe((20, 15));
        coverage[0, 0, 0].ShouldBe(origin, 0.5f);
        coverage[0, 14, 19].ShouldBe(origin + 19 + (100 * 14), 0.5f);

        IntegrationFitsWriter.TryReadSaturationMap(output, out var saturation).ShouldBeTrue();
        (saturation!.Width, saturation.Height).ShouldBe((20, 15));
        saturation[0, 0, 0].ShouldBe(origin, 0.5f);

        IntegrationFitsWriter.TryReadRejectionMap(output, out var rejection).ShouldBeTrue();
        (rejection!.Width, rejection.Height).ShouldBe((20, 15));
        rejection[0, 0, 0].ShouldBe(origin / 4000f, 0.001f);

        IntegrationFitsWriter.TryReadStandardErrorMap(output, out var standardError).ShouldBeTrue();
        (standardError!.Width, standardError.Height).ShouldBe((20, 15));
        (standardError[0, 0, 0] / (origin * 1e-5f)).ShouldBe(1f, 0.02f, "the standard error is stored as its logarithm, so it reads back to a ratio");

        // The rejection map's own card is this map's average, not the uncropped one's (0.4 was written above). The
        // sidecar is gzipped, so its header is read with the data and not by seeking past it.
        using var rejectionFile = Image.OpenFitsHeader(IntegrationFitsWriter.ExistingSidecarPath(IntegrationFitsWriter.RejectionPathFor(output))!);
        rejectionFile.ReadFirstImageHdu()!.Header.GetDoubleValue("REJ_RATE", -1).ShouldBeInRange(0.2, 0.3);
    }

    [Fact]
    public void CropFile_LeavesNoMapOfAnEarlierFileBesideItsOutput()
    {
        // An integrated master carries coverage, rejection and standard error maps, and no saturation map.
        var input = WriteMaster(AlignmentProvenance.Sidereal);
        IntegrationFitsWriter.TryReadSaturationMap(input, out _).ShouldBeFalse("premise: this master has no saturation map");
        var output = Path.Combine(Path.GetDirectoryName(input)!, "cropped.fits");
        // A file that was at the output's path before had a saturation map and a coverage map of its own size.
        IntegrationFitsWriter.WriteSaturationMap(output, MapPlane(1f, 3000f), frameCount: 3);
        IntegrationFitsWriter.WriteCoverageMap(output, MapPlane(1f, 3000f), frameCount: 3);

        var result = MasterCrop.CropFile(input, new PixelRect(7, 4, 20, 15), output);

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Sidecars.ShouldBe(["coverage", "rejection", "standard error"]);
        IntegrationFitsWriter.TryReadSaturationMap(output, out _).ShouldBeFalse("a map of other pixels must not be read as this crop's");
        IntegrationFitsWriter.TryReadCoverageMap(output, out var coverage).ShouldBeTrue();
        (coverage!.Width, coverage.Height).ShouldBe((20, 15), "and the coverage beside the output is this crop's, not the earlier file's");
    }

    // The bad pixel map is the sensor's and not on the canvas grid, so a crop does not carry it, and must not take it
    // away either: a crop onto the master's own path deleted the master's bad pixel map without a word (pilot's review
    // of #2307).
    [Fact]
    public void CropFile_OntoTheMastersOwnPathKeepsItsBadPixelMap()
    {
        var input = WriteMaster(AlignmentProvenance.Sidereal);
        var badPixels = IntegrationFitsWriter.BadPixelPathFor(input);
        File.WriteAllText(badPixels, "the sensor's");

        var result = MasterCrop.CropFile(input, new PixelRect(7, 4, 20, 15), input);

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Sidecars.ShouldBe(["coverage", "rejection", "standard error"]);
        result.SidecarsLeftOut.ShouldBeEmpty();
        File.Exists(badPixels).ShouldBeTrue("the crop names nothing in the sensor's map, so it is not the crop's to remove");
        File.ReadAllText(badPixels).ShouldBe("the sensor's");
        IntegrationFitsWriter.TryReadCoverageMap(input, out var coverage).ShouldBeTrue();
        var map = coverage.ShouldNotBeNull();
        (map.Width, map.Height).ShouldBe((20, 15), "and the maps on the canvas grid are this crop's");
    }

    [Fact]
    public void CropFile_ARefusedCropWritesNoMapAndRemovesNone()
    {
        var input = WriteMaster(AlignmentProvenance.Sidereal);
        IntegrationFitsWriter.WriteCoverageMap(input, MapPlane(1f, 3000f), frameCount: 24);
        var output = Path.Combine(Path.GetDirectoryName(input)!, "cropped.fits");
        IntegrationFitsWriter.WriteCoverageMap(output, MapPlane(1f, 3000f), frameCount: 3);

        var result = MasterCrop.CropFile(input, new PixelRect(35, 0, 10, 10), output);

        result.Succeeded.ShouldBeFalse();
        result.Sidecars.ShouldBeEmpty();
        IntegrationFitsWriter.TryReadCoverageMap(output, out var untouched).ShouldBeTrue("a refusal changes nothing on disk");
        (untouched!.Width, untouched.Height).ShouldBe((W, H));
    }

    [Fact]
    public void CropFile_AMapThatIsNotOnTheMastersGridIsLeftOutAndNamed()
    {
        var input = WriteMaster(AlignmentProvenance.Sidereal);
        var small = new Image([new float[H / 2, W / 2]], BitDepth.Float32, maxValue: 24f, minValue: 0f, pedestal: 0f,
            imageMeta: new ImageMeta { SensorType = SensorType.Monochrome });
        IntegrationFitsWriter.WriteSaturationMap(input, small, frameCount: 24);
        var output = Path.Combine(Path.GetDirectoryName(input)!, "cropped.fits");

        var result = MasterCrop.CropFile(input, new PixelRect(7, 4, 20, 15), output);

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Sidecars.ShouldBe(["coverage", "rejection", "standard error"], "the maps that are on the grid are still carried");
        result.SidecarsLeftOut.ShouldHaveSingleItem().ShouldContain("saturation map is 20x15, not the master's 40x30");
        IntegrationFitsWriter.TryReadSaturationMap(output, out _).ShouldBeFalse();
    }
}
