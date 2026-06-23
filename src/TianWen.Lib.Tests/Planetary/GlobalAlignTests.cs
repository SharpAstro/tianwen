using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class GlobalAlignTests
{
    private static Image GaussianImage(int w, int h, double cx, double cy, double sigma)
    {
        var a = new float[h, w];
        var twoSigma2 = 2.0 * sigma * sigma;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var d2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                a[y, x] = (float)Math.Exp(-d2 / twoSigma2);
            }
        }

        return Image.FromChannel(a);
    }

    [Fact]
    public void CenterOfMass_finds_blob_centre()
    {
        var img = GaussianImage(64, 64, 40.3, 22.7, 4.0);

        var (x, y) = PlanetaryDisk.CenterOfMass(img, PlanetaryDisk.BoundingBox(img));

        x.ShouldBe(40.3, 0.5);
        y.ShouldBe(22.7, 0.5);
    }

    [Theory]
    [InlineData(5.0, 3.0)]
    [InlineData(-4.0, 6.0)]
    [InlineData(0.4, -0.7)]
    [InlineData(0.0, 0.0)]
    public void GlobalAligner_recovers_blob_shift(double sx, double sy)
    {
        var reference = GaussianImage(64, 64, 32, 32, 4.0);
        var moving = GaussianImage(64, 64, 32 + sx, 32 + sy, 4.0);

        var aligner = GlobalAligner.FromReference(reference, PlanetaryDisk.BoundingBox(reference), 64);
        var shift = aligner.Estimate(moving, PlanetaryDisk.BoundingBox(moving));

        // moving's planet sits at reference + (sx, sy), so Estimate returns (sx, sy).
        shift.Dx.ShouldBe(sx, 0.3);
        shift.Dy.ShouldBe(sy, 0.3);
    }
}
