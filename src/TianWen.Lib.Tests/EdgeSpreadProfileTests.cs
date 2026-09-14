using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Sources;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The extended-object reading on synthetic objects whose blur is known: a thin shell and a filled
/// disc, each rendered sharp and then blurred, so the rim's FWHM and the edge's 10 to 90 width can be
/// asserted against the blur that made them.
/// </summary>
[Collection("Imaging")]
public class EdgeSpreadProfileTests(ITestOutputHelper output)
{
    private const int W = 400;
    private const int H = 400;
    private const float Sky = 0.05f;
    private const float Noise = 0.001f;

    [Theory]
    [InlineData(1.5f)]
    [InlineData(3.0f)]
    public void AShellsRimWidthReadsItsBlur(float blurSigma)
    {
        // A ring of radius 80 and thickness 4, blurred: the rim's FWHM in radius is the ring's width
        // convolved with the blur, about sqrt(4^2 + (2.355 sigma)^2).
        var plane = Render((x, y) =>
        {
            var r = MathF.Sqrt((x - 200) * (x - 200f) + (y - 200) * (y - 200f));
            return MathF.Abs(r - 80f) <= 2f ? 0.05f : 0f;
        }, blurSigma, out var rng);

        var map = BackgroundMap.Estimate(plane, W, H, new BackgroundMapOptions(BlockSize: 50));
        var seg = SourceSegmentation.Detect(plane, W, H, map, new SourceDetectionOptions(Deblend: false));
        var shell = seg.Segments.OrderByDescending(s => s.Area).First();
        shell.IsCompact.ShouldBeFalse();

        var reading = EdgeSpreadProfile.Measure(plane, W, H, seg, shell.Label);
        var expected = MathF.Sqrt(16f + MathF.Pow(2.3548f * blurSigma, 2f));
        output.WriteLine($"blur {blurSigma}: rim width {reading.RimWidth:F1} px (expected about {expected:F1}), contrast {reading.RimContrast:E2}, dip {reading.OutsideDipSigma:+0.00}, sectors {reading.SectorsRead}, noise out {reading.NoiseOutside:E2}");
        reading.SectorsRead.ShouldBeGreaterThan(30, "a full ring reads in nearly every sector");
        reading.RimWidth.ShouldBeInRange(expected - 1.5f, expected + 1.5f, "the rim's FWHM in radius follows the blur");
        reading.OutsideDipSigma.ShouldBeGreaterThan(-2f, "a blurred shell has no ring outside it");
        reading.NoiseOutside.ShouldBeInRange(0.5f * Noise, 1.5f * Noise);
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(3.0f)]
    public void AFilledDiscsEdgeWidthReadsItsBlur(float blurSigma)
    {
        // A disc of radius 80, blurred: the 10 to 90 percent edge width of a Gaussian-blurred step is 2.56 sigma.
        var plane = Render((x, y) =>
        {
            var r = MathF.Sqrt((x - 200) * (x - 200f) + (y - 200) * (y - 200f));
            return r <= 80f ? 0.03f : 0f;
        }, blurSigma, out _);

        var map = BackgroundMap.Estimate(plane, W, H, new BackgroundMapOptions(BlockSize: 50));
        var seg = SourceSegmentation.Detect(plane, W, H, map, new SourceDetectionOptions(Deblend: false));
        var disc = seg.Segments.OrderByDescending(s => s.Area).First();

        var reading = EdgeSpreadProfile.Measure(plane, W, H, seg, disc.Label);
        var expected = 2.563f * blurSigma;
        output.WriteLine($"blur {blurSigma}: edge width {reading.EdgeWidth:F1} px (expected about {expected:F1}), sectors {reading.SectorsRead}, rim {reading.RimWidth}");
        reading.SectorsRead.ShouldBeGreaterThan(30);
        reading.EdgeWidth.ShouldBeInRange(expected - 1.5f, expected + 2f, "the 10 to 90 edge width follows the blur");
    }

    [Fact]
    public void ASharperShellReadsNarrower()
    {
        var sharp = Render(Ring, 1.0f, out _);
        var soft = Render(Ring, 3.0f, out _);
        var map = BackgroundMap.Estimate(sharp, W, H, new BackgroundMapOptions(BlockSize: 50));
        var seg = SourceSegmentation.Detect(sharp, W, H, map, new SourceDetectionOptions(Deblend: false));
        var label = seg.Segments.OrderByDescending(s => s.Area).First().Label;
        var a = EdgeSpreadProfile.Measure(sharp, W, H, seg, label);
        var b = EdgeSpreadProfile.Measure(soft, W, H, seg, label);
        output.WriteLine($"sharp rim {a.RimWidth:F1}, soft rim {b.RimWidth:F1}");
        a.RimWidth.ShouldBeLessThan(b.RimWidth - 2f, "the same segment read on the softer rendering is wider");

        static float Ring(int x, int y)
        {
            var r = MathF.Sqrt((x - 200) * (x - 200f) + (y - 200) * (y - 200f));
            return MathF.Abs(r - 80f) <= 2f ? 0.05f : 0f;
        }
    }

    private static float[] Render(Func<int, int, float> truth, float blurSigma, out Random rng)
    {
        rng = new Random(3);
        var sharp = new float[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                sharp[y * W + x] = truth(x, y);
            }
        }

        var blurred = new float[W * H];
        SourceSegmentation.SmoothForDetection(sharp, W, H, blurSigma, blurred);
        for (var i = 0; i < blurred.Length; i++)
        {
            blurred[i] += Sky + Noise * Gaussian(rng);
        }

        return blurred;
    }

    private static float Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
}
