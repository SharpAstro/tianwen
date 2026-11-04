using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary><see cref="ExpectedSkyModel"/> (#1974): a statistical expected sky, fitted robustly from mostly clear frames.</summary>
public class ExpectedSkyModelTests
{
    private const double LevelSd = 0.019;
    private const double GradientSd = 0.12;
    private const double VeilLevel = 0.0705; // ln 1.073
    private const double VeilGradient = 0.6206; // ln 1.86

    private static double NextNormal(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    private static SkyCovariates RandomCovariates(Random r, bool moonVaries = true)
    {
        var x = 1.0 + r.NextDouble() * 1.4;
        var west = r.NextDouble() < 0.5;
        var up = moonVaries && r.NextDouble() < 0.4;
        var alt = up ? 10 + r.NextDouble() * 60 : -20;
        return new SkyCovariates(0, 0, west ? 2 : -2, 0, 0, x, west, 0,
            alt, up ? 0.3 + 0.7 * r.NextDouble() : 0.5, 20 + r.NextDouble() * 120, 0, 0, -5 + 10 * r.NextDouble());
    }

    // The true law: log level and log gradient strength against the airmass, the meridian side, the Moon and the time of night.
    private static (double Level, double Gradient) Truth(in SkyCovariates c)
    {
        var moon = c.MoonAltitudeDeg > 0 ? c.MoonIllumination * Math.Exp(-c.MoonSeparationDeg / 40.0) : 0;
        var moonAlt = c.MoonAltitudeDeg > 0 ? c.MoonAltitudeDeg / 90.0 : 0;
        var x = c.Airmass;
        var west = c.West ? x - 1 : 0;
        return (
            6.5 + 0.30 * x - 0.04 * x * x + 0.15 * west + 0.6 * moon + 0.1 * moonAlt - 0.01 * c.HoursFromLocalMidnight,
            4.0 + 0.50 * x - 0.05 * x * x + 0.60 * west + 0.9 * moon + 0.2 * moonAlt + 0.02 * c.HoursFromLocalMidnight);
    }

    private static SkySample Draw(Random r, bool veiled = false, bool moonVaries = true)
    {
        var c = RandomCovariates(r, moonVaries);
        var (l, g) = Truth(c);
        l += LevelSd * NextNormal(r) + (veiled ? VeilLevel : 0);
        g += GradientSd * NextNormal(r) + (veiled ? VeilGradient : 0);
        return new SkySample(c, Math.Exp(l), Math.Exp(g));
    }

    [Fact]
    public void AFitToClearFramesRecoversTheLawWithinItsScatter()
    {
        var r = new Random(1974);
        var model = ExpectedSkyModel.Fit([.. Enumerable.Range(0, 800).Select(_ => Draw(r))]);

        model.ShouldNotBeNull();
        model.Terms.ShouldBe(ExpectedSkyModel.AllTerms.ToArray());
        model.LevelScatter.ShouldBe(LevelSd, LevelSd * 0.2);
        model.GradientScatter.ShouldBe(GradientSd, GradientSd * 0.2);
        for (var i = 0; i < 50; i++)
        {
            var c = RandomCovariates(r);
            var (l, g) = model.Expected(c);
            var truth = Truth(c);
            Math.Abs(l - truth.Level).ShouldBeLessThan(model.LevelScatter * 0.5);
            Math.Abs(g - truth.Gradient).ShouldBeLessThan(model.GradientScatter * 0.5);
        }
    }

    [Fact]
    public void AVeilOfSevenPercentInLevelAndEightySixInGradientReadsFiveOrMore()
    {
        var r = new Random(7);
        var model = ExpectedSkyModel.Fit([.. Enumerable.Range(0, 800).Select(_ => Draw(r))]);
        model.ShouldNotBeNull();

        var veiled = Enumerable.Range(0, 200).Select(_ => Draw(r, veiled: true)).Select(s => model.Excess(s.Covariates, s.Level, s.GradientStrength).Sum).Order().ToArray();
        var clear = Enumerable.Range(0, 200).Select(_ => Draw(r)).Select(s => model.Excess(s.Covariates, s.Level, s.GradientStrength).Sum).ToArray();

        veiled[veiled.Length / 2].ShouldBeGreaterThanOrEqualTo(5.0);
        veiled.Count(e => e >= 5.0).ShouldBeGreaterThan((int)(veiled.Length * 0.9));
        clear.Count(e => e >= 5.0).ShouldBeLessThanOrEqualTo(2);
        Math.Abs(clear.Average()).ShouldBeLessThan(0.5);
    }

    [Fact]
    public void ATenPercentMinorityOfVeiledFramesDoesNotMoveTheFit()
    {
        var r = new Random(11);
        var samples = Enumerable.Range(0, 1000).Select(i => Draw(r, veiled: i % 10 == 0)).ToArray();
        var contaminated = ExpectedSkyModel.Fit(samples);
        contaminated.ShouldNotBeNull();

        for (var i = 0; i < 50; i++)
        {
            var c = RandomCovariates(r);
            var (l, g) = contaminated.Expected(c);
            var truth = Truth(c);
            Math.Abs(l - truth.Level).ShouldBeLessThan(0.012);
            Math.Abs(g - truth.Gradient).ShouldBeLessThan(0.07);
        }
        contaminated.LevelScatter.ShouldBe(LevelSd, LevelSd * 0.25);
        contaminated.FrameCount.ShouldBeLessThan(contaminated.OfferedCount);
    }

    [Fact]
    public void ATermWhoseCovariateNeverVariesIsLeftOut()
    {
        var r = new Random(3);
        // One side of the meridian only and no Moon: the west and Moon terms have nothing to fit.
        var samples = Enumerable.Range(0, 300).Select(_ =>
        {
            var s = Draw(r, moonVaries: false);
            var c = s.Covariates with { West = false, MoonAltitudeDeg = -30 };
            return new SkySample(c, s.Level, s.GradientStrength);
        }).ToArray();

        var model = ExpectedSkyModel.Fit(samples);

        model.ShouldNotBeNull();
        model.Terms.ShouldNotContain("west*(X-1)");
        model.Terms.ShouldNotContain("moon*falloff");
        model.Terms.ShouldNotContain("moonAlt");
        model.Terms.ShouldContain("X");
        model.LevelCoefficients.Length.ShouldBe(model.Terms.Length);
    }

    [Fact]
    public void TooFewFramesGiveNoModel()
    {
        var r = new Random(5);
        ExpectedSkyModel.Fit([.. Enumerable.Range(0, 5).Select(_ => Draw(r))]).ShouldBeNull();
    }

    [Fact]
    public void AFrameWithoutAnAirmassHasNoExcess()
    {
        var r = new Random(9);
        var model = ExpectedSkyModel.Fit([.. Enumerable.Range(0, 200).Select(_ => Draw(r))]);
        model.ShouldNotBeNull();

        var c = RandomCovariates(r) with { Airmass = double.NaN };
        var e = model.Excess(c, 1000, 10);

        e.LevelSigma.ShouldBe(double.NaN);
        e.Sum.ShouldBe(double.NaN);
    }

    [Fact]
    public void TheModelRoundTripsThroughJson()
    {
        var r = new Random(13);
        var model = ExpectedSkyModel.Fit([.. Enumerable.Range(0, 200).Select(_ => Draw(r))]);
        model.ShouldNotBeNull();

        // The store writes through named floating-point literals, since a model with no zero point carries NaN there.
        var options = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var json = JsonSerializer.Serialize(model, options);
        var back = JsonSerializer.Deserialize<ExpectedSkyModel>(json, options);

        back.ShouldNotBeNull();
        back.Version.ShouldBe(ExpectedSkyModel.CurrentVersion);
        back.Terms.ShouldBe(model.Terms);
        back.LevelCoefficients.ShouldBe(model.LevelCoefficients);
        back.GradientCoefficients.ShouldBe(model.GradientCoefficients);
        back.LevelScatter.ShouldBe(model.LevelScatter);
        back.FrameCount.ShouldBe(model.FrameCount);
    }
}
