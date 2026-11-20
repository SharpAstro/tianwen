using System;
using System.IO;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="MasterAutoCrop"/>: the coverage sidecar's exact tier, the edge walk as its fallback, and
/// the rule (what a refused edge loses, the margin) a caller states. In memory apart from the sidecar
/// files, which sit in a temp folder.
/// </summary>
public sealed class MasterAutoCropTests : IDisposable
{
    private const double BaseSigma = 0.002;

    private readonly TempFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    private string MasterPath() => Path.Combine(_temp.Create("autocrop-").FullName, "master.fits");

    /// <summary>White noise on a flat level; <paramref name="sigmaAt"/> scales the noise per pixel.</summary>
    private static Image NoiseFrame(int width, int height, Func<int, int, double> sigmaAt, int seed = 20261120)
    {
        var rng = new Random(seed);
        var plane = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var u1 = 1.0 - rng.NextDouble();
                var u2 = rng.NextDouble();
                var gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                plane[y, x] = (float)(0.2 + BaseSigma * sigmaAt(x, y) * gauss);
            }
        }

        return new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
            imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
    }

    /// <summary>A frame-count plane: 24 inside <paramref name="inner"/>, 0 outside it.</summary>
    private static Image CoveragePlane(int width, int height, PixelRect inner)
    {
        var plane = new float[height, width];
        for (var y = inner.Y; y < inner.Y + inner.Height; y++)
        {
            for (var x = inner.X; x < inner.X + inner.Width; x++)
            {
                plane[y, x] = 24f;
            }
        }

        return new Image([plane], BitDepth.Float32, maxValue: 24f, minValue: 0f, pedestal: 0f,
            imageMeta: new ImageMeta { SensorType = SensorType.Monochrome });
    }

    private static PixelRect Whole(Image image) => new(0, 0, image.Width, image.Height);

    [Fact]
    public void TheCoverageSidecarsExactTierWinsWhenOneIsWrittenBesideTheMaster()
    {
        var master = NoiseFrame(256, 256, static (_, _) => 1.0);
        var path = MasterPath();
        var inner = new PixelRect(32, 48, 160, 144);
        IntegrationFitsWriter.WriteCoverageMap(path, CoveragePlane(256, 256, inner), frameCount: 24);

        var scan = MasterAutoCrop.Scan(master, path, MasterAutoCropRule.ForViewing);

        scan.FromCoverage.ShouldBeTrue();
        scan.Trims.ShouldBeNull();
        scan.Rect.ShouldBe(inner);
    }

    [Fact]
    public void NoSidecarFallsBackToTheEdgeWalk()
    {
        var master = NoiseFrame(1024, 1024, static (_, _) => 1.0);

        var scan = MasterAutoCrop.Scan(master, MasterPath(), MasterAutoCropRule.ForViewing);

        scan.FromCoverage.ShouldBeFalse();
        scan.Trims.ShouldNotBeNull();
        scan.Rect.ShouldBe(Whole(master));
    }

    [Fact]
    public void AnUnreadableSidecarFallsBackAndDoesNotThrow()
    {
        var master = NoiseFrame(1024, 1024, static (_, _) => 1.0);
        var path = MasterPath();
        File.WriteAllText(IntegrationFitsWriter.CoveragePathFor(path), "this is not a FITS file");

        var scan = MasterAutoCrop.Scan(master, path, MasterAutoCropRule.ForViewing);

        scan.FromCoverage.ShouldBeFalse();
        scan.Trims.ShouldNotBeNull();
        scan.Rect.ShouldBe(Whole(master));
    }

    [Fact]
    public void AnEmptyUnionReturnsEmptyWithNoWalk()
    {
        var plane = new float[64, 64];
        var master = new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
            imageMeta: new ImageMeta { SensorType = SensorType.Monochrome });

        var scan = MasterAutoCrop.Scan(master, MasterPath(), MasterAutoCropRule.ForViewing);

        (scan.Rect.Width <= 0 || scan.Rect.Height <= 0).ShouldBeTrue();
        scan.FromCoverage.ShouldBeFalse();
        scan.Trims.ShouldBeNull();
    }

    [Fact]
    public void ADeclinedEdgeIsKeptForAViewerAndTrimmedForAModel()
    {
        // A band that ends between the search window and the confirming look: the walk refuses the edge.
        var master = NoiseFrame(1024, 1024, static (_, y) => y < 120 ? 3.0 : 1.0);

        var viewed = MasterAutoCrop.Scan(master, null, MasterAutoCropRule.ForViewing);
        viewed.Declined.ShouldBeTrue();
        viewed.Rect.ShouldBe(Whole(master));
        viewed.DeclinedTrim.ShouldBeNull();

        var rule = MasterAutoCropRule.ForModel(0.05);
        var modelled = MasterAutoCrop.Scan(master, null, rule);
        modelled.Declined.ShouldBeTrue();
        modelled.DeclinedTrim.ShouldNotBeNull();
        modelled.Rect.Height.ShouldBeLessThan(master.Height);
        modelled.Rect.Y.ShouldBeGreaterThan(0);
        MasterAutoCrop.DescribeDeclined(modelled, rule).ShouldContain("top");
        MasterAutoCrop.DescribeDeclined(viewed, MasterAutoCropRule.ForViewing).ShouldBeEmpty();
    }

    [Fact]
    public void AMarginInsetsEveryEdge()
    {
        var master = NoiseFrame(1024, 1024, static (_, _) => 1.0);

        var scan = MasterAutoCrop.Scan(master, null, MasterAutoCropRule.ForModel(0.0, margin: 0.01));

        // 1 percent of 1024 rounds to 10 px a side.
        scan.Rect.ShouldBe(new PixelRect(10, 10, 1004, 1004));
        scan.Margin.ShouldBe(new MasterAutoCropApplied(Whole(master), scan.Rect));
        scan.MarginIgnored.ShouldBeFalse();
    }

    [Fact]
    public void AMarginThatWouldLeaveSixteenPixelsOrLessIsIgnored()
    {
        // 25 percent of 32 is 8 px a side, which leaves exactly 16.
        var master = NoiseFrame(32, 32, static (_, _) => 1.0);

        var scan = MasterAutoCrop.Scan(master, null, MasterAutoCropRule.ForModel(0.0, margin: 0.25));

        scan.MarginIgnored.ShouldBeTrue();
        scan.Margin.ShouldBeNull();
        scan.Rect.ShouldBe(Whole(master));
    }

    [Fact]
    public void AMarginInsetsTheCoverageTiersRectangleToo()
    {
        var master = NoiseFrame(256, 256, static (_, _) => 1.0);
        var path = MasterPath();
        var inner = new PixelRect(32, 48, 160, 144);
        IntegrationFitsWriter.WriteCoverageMap(path, CoveragePlane(256, 256, inner), frameCount: 24);

        var scan = MasterAutoCrop.Scan(master, path, MasterAutoCropRule.ForModel(0.0, margin: 0.05));

        scan.FromCoverage.ShouldBeTrue();
        scan.Margin.ShouldNotBeNull();
        scan.Rect.X.ShouldBeGreaterThan(inner.X);
        scan.Rect.Y.ShouldBeGreaterThan(inner.Y);
        scan.Rect.Width.ShouldBeLessThan(inner.Width);
        scan.Rect.Height.ShouldBeLessThan(inner.Height);
    }
}
