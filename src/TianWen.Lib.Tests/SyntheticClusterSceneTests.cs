using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The synthetic cluster's two draws: the unresolved stars' truncated power law and King's projected profile.
/// </summary>
public sealed class SyntheticClusterSceneTests
{
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.97)]
    [InlineData(1.5)]
    public void ThePowerLawStaysInItsBoundsAtItsSlope(double slope)
    {
        var rng = new Random(7);
        const double lo = 0.1, hi = 1.0;
        var draws = Enumerable.Range(0, 200_000).Select(_ => SyntheticClusterScene.TruncatedPowerLaw(rng, lo, hi, slope)).ToArray();

        draws.ShouldAllBe(a => a >= lo && a <= hi);
        // N(>a) of a truncated power law: (a^-s - hi^-s) / (lo^-s - hi^-s), read at the geometric middle.
        var mid = Math.Sqrt(lo * hi);
        var expected = (Math.Pow(mid, -slope) - Math.Pow(hi, -slope)) / (Math.Pow(lo, -slope) - Math.Pow(hi, -slope));
        ((double)draws.Count(a => a > mid) / draws.Length).ShouldBe(expected, 0.005);
    }

    [Fact]
    public void KingPositionsStayInsideTheTidalRadiusAndGatherAtTheCore()
    {
        var rng = new Random(11);
        const double rc = 20, rt = 300;
        var radii = Enumerable.Range(0, 50_000).Select(_ => SyntheticClusterScene.KingPosition(rng, rc, rt)).Select(p => Math.Sqrt((p.X * p.X) + (p.Y * p.Y))).ToArray();

        radii.ShouldAllBe(r => r <= rt);
        // Surface density inside the core against a ring at five cores: King's profile falls by about an order of magnitude.
        static double Density(double[] r, double a, double b) => r.Count(x => x >= a && x < b) / (Math.PI * ((b * b) - (a * a)));
        (Density(radii, 0, rc) / Density(radii, 4 * rc, 6 * rc)).ShouldBeGreaterThan(8.0);
    }

    [Fact]
    public void KingsClosedFormLightAgreesWithItsDraws()
    {
        var rng = new Random(13);
        const double rc = 10, rt = 30;
        var radii = Enumerable.Range(0, 100_000).Select(_ => SyntheticClusterScene.KingPosition(rng, rc, rt)).Select(p => Math.Sqrt((p.X * p.X) + (p.Y * p.Y))).ToArray();

        foreach (var r in new[] { 5.0, 10.0, 20.0 })
        {
            ((double)radii.Count(x => x <= r) / radii.Length).ShouldBe(SyntheticClusterScene.KingLightWithin(r, rc, rt), 0.006);
        }
        SyntheticClusterScene.KingLightWithin(rt, rc, rt).ShouldBe(1.0, 1e-12);
    }

    /// <summary>A scene's knot list says where the turned tile draws each knot: a blob drawn in the tile at a knot's place is
    /// found, after <see cref="SyntheticClusterScene.Rotate"/>, where <see cref="SyntheticClusterScene.KnotsInScene"/> puts the
    /// knot, its axis turned with the sky and its peak at the nebula's strength.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(37.0)]
    [InlineData(90.0)]
    public void TheKnotListSaysWhereTheTurnedTileDrawsEachKnot(double degrees)
    {
        const int wide = 301, size = 201;
        const double kx = 190.0, ky = 118.0, sigma = 2.0;
        var tile = new float[wide * wide];
        for (var y = 0; y < wide; y++)
        {
            for (var x = 0; x < wide; x++)
            {
                tile[(y * wide) + x] = (float)Math.Exp(-0.5 * (((x - kx) * (x - kx)) + ((y - ky) * (y - ky))) / (sigma * sigma));
            }
        }
        var knot = new SyntheticBackground.Knot(kx, ky, sigma * 2, sigma, 0.25, [1.0, 2.0, 3.0]);

        var turned = SyntheticClusterScene.Rotate(tile, wide, size, degrees);
        var listed = SyntheticClusterScene.KnotsInScene([knot], wide, size, degrees, strength: 0.5);

        listed.Length.ShouldBe(1);
        var (sum, sx, sy) = (0.0, 0.0, 0.0);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var v = turned[(y * size) + x];
                (sum, sx, sy) = (sum + v, sx + (v * x), sy + (v * y));
            }
        }
        (sx / sum).ShouldBe(listed[0].X, 0.05, "the centroid of the turned blob is the listed centre");
        (sy / sum).ShouldBe(listed[0].Y, 0.05);
        listed[0].AngleRad.ShouldBe(0.25 + (degrees * Math.PI / 180.0), 1e-12);
        listed[0].Peak.ShouldBe([0.5, 1.0, 1.5]);
    }

    [Fact]
    public void AKnotTheSceneDoesNotShowIsNotListed()
        => SyntheticClusterScene.KnotsInScene([new SyntheticBackground.Knot(5.0, 5.0, 2.0, 1.0, 0.0, [1.0])], 301, 201, 0.0, 1.0).ShouldBeEmpty();

    [Fact]
    public void TheKnotListIsOneRowPerKnot()
    {
        var csv = SyntheticClusterRun.KnotsCsv([new SyntheticBackground.Knot(12.5, 40.25, 3.0, 1.5, Math.PI / 4, [0.1, 0.2, 0.3])]);

        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).ShouldBe([
            "x,y,sigma_major,sigma_minor,angle_deg,peak_r,peak_g,peak_b",
            "12.500,40.250,3.0000,1.5000,45.00,0.1,0.2,0.3",
        ]);
    }

    [Theory]
    [InlineData(2.0, 0.343)]
    [InlineData(3.0, 0.296)]
    [InlineData(10.0, 0.170)]
    public void KingsHalfLightRadiusIsTheTidalShareTabulated(double concentration, double halfLightOverTidal)
        // The shares a numerical integral of the profile gave when the groups' sizes were chosen.
        => (SyntheticClusterScene.KingHalfLightOverCore(concentration) / concentration).ShouldBe(halfLightOverTidal, 0.002);
}
