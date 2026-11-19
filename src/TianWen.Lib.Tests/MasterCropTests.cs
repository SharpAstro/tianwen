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
            new List<Image> { Frame(), Frame() },
            new IntegrationOptions(ApplyNormalization: false));
        IntegrationFitsWriter.Write(path, result, TestWcs(), alignment: alignment);
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
}
