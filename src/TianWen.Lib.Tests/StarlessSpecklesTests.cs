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
    public void ASiteIsJudgedAgainstTheNullOfItsOwnBackground()
    {
        // The left half a smooth sky, the right half a nebula's texture (a 4 sigma ripple 24 px in period), which alone puts
        // core pixels 4 sigma under a ring's median. Dipoles only at the smooth sites; the textured sites are clean.
        var plane = Noise(seed: 5);
        const int Half = Size / 2;
        for (var y = 0; y < Size; y++)
        {
            for (var x = Half; x < Size; x++)
            {
                plane[(y * Size) + x] += (float)(4 * Sigma * Math.Sin(2 * Math.PI * x / 24.0) * Math.Sin(2 * Math.PI * y / 24.0));
            }
        }
        // The master's field, as the builder hands it over: the sky map's spread over the pixels' own noise, 1 on the left,
        // 2.5 on the right.
        var spread = new float[Size * Size];
        var noise = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                noise[(y * Size) + x] = Sigma;
                spread[(y * Size) + x] = x < Half ? Sigma : 2.5f * Sigma;
            }
        }
        var field = new TextureField(spread, noise, Size);
        var sites = new List<(float X, float Y, float Significance)>();
        var rng = new Random(6);
        for (var k = 0; k < 120; k++)
        {
            var left = k % 2 == 0;
            var x = left ? 20 + rng.Next(Half - 40) : Half + 20 + rng.Next(Half - 40);
            var y = 20 + rng.Next(Size - 40);
            if (left)
            {
                plane[(y * Size) + x] -= 8 * Sigma;
                plane[(y * Size) + x + 1] += 8 * Sigma;
            }
            sites.Add((x + 0.4f, y, 50f));
        }
        var sources = sites.Select(static s => (s.X, s.Y)).ToList();

        var plain = StarlessSpeckles.Measure(plane, Size, Size, null, sites, sources);
        var split = StarlessSpeckles.Measure(plane, Size, Size, null, sites, sources, texture: field);
        foreach (var c in split.Backgrounds)
        {
            output.WriteLine($"texture {c.MinTexture}-{c.MaxTexture}: sites {c.Bands.Sum(static b => b.Sites)}, speckled {c.Bands.Sum(static b => b.Speckled)}, null {c.Null.Speckled}/{c.Null.Sites}");
        }
        output.WriteLine($"frame: null {split.Null.Speckled}/{split.Null.Sites}");

        plain.Backgrounds.IsDefaultOrEmpty.ShouldBeTrue();
        split.Null.ShouldBe(plain.Null, "the frame-wide null keeps its positions whether or not a field is given");
        split.Bands.ShouldBe(plain.Bands);
        split.Backgrounds.Length.ShouldBe(StarlessSpeckles.TextureEdges.Length);
        var smooth = split.Backgrounds[0];
        var strong = split.Backgrounds[^1];
        smooth.Bands.Sum(static b => b.Sites).ShouldBe(60);
        strong.Bands.Sum(static b => b.Sites).ShouldBe(60);
        split.Backgrounds[1].Bands.Sum(static b => b.Sites).ShouldBe(0);
        smooth.Bands.Sum(static b => b.Speckled).ShouldBe(60, "every smooth site carries a dipole");
        smooth.Null.Rate.ShouldBeLessThan(0.05f);
        // The textured class's own null is high, so its clean sites are not a remover's fault: they read at that null.
        strong.Null.Rate.ShouldBeGreaterThan(5 * Math.Max(smooth.Null.Rate, 0.01f));
        var strongRate = (float)strong.Bands.Sum(static b => b.Speckled) / strong.Bands.Sum(static b => b.Sites);
        strongRate.ShouldBeLessThan(2 * strong.Null.Rate + 0.1f);
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
