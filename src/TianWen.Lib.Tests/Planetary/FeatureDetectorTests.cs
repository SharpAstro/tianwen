using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class FeatureDetectorTests
{
    private static void AddBlob(float[,] a, double cx, double cy, double sigma, float peak)
    {
        int h = a.GetLength(0), w = a.GetLength(1);
        var twoSigma2 = 2.0 * sigma * sigma;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var d2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                a[y, x] += (float)(peak * Math.Exp(-d2 / twoSigma2));
            }
        }
    }

    [Fact]
    public void Detects_points_clustered_on_features()
    {
        var px = new float[64, 64];
        (double X, double Y)[] features = [(20, 20), (44, 30), (30, 50)];
        foreach (var (fx, fy) in features)
        {
            AddBlob(px, fx, fy, 1.6, 0.9f);
        }

        var aps = FeatureDetector.DetectAlignmentPoints(Image.FromChannel(px), new System.Drawing.Rectangle(0, 0, 64, 64), spacing: 16, maxPoints: 32, minGradientFraction: 0.2);

        aps.Length.ShouldBeGreaterThan(0);

        // Every AP lands near one of the (only) features -- the gradient is ~zero elsewhere.
        foreach (var p in aps)
        {
            var nearest = features.Min(f => Math.Sqrt(((p.X - f.X) * (p.X - f.X)) + ((p.Y - f.Y) * (p.Y - f.Y))));
            nearest.ShouldBeLessThan(6.0);
        }
    }

    [Fact]
    public void Uniform_image_yields_no_points()
    {
        var flat = new float[48, 48];
        Array.Clear(flat); // all zero -> no gradient

        var aps = FeatureDetector.DetectAlignmentPoints(Image.FromChannel(flat), new System.Drawing.Rectangle(0, 0, 48, 48));

        aps.ShouldBeEmpty();
    }
}
