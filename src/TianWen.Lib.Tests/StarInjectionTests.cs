using Shouldly;
using System;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The star injector's pieces (docs/plans/star-remover-training.md, R1): the profile an injected star is drawn with, the
/// renderer that puts it into a plate (a saturated one as a stack of clipped subs), and the population that decides where
/// a cell's stars go and how bright they are.
/// </summary>
[Collection("Imaging")]
public class StarInjectionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(StarProfileFamily.Moffat, 2.5, 3.0)]
    [InlineData(StarProfileFamily.Gaussian, 2.5, 3.0)]
    [InlineData(StarProfileFamily.Moffat, 1.6, 8.0)]
    public void ARoundProfileIsHalfItsPeakAtHalfItsFwhm(StarProfileFamily family, double fwhm, double beta)
    {
        var profile = StarProfile.Round(family, fwhm, beta);
        profile.At(0, 0).ShouldBe(1.0, 1e-12);
        profile.At(fwhm / 2, 0).ShouldBe(0.5, 1e-9);
        profile.At(0, fwhm / 2).ShouldBe(0.5, 1e-9);
        profile.RadiusAtFraction(0.5).ShouldBe(fwhm / 2, 1e-9);
    }

    [Fact]
    public void AnElongatedProfileKeepsItsAreaAndReadsItsAxisRatioInItsMoments()
    {
        const double q = 0.7;
        var angle = 30.0 * Math.PI / 180.0;
        var profile = new StarProfile(StarProfileFamily.Moffat, 3.0, 3.0, q, angle);

        // Half maximum along the major axis at r / sqrt(q), along the minor at r sqrt(q): the geometric mean is the FWHM.
        var (sin, cos) = Math.SinCos(angle);
        var major = 1.5 / Math.Sqrt(q);
        var minor = 1.5 * Math.Sqrt(q);
        profile.At(major * cos, major * sin).ShouldBe(0.5, 1e-9);
        profile.At(-minor * sin, minor * cos).ShouldBe(0.5, 1e-9);

        const int size = 64;
        var plane = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                plane[y * size + x] = (float)profile.PixelMean(x, y, 32.3, 31.8);
            }
        }
        var m = InjectionPopulation.Moment(plane, size, size, 32.3, 31.8, 12.0);
        m.ShouldNotBeNull();
        output.WriteLine($"e {m.Value.E:F3} against {Math.Sqrt(1 - q * q):F3}, angle {m.Value.Theta * 180 / Math.PI:F1} deg");
        m.Value.E.ShouldBe(Math.Sqrt(1 - q * q), 0.05);
        (m.Value.Theta * 180 / Math.PI).ShouldBe(30.0, 2.0);
    }

    [Theory]
    // The three-point rule's error is the core pixel's and grows as the star sharpens: measured 1.3e-4 of the peak at
    // 1.8 px FWHM and 1e-5 at 3 px, against a 64 by 64 midpoint reference.
    [InlineData(1.8, 2.5, 0.8, 2e-4)]
    [InlineData(3.0, 3.0, 0.7, 2e-5)]
    public void APixelMeanIntegratesTheProfileOverThePixel(double fwhm, double beta, double axisRatio, double bound)
    {
        var profile = new StarProfile(StarProfileFamily.Moffat, fwhm, beta, axisRatio, 0.4);
        var worst = 0.0;
        for (var py = -6; py <= 6; py++)
        {
            for (var px = -6; px <= 6; px++)
            {
                double reference = 0;
                for (var j = 0; j < 64; j++)
                {
                    for (var i = 0; i < 64; i++)
                    {
                        reference += profile.At(px - 0.2 + (i + 0.5) / 64.0 - 0.5, py + 0.1 + (j + 0.5) / 64.0 - 0.5);
                    }
                }
                reference /= 64.0 * 64.0;
                worst = Math.Max(worst, Math.Abs(profile.PixelMean(px, py, 0.2, -0.1) - reference));
            }
        }
        output.WriteLine($"worst pixel-mean error {worst:E2} of the peak");
        worst.ShouldBeLessThan(bound);
    }

    [Fact]
    public void AnUnsaturatedStarIsItsProfileTimesItsAmplitudeAndNothingElse()
    {
        const int size = 48;
        var plate = new[] { Filled(size, 0.1f), Filled(size, 0.2f) };
        var profiles = ImmutableArray.Create(StarProfile.Round(StarProfileFamily.Moffat, 2.4, 3.0), StarProfile.Round(StarProfileFamily.Moffat, 2.8, 2.5));
        var star = new InjectedStar(20.3, 25.7, [0.05, 0.02], profiles, false, []);
        var render = StarInjection.Render(plate, size, size, null, [star], [1e-7, 1e-7], new Random(1));
        for (var c = 0; c < 2; c++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var expected = plate[c][y * size + x] + star.Amplitudes[c] * profiles[c].PixelMean(x, y, star.X, star.Y);
                    render.Planes[c][y * size + x].ShouldBe((float)expected, 1e-6f);
                    render.UnclippedFraction[c][y * size + x].ShouldBe(1f);
                }
            }
        }
    }

    [Fact]
    public void ASaturatedStarClipsAsAStackDoesAndItsPlateauIsQuiet()
    {
        const int size = 64;
        var plate = new[] { Filled(size, 0.05f), Filled(size, 0.06f), Filled(size, 0.04f) };
        var profiles = Enumerable.Repeat(StarProfile.Round(StarProfileFamily.Moffat, 2.5, 2.8), 3).ToImmutableArray();
        // Green clips first: its clip is the lowest over its sky against the brightest star.
        var star = new InjectedStar(32.0, 31.5, [4.0, 6.0, 3.0], profiles, true, [0.9, 0.7, 0.95]);
        var absent = new BitMatrix(size, size);
        absent[2, 2] = true;
        var render = StarInjection.Render(plate, size, size, absent, [star], [1e-6, 1e-6, 1e-6], new Random(7));

        for (var c = 0; c < 3; c++)
        {
            var plane = render.Planes[c];
            plane.Max().ShouldBeLessThanOrEqualTo((float)star.ClipLevels[c] + 1e-6f, $"channel {c} passed its clip");
            plane[32 * size + 32].ShouldBe((float)star.ClipLevels[c], 1e-6f, $"channel {c}'s core is not at its clip");
            render.UnclippedFraction[c][32 * size + 32].ShouldBe(0f);
            render.UnclippedFraction[c][5 * size + 5].ShouldBe(1f);
            plane[2 * size + 2].ShouldBe(plate[c][2 * size + 2], "an absent pixel was written");

            // A stack's plateau has a soft edge: some pixel sits strictly between the clip and the unclipped star.
            var soft = Enumerable.Range(0, size * size).Count(i => render.UnclippedFraction[c][i] is > 0f and < 1f);
            output.WriteLine($"channel {c}: plateau {Enumerable.Range(0, size * size).Count(i => plane[i] >= star.ClipLevels[c] - 1e-6)} px, soft edge {soft} px");
            soft.ShouldBeGreaterThan(0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStarIsRenderedOutToItsReachAndNoFarther(bool saturated)
    {
        const int size = 96;
        const double floor = 1e-4;
        var plate = new[] { Filled(size, 0.05f) };
        var profile = StarProfile.Round(StarProfileFamily.Moffat, 2.5, 3.0);
        var star = new InjectedStar(48.3, 47.6, [1.0], [profile], saturated, saturated ? [0.6] : []);
        var render = StarInjection.Render(plate, size, size, null, [star], [floor], new Random(2));

        // The reach the renderer documents: where the light falls under the floor; a saturated star's subs scatter in
        // amplitude and width, so its reach is taken at 1.2 times both, and a pixel further for their offsets.
        var reach = saturated
            ? Math.Ceiling(profile.Scaled(1.2).RadiusAtFraction(floor / 1.2)) + 1
            : Math.Ceiling(profile.RadiusAtFraction(floor));
        var farthest = 0.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (render.Planes[0][(y * size) + x] != plate[0][(y * size) + x])
                {
                    farthest = Math.Max(farthest, Math.Sqrt(((x - star.X) * (x - star.X)) + ((y - star.Y) * (y - star.Y))));
                }
            }
        }
        output.WriteLine($"reach {reach} px, farthest pixel written {farthest:F1} px");
        farthest.ShouldBeLessThanOrEqualTo(reach, "a pixel past the reach was written: the box's corners");
        farthest.ShouldBeGreaterThan(0.8 * reach);
    }

    [Fact]
    public void ANeighbourOnASaturatedPlateauClipsWithIt()
    {
        // The sensor clips the sum: a bright unsaturated star 2 px off a saturated one's centre must not stand above the clip.
        const int size = 64;
        const double clip = 0.5;
        var plate = new[] { Filled(size, 0.05f) };
        var profile = StarProfile.Round(StarProfileFamily.Moffat, 2.5, 3.0);
        var saturated = new InjectedStar(32.0, 32.0, [30.0], [profile], true, [clip]);
        var neighbour = new InjectedStar(34.0, 32.0, [0.8], [profile], false, []);
        var render = StarInjection.Render(plate, size, size, null, [neighbour, saturated], [1e-6], new Random(5));
        var peak = render.Planes[0].Max();
        output.WriteLine($"peak {peak:F4} against the clip {clip}");
        peak.ShouldBeLessThanOrEqualTo((float)clip + 1e-6f);
    }

    [Fact]
    public void TheSameSeedGivesTheSameSaturatedStar()
    {
        const int size = 40;
        var plate = new[] { Filled(size, 0.05f) };
        var star = new InjectedStar(20, 20, [5.0], [StarProfile.Round(StarProfileFamily.Moffat, 2.5, 3.0)], true, [0.8]);
        var a = StarInjection.Render(plate, size, size, null, [star], [1e-6], new Random(3));
        var b = StarInjection.Render(plate, size, size, null, [star], [1e-6], new Random(3));
        a.Planes[0].ShouldBe(b.Planes[0]);
    }

    [Fact]
    public void ARandomStarKeepsAwayFromEverySubtractedSiteAndTheCountIsTheCellsOwn()
    {
        var (population, catalogue) = BuildPopulation();
        var sites = catalogue.Where(static s => s.Outcome == StarFitOutcome.Subtracted).ToArray();
        var inCell = sites.Count(static s => s.X is >= 64 and < 192 && s.Y is >= 64 and < 192);
        var plan = population.Plan(64, 64, 128, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.0, new Random(5));
        plan.Requested.ShouldBe(inCell);
        plan.Placed.ShouldBe(inCell, "an uncrowded cell has room for every star");
        foreach (var star in plan.Stars)
        {
            var nearest = sites.Min(s => Math.Sqrt((s.X - star.X) * (s.X - star.X) + (s.Y - star.Y) * (s.Y - star.Y)));
            nearest.ShouldBeGreaterThanOrEqualTo(InjectionPopulation.ExclusionFwhm * 2.5);
            star.X.ShouldBeInRange(63.5, 191.5);
            star.Y.ShouldBeInRange(63.5, 191.5);
            star.Saturated.ShouldBeFalse();
        }
    }

    [Fact]
    public void AnAtSiteStarSitsOnASubtractedSite()
    {
        var (population, catalogue) = BuildPopulation();
        var sites = catalogue.Where(static s => s.Outcome == StarFitOutcome.Subtracted).ToArray();
        var plan = population.Plan(64, 64, 128, 0, InjectionPlacement.AtSite, StarProfileFamily.Moffat, 0.0, new Random(6));
        plan.Stars.Length.ShouldBe(plan.Requested);
        foreach (var star in plan.Stars)
        {
            sites.Min(s => Math.Sqrt((s.X - star.X) * (s.X - star.X) + (s.Y - star.Y) * (s.Y - star.Y))).ShouldBeLessThanOrEqualTo(0.36);
        }
    }

    [Fact]
    public void ASaturatedInjectionTakesAMastersOwnSaturatedStar()
    {
        var (population, _) = BuildPopulation();
        population.SaturatedPool.ShouldBe(1);
        var plan = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Gaussian, 1.0, new Random(8));
        var saturated = plan.Stars.Where(static s => s.Saturated).ToArray();
        saturated.Length.ShouldBe(1);
        plan.SaturatedFallback.ShouldBeFalse();
        // The saturated star of the catalogue sits on a core of 0.9 in channel 0 (the master's own pixels).
        saturated[0].ClipLevels[0].ShouldBe(0.9, 1e-6);
        saturated[0].Amplitudes[0].ShouldBe(3.0, 1e-6);
        saturated[0].Profiles.ShouldAllBe(static p => p.Family == StarProfileFamily.Gaussian);
    }

    [Fact]
    public void OnlyASaturatedStarSubtractedWithTheFieldsMoffatIsInTheSaturatedPool()
    {
        // The pool and every measure of the master's own saturated stars share this rule: a giant's profile or a pair (the
        // SMC's extended cores were fitted with the profile) is no star the injector reproduces.
        static FittedStar Star(StarFitModel model, bool saturated, StarFitOutcome outcome = StarFitOutcome.Subtracted)
            => new FittedStar(10, 10, 200, 1f, 1f, 0.1f, 0.01f, outcome, saturated, false, float.NaN, float.NaN, false, float.NaN, model, [1f]);
        InjectionPopulation.InSaturatedPool(Star(StarFitModel.Moffat, saturated: true), 1).ShouldBeTrue();
        InjectionPopulation.InSaturatedPool(Star(StarFitModel.Profile, saturated: true), 1).ShouldBeFalse();
        InjectionPopulation.InSaturatedPool(Star(StarFitModel.Pair, saturated: true), 1).ShouldBeFalse();
        InjectionPopulation.InSaturatedPool(Star(StarFitModel.Moffat, saturated: false), 1).ShouldBeFalse();
        InjectionPopulation.InSaturatedPool(Star(StarFitModel.Moffat, saturated: true, StarFitOutcome.Knot), 1).ShouldBeFalse();
        InjectionPopulation.InSaturatedPool(Star(StarFitModel.Moffat, saturated: true), 3).ShouldBeFalse("an amplitude per channel or none");
    }

    [Fact]
    public void AnInjectedStarIsElongatedAsTheMastersStarsAroundItAre()
    {
        const int size = 256;
        const double q = 0.6;
        const double amplitude = 0.5;
        var angle = -20.0 * Math.PI / 180.0;
        var shape = new StarProfile(StarProfileFamily.Moffat, 2.5, 3.0, q, angle);
        var masterPlane = new float[size, size];
        var platePlane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                masterPlane[y, x] = 0.1f;
                platePlane[y, x] = 0.1f;
            }
        }
        var catalogue = ImmutableArray.CreateBuilder<FittedStar>();
        for (var gy = 24; gy < size - 16; gy += 40)
        {
            for (var gx = 24; gx < size - 16; gx += 40)
            {
                var cx = gx + 0.3;
                var cy = gy - 0.2;
                for (var y = gy - 14; y <= gy + 14; y++)
                {
                    for (var x = gx - 14; x <= gx + 14; x++)
                    {
                        masterPlane[y, x] += (float)(amplitude * shape.PixelMean(x, y, cx, cy));
                    }
                }
                catalogue.Add(new FittedStar((float)cx, (float)cy, 100, (float)amplitude, 1f, 0.1f, 0.01f, StarFitOutcome.Subtracted, false, false,
                    float.NaN, float.NaN, false, float.NaN, StarFitModel.Moffat, [(float)amplitude]));
            }
        }
        var master = new Image([masterPlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var plate = new Image([platePlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var population = InjectionPopulation.Build(catalogue.ToImmutable(), master, plate, 1.0, [(2.5, 3.0)], null);

        population.MomentPool.ShouldBe(catalogue.Count);
        var (axisRatio, angleRad) = population.ElongationAt(128, 128);
        output.WriteLine($"axis ratio {axisRatio:F3} against {q:F3}, angle {angleRad * 180 / Math.PI:F1} deg against -20.0");
        // Read through the moment's window alone, this field's stars came out at 0.677: the window rounds them.
        axisRatio.ShouldBe(q, 0.02);
        (angleRad * 180 / Math.PI).ShouldBe(-20.0, 3.0);
        population.ProfilesAt(128, 128, StarProfileFamily.Moffat)[0].AxisRatio.ShouldBe(axisRatio);
    }

    [Fact]
    public void ACatalogueFromBeforeTheAmplitudesCannotSeedAPopulation()
    {
        var (_, catalogue) = BuildPopulation();
        var old = catalogue.Select(static s => s with { Model = StarFitModel.None, ChannelAmplitudes = [] }).ToImmutableArray();
        var image = Image(256, 0.1f);
        Should.Throw<InvalidOperationException>(() => InjectionPopulation.Build(old, image, image, 1.0, [(2.5, 3.0)], null));
    }

    [Theory]
    [InlineData(StarProfileFamily.Moffat, 3.0, 3.0, 0.8)]
    [InlineData(StarProfileFamily.Moffat, 2.2, 2.4, 0.9)]
    [InlineData(StarProfileFamily.Gaussian, 3.0, 3.0, 1.0)]
    public void AStarsShapeIsFittedBackOffItsNoise(StarProfileFamily family, double fwhm, double beta, double q)
    {
        const int size = 48;
        const double sigma = 0.002;
        var profile = new StarProfile(family, fwhm, beta, q, 0.5);
        var rng = new Random(9);
        var plane = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                plane[(y * size) + x] = (float)((0.2 * profile.PixelMean(x, y, 24.3, 23.6)) + (sigma * StarInjection.Gaussian(rng)));
            }
        }
        var fit = InjectionMeasure.FitMoffat(plane, size, size, 24.0, 24.0, fwhm * 1.2);
        fit.ShouldNotBeNull();
        output.WriteLine($"{family} {fwhm}/{beta}/{q}: fitted FWHM {fit.Value.FwhmPx:F3}, beta {fit.Value.Beta:F2}, q {fit.Value.AxisRatio:F3}, " +
            $"at ({fit.Value.X:F2}, {fit.Value.Y:F2}), converged {fit.Value.Converged}");
        fit.Value.FwhmPx.ShouldBe(fwhm, fwhm * 0.03);
        fit.Value.AxisRatio.ShouldBe(q, 0.03);
        fit.Value.X.ShouldBe(24.3, 0.05);
        if (family == StarProfileFamily.Moffat)
        {
            fit.Value.Beta.ShouldBe(beta, beta * 0.15);
        }
        else
        {
            fit.Value.Beta.ShouldBeGreaterThan(6.0, "a Gaussian reads as a Moffat with no wings");
        }
    }

    [Fact]
    public void AStackedSaturatedStarsEdgeIsSofterThanAHardClips()
    {
        const int size = 128;
        const double clip = 0.6;
        var plate = new[] { Filled(size, 0.05f) };
        var profile = StarProfile.Round(StarProfileFamily.Moffat, 2.5, 2.8);
        var star = new InjectedStar(64.3, 63.8, [40.0], [profile], true, [clip]);
        var stacked = StarInjection.Render(plate, size, size, null, [star], [1e-6], new Random(4)).Planes[0];
        var hard = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                hard[(y * size) + x] = (float)Math.Min(clip, 0.05 + (40.0 * profile.PixelMean(x, y, star.X, star.Y)));
            }
        }
        var soft = InjectionMeasure.SaturatedShape(stacked, size, size, star.X, star.Y);
        var sharp = InjectionMeasure.SaturatedShape(hard, size, size, star.X, star.Y);
        soft.ShouldNotBeNull();
        sharp.ShouldNotBeNull();
        output.WriteLine($"stacked: plateau {soft.Value.PlateauPx} px, edge {soft.Value.EdgePx:F1} px; hard clip: plateau {sharp.Value.PlateauPx} px, edge {sharp.Value.EdgePx:F1} px");
        soft.Value.PlateauPx.ShouldBeLessThan(sharp.Value.PlateauPx, "the subs' scatter rounds the plateau's rim off");
        soft.Value.EdgePx.ShouldBeGreaterThanOrEqualTo(sharp.Value.EdgePx);
        InjectionMeasure.SaturatedShape(stacked, size, size, 20.0, 64.0).ShouldBeNull("too near the edge to read the sky");
    }

    private static (InjectionPopulation Population, ImmutableArray<FittedStar> Catalogue) BuildPopulation()
    {
        const int size = 256;
        var rng = new Random(2);
        var catalogue = ImmutableArray.CreateBuilder<FittedStar>();
        for (var i = 0; i < 30; i++)
        {
            var x = (float)(10 + rng.NextDouble() * (size - 20));
            var y = (float)(10 + rng.NextDouble() * (size - 20));
            var amplitude = (float)(0.01 + rng.NextDouble() * 0.2);
            catalogue.Add(Star(x, y, amplitude, saturated: false));
        }
        catalogue.Add(Star(200, 40, 3.0f, saturated: true));
        catalogue.Add(new FittedStar(120, 120, 10, 0.05f, 2.5f, 0.1f, 0.01f, StarFitOutcome.Knot, false, false, float.NaN, float.NaN, false, float.NaN));
        var built = catalogue.ToImmutable();

        // The master holds the saturated star's core at 0.9; the plate is flat, so the stars plate is the master's excess.
        var masterPlane = new float[size, size];
        var platePlane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                masterPlane[y, x] = 0.1f;
                platePlane[y, x] = 0.1f;
            }
        }
        masterPlane[40, 200] = 0.9f;
        var master = new Image([masterPlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var plate = new Image([platePlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        return (InjectionPopulation.Build(built, master, plate, 1.0, [(2.5, 3.0)], null), built);
    }

    private static FittedStar Star(float x, float y, float amplitude, bool saturated)
        => new FittedStar(x, y, 100, amplitude, 1f, 0.1f, 0.01f, StarFitOutcome.Subtracted, saturated, false, float.NaN, float.NaN, false, float.NaN,
            StarFitModel.Moffat, [amplitude]);

    private static float[] Filled(int size, float value)
    {
        var plane = new float[size * size];
        Array.Fill(plane, value);
        return plane;
    }

    private static Image Image(int size, float value)
    {
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                plane[y, x] = value;
            }
        }
        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }
}
