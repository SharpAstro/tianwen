using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The speckle measure (docs/plans/star-remover-training.md, "The speckle teacher"): a dark core pixel at a site is found
/// where a core's mean, which the hole test reads, is left at the sky; a clean plate reads near its null; a site too near
/// the edge is not read.
/// </summary>
[Collection("Imaging")]
public class StarlessSpecklesTests(ITestOutputHelper output)
{
    private const int Size = 400;
    private const float Sky = 0.1f;
    private const float Sigma = 0.002f;

    [Fact]
    public void ADarkPixelBesideABrightOneIsASpeckleTheCoresMeanHides()
    {
        var plane = Noise(seed: 1);
        var sites = new List<(float X, float Y, float Significance)>();
        var rng = new Random(2);
        for (var k = 0; k < 100; k++)
        {
            var x = 30 + rng.Next(Size - 60);
            var y = 30 + rng.Next(Size - 60);
            // Half the sites carry a misfit's dipole: one pixel 8 sigma down beside one 8 sigma up, mean unchanged (at 6, the
            // noise lifts one dip in fifty back over the 4 sigma line).
            if (k % 2 == 0)
            {
                plane[(y * Size) + x] -= 8 * Sigma;
                plane[(y * Size) + x + 1] += 8 * Sigma;
            }
            sites.Add((x + 0.4f, y, k % 2 == 0 ? 50f : 500f));
        }
        var report = StarlessSpeckles.Measure(plane, Size, Size, null, sites, [.. sites.Select(static s => (s.X, s.Y))]);
        foreach (var band in report.Bands)
        {
            output.WriteLine($"band {band.MinSignificance}-{band.MaxSignificance}: {band.Speckled}/{band.Sites}");
        }
        output.WriteLine($"null {report.Null.Speckled}/{report.Null.Sites}");

        var misfit = report.Bands.Single(static b => b.MinSignificance == 20f);
        var clean = report.Bands.Single(static b => b.MinSignificance == 100f);
        misfit.Sites.ShouldBe(50);
        misfit.Rate.ShouldBe(1f, "every dipole's dark pixel is 8 sigma down");
        clean.Rate.ShouldBeLessThan(0.05f, "noise alone puts a core pixel 4 sigma down rarely");
        report.Null.Sites.ShouldBe(StarlessSpeckles.NullSites);
        report.Null.Rate.ShouldBeLessThan(0.05f);
    }

    [Fact]
    public void ASiteTooNearTheEdgeIsNotRead()
    {
        var plane = Noise(seed: 3);
        var report = StarlessSpeckles.Measure(plane, Size, Size, null, [(5f, 200f, 50f), (200f, 200f, 50f)], []);
        report.Bands.Sum(static b => b.Sites).ShouldBe(1);
    }

    private static float[] Noise(int seed)
    {
        var rng = new Random(seed);
        var plane = new float[Size * Size];
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = Sky + (Sigma * (float)StarInjection.Gaussian(rng));
        }
        return plane;
    }
}
