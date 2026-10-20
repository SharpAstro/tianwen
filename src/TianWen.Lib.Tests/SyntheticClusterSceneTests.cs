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
}
