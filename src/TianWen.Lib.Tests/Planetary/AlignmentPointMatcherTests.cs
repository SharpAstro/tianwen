using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class AlignmentPointMatcherTests
{
    private const int N = 96;

    private static float[,] TexturedDisk(double cx, double cy, double radius)
    {
        var a = new float[N, N];
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if ((dx * dx) + (dy * dy) < radius * radius)
                {
                    // Smooth, band-limited texture: rich structure for phase correlation, yet near-lossless
                    // under bilinear resampling so the residual reflects misalignment, not interpolation loss.
                    var v = 0.5 + (0.25 * Math.Sin(x * 0.45) * Math.Cos(y * 0.40)) + (0.12 * Math.Sin((x + y) * 0.22));
                    a[y, x] = (float)v;
                }
                else
                {
                    a[y, x] = 0.02f;
                }
            }
        }

        return a;
    }

    private static float SampleBilinear(float[,] src, double x, double y)
    {
        int h = src.GetLength(0), w = src.GetLength(1);
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        if (x0 < 0 || y0 < 0 || x0 >= w - 1 || y0 >= h - 1)
        {
            return 0f;
        }

        var fx = x - x0;
        var fy = y - y0;
        return (float)((src[y0, x0] * (1 - fx) * (1 - fy))
            + (src[y0, x0 + 1] * fx * (1 - fy))
            + (src[y0 + 1, x0] * (1 - fx) * fy)
            + (src[y0 + 1, x0 + 1] * fx * fy));
    }

    // A smooth (linear-gradient) displacement: frame(x,y) = reference(x - dx, y - dy).
    private static (double Dx, double Dy) Field(int x, int y)
        => (-1.5 + (3.0 * x / (N - 1)), 1.0 - (2.0 * y / (N - 1)));

    private static float[,] Distort(float[,] reference)
    {
        var dst = new float[N, N];
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var (dx, dy) = Field(x, y);
                dst[y, x] = SampleBilinear(reference, x - dx, y - dy);
            }
        }

        return dst;
    }

    private static double MeanAbsDiff(Image a, Image b, int lo, int hi)
    {
        double sum = 0;
        var n = 0;
        for (var y = lo; y < hi; y++)
        {
            for (var x = lo; x < hi; x++)
            {
                var av = a[0, y, x];
                if (float.IsNaN(av))
                {
                    continue;
                }

                sum += Math.Abs(av - b[0, y, x]);
                n++;
            }
        }

        return n > 0 ? sum / n : double.MaxValue;
    }

    [Fact]
    public async Task Mesh_warp_reduces_smooth_distortion()
    {
        var reference = TexturedDisk(48, 48, 40);
        var frame = Distort(reference);
        var refImg = Image.FromChannel(reference);
        var frameImg = Image.FromChannel(frame);

        var region = PlanetaryDisk.BoundingBox(refImg);
        var aps = FeatureDetector.DetectAlignmentPoints(refImg, region, spacing: 16, maxPoints: 64, minGradientFraction: 0.1);
        aps.Length.ShouldBeGreaterThan(4);

        var matcher = AlignmentPointMatcher.FromReference(refImg, aps, patchSize: 32);
        var mesh = matcher.BuildMesh(frameImg, globalDx: 0, globalDy: 0, nodeSpacing: 16, influence: 24);
        var warped = await frameImg.WarpByMeshAsync(mesh, TestContext.Current.CancellationToken);

        // Compare over the disk interior (away from the edge, where warp samples can fall out of bounds).
        var frameErr = MeanAbsDiff(frameImg, refImg, 30, 66);
        var warpedErr = MeanAbsDiff(warped, refImg, 30, 66);

        warpedErr.ShouldBeLessThan(frameErr * 0.5); // the mesh more than halves the distortion residual
    }
}
