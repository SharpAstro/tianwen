using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Sources;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The detection's sidecars round-trip through the FITS writer and reader: the label map exactly (a
/// 32-bit integer plane), the masks as 0 / 1, the background and noise maps as the floats the map
/// answers, each stamped with its MAPKIND; and the CSV table has one row per segment in label order.
/// </summary>
[Collection("Imaging")]
public class SourceDetectionWriterTests : IDisposable
{
    private const int W = 256;
    private const int H = 192;
    private const float Sky = 0.1f;
    private const float Noise = 0.002f;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TianWen.SourceDetectionWriter", Guid.NewGuid().ToString("N")[..8]);

    public SourceDetectionWriterTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("master.fits", ".labels.fits", null, "master.labels.fits")]
    [InlineData("master.fit", ".sources.csv", null, "master.sources.csv")]
    [InlineData("light.fits.gz", ".starmask.fits", null, "light.starmask.fits")]
    [InlineData("light.fit.fz", ".rms.fits", null, "light.rms.fits")]
    [InlineData("master.fits", ".labels.fits", "out", "master.labels.fits")]
    public void TheSidecarIsNamedByTheFrameAndItsSuffix(string frame, string suffix, string? directory, string expectedName)
    {
        var framePath = Path.Combine("frames", frame);
        var expected = Path.Combine(directory ?? "frames", expectedName);
        SourceDetectionWriter.SidecarPath(framePath, suffix, directory).ShouldBe(expected);
    }

    [Fact]
    public void TheLabelMapRoundTripsExactlyAndSaysWhatItIs()
    {
        var (segments, _) = Detect();
        var path = Path.Combine(_dir, "m.labels.fits");
        SourceDetectionWriter.WriteMap(path, SourceDetectionWriter.LabelsToImage(segments), SourceDetectionWriter.LabelsMapKind, wcs: null);

        Image.TryReadFitsFile(path, out var back, out _).ShouldBeTrue();
        back.Width.ShouldBe(W);
        back.Height.ShouldBe(H);
        back.BitDepth.ShouldBe(BitDepth.Int32);
        var plane = back.GetChannelSpan(0);
        var labels = segments.Labels;
        for (var i = 0; i < labels.Length; i++)
        {
            plane[i].ShouldBe(labels[i], $"pixel {i}");
        }

        using var fits = new nom.tam.fits.Fits(path);
        var header = fits.ReadFirstImageHduHeaderOnly().ShouldNotBeNull().Header;
        header.GetIntValue("BITPIX", 0).ShouldBe(32);
        header.GetStringValue("MAPKIND").ShouldBe(SourceDetectionWriter.LabelsMapKind);
        header.GetStringValue("SWCREATE").ShouldBe(SourceDetectionWriter.SoftwareCreator);
        TianWen.Lib.Imaging.Stacking.IntegrationFitsWriter.IsTianWenProduct(header.GetStringValue("SWCREATE")).ShouldBeTrue("a folder scan must never take a label map for a light");
    }

    [Fact]
    public void TheMasksRoundTripAsZeroAndOne()
    {
        var (segments, _) = Detect();
        var star = segments.StarMask(3);
        var path = Path.Combine(_dir, "m.starmask.fits");
        SourceDetectionWriter.WriteMap(path, SourceDetectionWriter.MaskToImage(star), SourceDetectionWriter.StarMaskMapKind, wcs: null);

        Image.TryReadFitsFile(path, out var back, out _).ShouldBeTrue();
        back.BitDepth.ShouldBe(BitDepth.Int8);
        var plane = back.GetChannelSpan(0);
        var set = 0;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var expected = star[y, x] ? 1f : 0f;
                plane[y * W + x].ShouldBe(expected, $"({x},{y})");
                set += (int)expected;
            }
        }

        set.ShouldBeGreaterThan(0, "the field has stars");
        set.ShouldBeLessThan(W * H / 2, "the star mask covers a minority of the frame");
    }

    [Fact]
    public void TheBackgroundAndNoiseMapsRoundTripAsTheMapAnswers()
    {
        var (_, background) = Detect();
        var bgPath = Path.Combine(_dir, "m.background.fits");
        var rmsPath = Path.Combine(_dir, "m.rms.fits");
        SourceDetectionWriter.WriteMap(bgPath, SourceDetectionWriter.BackgroundToImage(background), SourceDetectionWriter.BackgroundMapKind, wcs: null);
        SourceDetectionWriter.WriteMap(rmsPath, SourceDetectionWriter.RmsToImage(background), SourceDetectionWriter.RmsMapKind, wcs: null);

        Image.TryReadFitsFile(bgPath, out var bg, out _).ShouldBeTrue();
        Image.TryReadFitsFile(rmsPath, out var rms, out _).ShouldBeTrue();
        var bgPlane = bg.GetChannelSpan(0);
        var rmsPlane = rms.GetChannelSpan(0);
        for (var y = 0; y < H; y += 13)
        {
            for (var x = 0; x < W; x += 11)
            {
                bgPlane[y * W + x].ShouldBe(background.BackgroundAt(x, y), 1e-6f, $"background ({x},{y})");
                rmsPlane[y * W + x].ShouldBe(background.RmsAt(x, y), 1e-6f, $"rms ({x},{y})");
            }
        }

        bgPlane[(H / 4) * W + W / 4].ShouldBeInRange(0.9f * Sky, 1.1f * Sky);
        rmsPlane[(H / 4) * W + W / 4].ShouldBeInRange(0.7f * Noise, 1.3f * Noise);
    }

    [Fact]
    public async Task TheTableHasOneRowPerSegmentInLabelOrder()
    {
        var (segments, background) = Detect();
        var path = Path.Combine(_dir, "m.sources.csv");
        await SourceDetectionWriter.WriteTableAsync(path, segments, background, wcs: null, TestContext.Current.CancellationToken);

        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        lines.Length.ShouldBe(segments.Segments.Length + 1);
        lines[0].ShouldBe(SourceDetectionWriter.TableHeader);
        var columns = SourceDetectionWriter.TableHeader.Split(',').Length;
        for (var i = 0; i < segments.Segments.Length; i++)
        {
            var cells = lines[i + 1].Split(',');
            cells.Length.ShouldBe(columns, $"row {i}");
            cells[0].ShouldBe(segments.Segments[i].Label.ToString(), $"row {i} label");
            cells[1].ShouldBe(segments.Segments[i].Area.ToString(), $"row {i} area");
            cells[4].ShouldBeEmpty("no WCS, no sky position");
            cells[^1].ShouldBe(segments.Segments[i].IsCompact ? "1" : "0", $"row {i} class");
        }

        lines.Skip(1).Count(l => l.EndsWith(",0", StringComparison.Ordinal)).ShouldBeGreaterThan(0, "the nebula is extended");
    }

    [Fact]
    public void ThePrintedRowLinesUpUnderItsHeader()
    {
        var (segments, background) = Detect();
        var row = ImageSubCommand.FormatSegmentRow(segments.Segments[0], background);
        row.ShouldEndWith(segments.Segments[0].IsCompact ? "compact" : "extended");
        row.Length.ShouldBeGreaterThanOrEqualTo(ImageSubCommand.SegmentRowHeader.Length - 1);
        row.TrimStart().ShouldStartWith(segments.Segments[0].Label.ToString());
    }

    private static (SegmentationMap Segments, BackgroundMap Background) Detect()
    {
        var plane = SourceTestFrames.StarFieldWithNebula(W, H, Sky, Noise, stars: 30, seed: 5);
        var background = BackgroundMap.Estimate(plane, W, H, new BackgroundMapOptions(BlockSize: 32));
        var segments = SourceSegmentation.Detect(plane, W, H, background);
        segments.Segments.Length.ShouldBeGreaterThan(10);
        return (segments, background);
    }
}
