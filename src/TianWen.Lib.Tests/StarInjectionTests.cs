using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
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
    public void AStackedUnsaturatedStarCarriesAFewPercentOfItsPeakAtItsCoreAndKeepsItsFlux()
    {
        // R2m: rendered as the stack of virtual subs a saturated star is, an unsaturated one departs from its exact profile
        // at the core by what real masters' stars carry beyond their shot noise (half pairs: 2 to 5 % of the peak a pixel at
        // 10 to 100 sigma), and keeps its light.
        const int size = 41;
        var plate = new[] { Filled(size, 0.05f) };
        var profile = StarProfile.Round(StarProfileFamily.Moffat, 2.3, 3.0);
        var shares = new double[40];
        for (var s = 0; s < shares.Length; s++)
        {
            var star = new InjectedStar(20.0 + (0.1 * (s % 7)), 20.0 - (0.07 * (s % 5)), [2.0], [profile], false, []);
            var exact = StarInjection.Render(plate, size, size, null, [star], [1e-7], new Random(s)).Planes[0];
            var stacked = StarInjection.Render(plate, size, size, null, [star], [1e-7], new Random(s), stackAll: true).Planes[0];
            var peak = exact.Max() - 0.05;
            double sq = 0;
            for (var y = 19; y <= 21; y++)
            {
                for (var x = 19; x <= 21; x++)
                {
                    var d = stacked[(y * size) + x] - exact[(y * size) + x];
                    sq += d * d;
                }
            }
            shares[s] = Math.Sqrt(sq / 9) / peak;
            var flux = exact.Sum(v => v - 0.05);
            stacked.Sum(v => v - 0.05).ShouldBe(flux, flux * 0.15, "the subs scatter the light, they neither add nor take it");
        }
        Array.Sort(shares);
        var median = shares[shares.Length / 2];
        output.WriteLine($"core departure from the exact profile, rms over 3x3: median {100 * median:F2} % of the peak, range {100 * shares[0]:F2} to {100 * shares[^1]:F2} %");
        median.ShouldBeInRange(0.02, 0.07);
        StarInjection.Render(plate, size, size, null, [new InjectedStar(20, 20, [2.0], [profile], false, [])], [1e-7], new Random(1)).Planes[0]
            .ShouldBe(StarInjection.Render(plate, size, size, null, [new InjectedStar(20, 20, [2.0], [profile], false, [])], [1e-7], new Random(2)).Planes[0],
                "without the switch an unsaturated star draws nothing from the generator");
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

    // R2k's catalogue: a 300 sigma star P with a second-pass lobe L 3 px east (inside 2 FWHM of 2.5 px); a second-pass row F
    // far from everything; a second-pass row M 3 px from a FAINTER first-pass star Q, which is no parent of it.
    private static (ImmutableArray<FittedStar> Catalogue, Image Image) LobeCatalogue()
    {
        static FittedStar Row(float x, float y, float significance, float amplitude, bool secondPass)
            => new FittedStar(x, y, significance, amplitude, 1f, 0.1f, 0.01f, StarFitOutcome.Subtracted, false, false, float.NaN, float.NaN,
                secondPass, float.NaN, StarFitModel.Moffat, [amplitude]);
        ImmutableArray<FittedStar> catalogue =
        [
            Row(100, 100, 300, 0.3f, secondPass: false),
            Row(103, 100, 20, 0.02f, secondPass: true),
            Row(200, 200, 10, 0.01f, secondPass: true),
            Row(60, 160, 5, 0.005f, secondPass: false),
            Row(63, 160, 15, 0.015f, secondPass: true),
        ];
        return (catalogue, Image(256, 0.1f));
    }

    [Fact]
    public void ALobeWithinReachOfABrighterFirstPassStarIsThatStarsAndNothingElseIs()
    {
        var (catalogue, image) = LobeCatalogue();
        InjectionPopulation.LobeParents(catalogue, InjectionPopulation.LobeReachFwhm * 2.5).ShouldBe([-1, 0, -1, -1, -1]);

        var plain = InjectionPopulation.Build(catalogue, image, image, 1.0, [(2.5, 3.0)], null);
        (plain.Sites, plain.CountedSites, plain.AmplitudePool, plain.CarriedLobes).ShouldBe((5, 5, 5, 0), "off, every row is a star of its own");

        var lobed = InjectionPopulation.Build(catalogue, image, image, 1.0, [(2.5, 3.0)], null, lobes: true);
        (lobed.Sites, lobed.CountedSites, lobed.AmplitudePool, lobed.CarriedLobes).ShouldBe((5, 4, 4, 1),
            "the lobe leaves the pool and the count, and its star carries it; the far row and the one beside a fainter star stay stars");
    }

    [Fact]
    public void ADrawnStarBringsItsLobeAtItsOwnOffsetAndAmplitudes()
    {
        var (catalogue, image) = LobeCatalogue();
        // P and its lobe alone, so every draw is P.
        ImmutableArray<FittedStar> pair = [catalogue[0], catalogue[1]];

        var lobed = InjectionPopulation.Build(pair, image, image, 1.0, [(2.5, 3.0)], null, lobes: true);
        var plan = lobed.Plan(0, 0, 256, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.0, new Random(11));
        (plan.Requested, plan.Placed).ShouldBe((1, 1), "the cell counts one star, P");
        plan.Stars.Length.ShouldBe(2);
        var (star, lobe) = (plan.Stars[0], plan.Stars[1]);
        star.Lobe.ShouldBeFalse();
        star.Amplitudes[0].ShouldBe(0.3, 1e-6);
        lobe.Lobe.ShouldBeTrue();
        lobe.Saturated.ShouldBeFalse();
        (lobe.X - star.X, lobe.Y - star.Y).ShouldBe((3.0, 0.0));
        lobe.Amplitudes[0].ShouldBe(0.02, 1e-6);

        var plain = InjectionPopulation.Build(pair, image, image, 1.0, [(2.5, 3.0)], null);
        var plainPlan = plain.Plan(0, 0, 256, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.0, new Random(11));
        (plainPlan.Requested, plainPlan.Stars.Length).ShouldBe((2, 2), "off, the lobe is a site and a star of its own");
        plainPlan.Stars.ShouldAllBe(static s => !s.Lobe);
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
    public void ADivergedEdgeFitIsNeverDrawnFromThePoolOrCarriedAsALobe()
    {
        static FittedStar Row(float x, float y, float amplitude, float localSigma, bool secondPass = false)
            => new FittedStar(x, y, secondPass ? 20 : 100, amplitude, 1f, 0.1f, localSigma, StarFitOutcome.Subtracted, false, false, float.NaN, float.NaN,
                secondPass, float.NaN, StarFitModel.Moffat, [amplitude]);
        // Ordinary stars at 0.2 and 0.3; a fit with no finite sigma (its centre outside the frame) at 1e12; an unsaturated
        // star whose amplitude plus its 0.1 sky is past the clip of 1; and a second-pass row of the 0.3 star that diverged too.
        ImmutableArray<FittedStar> catalogue =
        [
            Row(40, 40, 0.2f, 0.01f),
            Row(100, 100, 0.3f, 0.01f),
            Row(160, 160, 1e12f, float.NaN),
            Row(200, 60, 5f, 0.01f),
            Row(103, 100, 4f, 0.01f, secondPass: true),
            Row(60, 200, 0.25f, 0.01f),
        ];
        var image = Image(256, 0.1f);

        foreach (var lobes in new[] { false, true })
        {
            var population = InjectionPopulation.Build(catalogue, image, image, 1.0, [(2.5, 3.0)], null, lobes: lobes);
            population.AmplitudePool.ShouldBe(3, "the three ordinary stars; the diverged and the over-clip rows are refused");
            population.Refused.ShouldBe(3);
            population.CarriedLobes.ShouldBe(0, "the second-pass row is over the clip, so it is not carried");
            population.Colours.ShouldAllBe(static c => c[0] < 1.0);
            population.CountedSites.ShouldBe(lobes ? 5 : 6, "the sites still count every refused star; only a claimed lobe leaves the count");
            for (var seed = 0; seed < 20; seed++)
            {
                var plan = population.Plan(0, 0, 256, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.0, new Random(seed));
                plan.Stars.ShouldAllBe(static s => s.Amplitudes[0] < 1.0);
            }
        }
    }

    // #1867 review: the margin pinned either side. The clip is 1 and the sky 0.1, so the ceiling less the sky is 0.9; a row just under
    // OvershootMargin times that is admitted, one just over is refused (at 1x or at 10x the pool would not read 2 and 1).
    [Fact]
    public void TheOvershootMarginAdmitsJustUnderItAndRefusesJustOver()
    {
        static FittedStar Row(float x, float y, float amplitude)
            => new FittedStar(x, y, 100, amplitude, 1f, 0.1f, 0.01f, StarFitOutcome.Subtracted, false, false, float.NaN, float.NaN,
                false, float.NaN, StarFitModel.Moffat, [amplitude]);
        var ceiling = (float)(ClassicalStarRemover.OvershootMargin * 0.9);
        ImmutableArray<FittedStar> catalogue = [Row(40, 40, 0.2f), Row(100, 100, ceiling * 0.97f), Row(160, 160, ceiling * 1.03f)];
        var image = Image(256, 0.1f);

        var population = InjectionPopulation.Build(catalogue, image, image, 1.0, [(2.5, 3.0)], null, lobes: false);

        population.AmplitudePool.ShouldBe(2, "the ordinary star and the one just under the margin");
        population.Refused.ShouldBe(1, "the one just over the margin");
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

    /// <summary>
    /// The plate builder's profile is its Moffat plus its residual table, the table added at the pixel's centre as the
    /// builder adds it (RadialCorrection.Model), reaching as far as the table stands above the fraction asked for, and
    /// stretching with the width.
    /// </summary>
    [Fact]
    public void AFieldProfileIsItsMoffatPlusItsHaloAndReachesAsFarAsItsTable()
    {
        var moffat = StarProfile.Round(StarProfileFamily.Moffat, 2.5, 3.0);
        var alpha = MoffatPsf.AlphaFor(2.5, 3.0);
        ImmutableArray<float> halo = [.. Enumerable.Repeat(0.01f, 40)];
        var field = new StarProfile(StarProfileFamily.Field, 2.5, 3.0, 1.0, 0.0, halo, alpha);

        // The table is float: its 0.01 is 0.0099999998 as a double.
        field.PixelMean(10, 13, 10.2, 9.7).ShouldBe(moffat.PixelMean(10, 13, 10.2, 9.7) + 0.01f, 1e-12);
        field.PixelMean(10, 10, 10.0, 10.0).ShouldBe(moffat.PixelMean(10, 10, 10.0, 10.0) + 0.01f, 1e-12);
        field.PixelMean(22, 10, 10.0, 10.0).ShouldBe(moffat.PixelMean(22, 10, 10.0, 10.0), 1e-12, "past the table's 10 px the halo is gone");
        moffat.RadiusAtFraction(0.005).ShouldBeLessThan(6.0);
        field.RadiusAtFraction(0.005).ShouldBe(10.0, 1e-9, "the table stands at 0.01 out to its last bin");
        field.Scaled(2.0).PixelMean(28, 10, 10.0, 10.0).ShouldBeGreaterThan(0.009, "twice as wide, the halo reaches twice as far");

        // A table below the Moffat in the core never draws negative light.
        var hollow = field with { Halo = [.. Enumerable.Repeat(-2f, 4)] };
        hollow.PixelMean(10, 10, 10.0, 10.0).ShouldBe(0.0);
    }

    /// <summary>
    /// A population given the plate's field profile draws the Field family with it, halo and all; without one it refuses the
    /// Field family rather than draw the catalogue's amplitudes with another profile, and the PSF store's families are untouched.
    /// </summary>
    [Fact]
    public void AFieldPopulationDrawsWithThePlatesOwnProfileAndRefusesWithoutIt()
    {
        var (withoutField, catalogue) = BuildPopulation();
        Should.Throw<InvalidOperationException>(() => withoutField.ProfilesAt(100, 100, StarProfileFamily.Field));

        var channel = new FieldChannelProfile(MoffatPsf.AlphaFor(2.1, 2.7), 2.7, 1.3, [0.02f, 0.01f, 0.005f]);
        var withField = InjectionPopulation.Build(catalogue, Image(256, 0.1f), Image(256, 0.1f), 1.0, [(2.5, 3.0)], null,
            new StarlessFieldProfile([channel], channel));
        var drawn = withField.ProfilesAt(100, 100, StarProfileFamily.Field).Single();
        drawn.FwhmPx.ShouldBe(2.1, 1e-9);
        drawn.Beta.ShouldBe(2.7);
        drawn.Halo.ShouldBe(channel.Table);
        drawn.HaloAlpha.ShouldBe(1.3);
        withField.ProfilesAt(100, 100, StarProfileFamily.Moffat).Single().FwhmPx.ShouldBe(2.5, "the store's Moffat is still the Moffat arm");
        Should.Throw<ArgumentException>(() => InjectionPopulation.Build(catalogue, Image(256, 0.1f), Image(256, 0.1f), 1.0, [(2.5, 3.0)], null,
            new StarlessFieldProfile([channel, channel], channel)));
    }

    /// <summary>
    /// A field profile is read back against what the shape fit makes of it drawn alone; for a plain Moffat that is its own
    /// FWHM and beta, and a halo reads as a lower beta, not as a wrong width.
    /// </summary>
    [Fact]
    public void TheReferenceFitOfAMoffatIsItselfAndAHaloLowersItsBeta()
    {
        var moffat = StarProfile.Round(StarProfileFamily.Moffat, 2.5, 3.0);
        var plain = InjectionMeasure.ReferenceFit(moffat).ShouldNotBeNull();
        plain.FwhmPx.ShouldBe(2.5, 0.025);
        plain.Beta.ShouldBe(3.0, 0.15);

        ImmutableArray<float> halo = [.. Enumerable.Range(0, 40).Select(static b => (float)(0.03 * Math.Exp(-b / 12.0)))];
        var field = new StarProfile(StarProfileFamily.Field, 2.5, 3.0, 1.0, 0.0, halo, MoffatPsf.AlphaFor(2.5, 3.0));
        var withHalo = InjectionMeasure.ReferenceFit(field).ShouldNotBeNull();
        withHalo.Beta.ShouldBeLessThan(plain.Beta);
    }

    /// <summary>
    /// R2s's wing gain scales a profile past 1.5 FWHM by the gain, its halo table with it, and leaves the peak and the core
    /// as measured; a default profile, whose log gain is 0, draws exactly as before.
    /// </summary>
    [Fact]
    public void AWingGainScalesTheWingAndItsHaloAndLeavesTheCore()
    {
        default(StarProfile).HaloGain.ShouldBe(1.0, "a default profile draws its wings as measured");
        var plain = StarProfile.Round(StarProfileFamily.Moffat, 2.5, 3.0);
        var bright = plain with { HaloLogGain = Math.Log(2.0) };
        var dim = plain with { HaloLogGain = Math.Log(0.5) };

        // Every quadrature point of the centre pixel and its neighbour lies inside 0.75 FWHM (1.9 px): untouched.
        bright.PixelMean(10, 10, 10.0, 10.0).ShouldBe(plain.PixelMean(10, 10, 10.0, 10.0));
        bright.PixelMean(11, 10, 10.0, 10.0).ShouldBe(plain.PixelMean(11, 10, 10.0, 10.0));
        // Every point of a pixel 5 px out lies past 1.5 FWHM (3.75 px): the whole gain.
        bright.PixelMean(15, 10, 10.0, 10.0).ShouldBe(2.0 * plain.PixelMean(15, 10, 10.0, 10.0), 1e-15);
        dim.PixelMean(15, 10, 10.0, 10.0).ShouldBe(0.5 * plain.PixelMean(15, 10, 10.0, 10.0), 1e-15);
        bright.RadiusAtFraction(1e-3).ShouldBeGreaterThan(plain.RadiusAtFraction(1e-3), "a brighter wing reaches farther");
        dim.RadiusAtFraction(1e-3).ShouldBe(plain.RadiusAtFraction(1e-3), "a dimmer one keeps the measured reach");

        ImmutableArray<float> halo = [.. Enumerable.Repeat(0.01f, 40)];
        var field = new StarProfile(StarProfileFamily.Field, 2.5, 3.0, 1.0, 0.0, halo, MoffatPsf.AlphaFor(2.5, 3.0));
        (field with { HaloLogGain = Math.Log(2.0) }).PixelMean(18, 10, 10.0, 10.0)
            .ShouldBe(2.0 * field.PixelMean(18, 10, 10.0, 10.0), 1e-12, "the halo table is part of the wing");
    }

    /// <summary>
    /// A channel relation (R2s2) says how the channels differ and nothing of the star's size: identical channels relate as
    /// zero, a profile and its widened copy measure the same wing-to-core light (the Moffat and the field's table both scale
    /// with the width), and a wing gain g multiplies that light by g exactly.
    /// </summary>
    [Fact]
    public void AChannelRelationIsHowTheChannelsDifferAtAnyWidth()
    {
        var same = ChannelRelation.Of("same", ColourField((2.4, 0.004f), (2.4, 0.004f), (2.4, 0.004f)));
        same.LogWidth.ShouldAllBe(static w => Math.Abs(w) < 1e-12);
        same.LogWing.ShouldAllBe(static w => Math.Abs(w) < 1e-12);

        var field = ColourField((3.0, 0.002f), (2.3, 0.006f), (2.6, 0.004f));
        var star = new StarProfile(StarProfileFamily.Field, field.Channels[0].Fwhm, field.Channels[0].Beta, 1.0, 0.0, field.Channels[0].Table, field.Channels[0].TableAlpha);
        ChannelRelation.WingToCore(star.Scaled(1.7)).ShouldBe(ChannelRelation.WingToCore(star), 1e-9 * ChannelRelation.WingToCore(star), "the same star wider");
        ChannelRelation.WingToCore(star with { HaloLogGain = Math.Log(2.0) }).ShouldBe(2.0 * ChannelRelation.WingToCore(star), 1e-12);

        var wider = ChannelRelation.Of("wider", [.. field.Channels.Select(c => new StarProfile(StarProfileFamily.Field, 1.7 * c.Fwhm, c.Beta, 1.0, 0.0, c.Table, c.TableAlpha))]);
        var measured = ChannelRelation.Of("field", field);
        for (var c = 0; c < 3; c++)
        {
            wider.LogWidth[c].ShouldBe(measured.LogWidth[c], 1e-12);
            wider.LogWing[c].ShouldBe(measured.LogWing[c], 1e-9);
        }
        measured.LogWidth.Sum().ShouldBe(0.0, 1e-12, "a relation is against the channels' mean");
        measured.LogWing.Sum().ShouldBe(0.0, 1e-12);
    }

    /// <summary>
    /// The measured agreement R2s lacked: every star of a draw, its saturated one too, takes exactly one relation of the
    /// pool, measured as that relation, while keeping its master's mean width and mean wing, its place, its brightness and
    /// its colour; a pool member of another channel count is never drawn, and the same two seeds draw the same plan.
    /// </summary>
    [Fact]
    public void AnInjectedStarTakesTheDrawnMastersChannelRelation()
    {
        // The master's own red is the widest with the weakest wing, as most of the training masters' are.
        var own = ColourField((3.0, 0.002f), (2.3, 0.006f), (2.6, 0.004f));
        var population = BuildColourPopulation(own);
        var a = ChannelRelation.Of("a", ColourField((2.2, 0.003f), (2.0, 0.005f), (2.4, 0.003f)));
        var b = ChannelRelation.Of("b", ColourField((4.6, 0.010f), (1.2, 0.008f), (1.3, 0.007f)));
        var mono = ChannelRelation.Of("mono", [StarProfile.Round(StarProfileFamily.Moffat, 2.0, 3.0)]);
        var variation = new ChannelVariation([a, b, mono]);

        var plain = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21));
        plain.RelationFrom.ShouldBeNull();
        var seen = new HashSet<string>();
        for (var seed = 0; seed < 24; seed++)
        {
            var varied = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21), variation, new Random(seed));
            var drawn = varied.RelationFrom switch { "a" => a, "b" => b, var other => throw new ShouldAssertException($"drew {other}") };
            seen.Add(drawn.Source);
            varied.Stars.Length.ShouldBe(plain.Stars.Length);
            varied.Stars.Count(static s => s.Saturated).ShouldBe(1, "the saturated draw takes the relation too");
            for (var i = 0; i < plain.Stars.Length; i++)
            {
                var (v, p) = (varied.Stars[i], plain.Stars[i]);
                (v.X, v.Y, v.Saturated).ShouldBe((p.X, p.Y, p.Saturated));
                v.Amplitudes.ShouldBe(p.Amplitudes);
                var relation = ChannelRelation.Of("star", v.Profiles);
                for (var c = 0; c < 3; c++)
                {
                    relation.LogWidth[c].ShouldBe(drawn.LogWidth[c], 1e-9);
                    relation.LogWing[c].ShouldBe(drawn.LogWing[c], 1e-9);
                }
                // The master's own size and halo: the geometric means across channels are unmoved.
                v.Profiles.Average(static q => Math.Log(q.FwhmPx)).ShouldBe(p.Profiles.Average(static q => Math.Log(q.FwhmPx)), 1e-9);
                v.Profiles.Average(static q => Math.Log(ChannelRelation.WingToCore(q with { AxisRatio = 1.0 })))
                    .ShouldBe(p.Profiles.Average(static q => Math.Log(ChannelRelation.WingToCore(q with { AxisRatio = 1.0 }))), 1e-9);
            }
        }
        seen.ShouldBe(["a", "b"], ignoreOrder: true);

        var once = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21), variation, new Random(99));
        var again = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21), variation, new Random(99));
        again.Stars.SelectMany(static s => s.Profiles).ShouldBe(once.Stars.SelectMany(static s => s.Profiles));
        Should.Throw<ArgumentNullException>(() =>
            population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21), variation, null));
        Should.Throw<InvalidOperationException>(() =>
            population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Moffat, 1.0, new Random(21), variation, new Random(1)));

        // A master whose channel count the pool lacks keeps its own relation.
        var (kept, factors) = new ChannelVariation([a, b]).DrawCell(mono, new Random(5));
        kept.ShouldBeSameAs(mono);
        factors.ShouldAllBe(static f => f.LogHalo == 0.0 && f.LogWidth == 0.0);
    }

    /// <summary>
    /// R2v's wing cut: about half the draws weaken one channel's wing, every star of the draw alike and the saturated one
    /// too, by a factor from the least given up to 1, never brighter, and the plan names the channel and the factor; the
    /// stars' places, brightness, colour, widths and every other channel are the draw's without it, and a mono master is
    /// never cut.
    /// </summary>
    [Fact]
    public void AWingCutWeakensOneChannelInHalfTheDrawsAndMovesNoStar()
    {
        var population = BuildColourPopulation(ColourField((3.0, 0.002f), (2.3, 0.006f), (2.6, 0.004f)));
        var cut = new ChannelWingCut(0.45);
        var plain = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21));
        var (cuts, channelsCut) = (0, new HashSet<int>());
        const int draws = 400;
        for (var seed = 0; seed < draws; seed++)
        {
            var drawn = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21), null, new Random(seed), cut);
            drawn.Stars.Length.ShouldBe(plain.Stars.Length);
            int? channel = null;
            var factor = 1.0;
            for (var i = 0; i < plain.Stars.Length; i++)
            {
                var (d, p) = (drawn.Stars[i], plain.Stars[i]);
                (d.X, d.Y, d.Saturated).ShouldBe((p.X, p.Y, p.Saturated));
                d.Amplitudes.ShouldBe(p.Amplitudes);
                for (var c = 0; c < 3; c++)
                {
                    d.Profiles[c].FwhmPx.ShouldBe(p.Profiles[c].FwhmPx);
                    var gain = Math.Exp(d.Profiles[c].HaloLogGain - p.Profiles[c].HaloLogGain);
                    if (gain == 1.0)
                    {
                        continue;
                    }
                    // One channel a draw, the same factor on every star of it.
                    (channel ?? c).ShouldBe(c);
                    channel = c;
                    if (factor != 1.0)
                    {
                        gain.ShouldBe(factor, 1e-12);
                    }
                    factor = gain;
                    gain.ShouldBeInRange(0.45, 1.0);
                }
            }
            if (channel is { } ch)
            {
                cuts++;
                channelsCut.Add(ch);
                drawn.RelationFrom.ShouldBe(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"wing cut: channel {ch} at {factor:F3}"));
            }
            else
            {
                drawn.RelationFrom.ShouldBeNull();
            }
        }
        // Half the draws, binomial over 400: 3.5 standard deviations either side.
        cuts.ShouldBeInRange(165, 235);
        channelsCut.ShouldBe([0, 1, 2], ignoreOrder: true);

        Should.Throw<ArgumentNullException>(() =>
            population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21), null, null, cut));
        var a = ChannelRelation.Of("a", ColourField((2.2, 0.003f), (2.0, 0.005f), (2.4, 0.003f)));
        Should.Throw<InvalidOperationException>(() =>
            population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21), new ChannelVariation([a]), new Random(1), cut));
        Should.Throw<ArgumentOutOfRangeException>(() => new ChannelWingCut(1.5));
        Should.Throw<ArgumentOutOfRangeException>(() => new ChannelWingCut(0.0));
        for (var seed = 0; seed < 50; seed++)
        {
            var (monoFactors, monoChannel, _) = cut.DrawCell(1, new Random(seed));
            monoChannel.ShouldBe(-1);
            monoFactors.ShouldAllBe(static f => f.LogHalo == 0.0 && f.LogWidth == 0.0);
        }
    }

    /// <summary>
    /// Synthetics S4's estimator: on two noise series of correlation 0.12 with one sample in fifty carrying light both share
    /// (a faint star, 3 to 7 sigma), a Pearson coefficient reads 0.41, the light taken for correlated noise, and the robust
    /// form 0.14. It is not immune (one sample in twenty moves it to 0.17), so the plate it reads has its stars subtracted.
    /// </summary>
    [Fact]
    public void TheRobustCorrelationReadsTheNoisePastTheLightBothChannelsShare()
    {
        const int n = 200_000;
        const double rho = 0.12;
        var rng = new Random(3);
        var u = new float[n];
        var v = new float[n];
        for (var i = 0; i < n; i++)
        {
            var (a, b) = (Gaussian(rng), Gaussian(rng));
            var star = rng.NextDouble() < 0.02 ? 3.0 + (4.0 * rng.NextDouble()) : 0.0;
            u[i] = (float)(a + star);
            v[i] = (float)((rho * a) + (Math.Sqrt(1 - (rho * rho)) * b) + star);
        }
        var pearson = Pearson(u, v);
        var robust = ChannelNoiseCorrelation.RobustCorrelation(u, v);
        output.WriteLine($"Pearson {pearson:F3}, robust {robust:F3}, drawn {rho}");
        pearson.ShouldBeGreaterThan(0.3, "the shared light dominates a Pearson coefficient");
        robust.ShouldBe(rho, 0.03);
        robust.ShouldBeLessThan(pearson - 0.2);
        ChannelNoiseCorrelation.RobustCorrelation([1f, 1f, 1f], [0f, 1f, 2f]).ShouldBe(double.NaN, "a series with no spread has no correlation to read");
    }

    /// <summary>
    /// Synthetics S4's measure: a plate whose noise carries a known correlation between its channels, on a sloped sky with
    /// a few stars every channel shares and an absent ring, reads that correlation back; a noise-free plate reads none, a
    /// mono one is its identity.
    /// </summary>
    [Fact]
    public void APlatesChannelNoiseCorrelationIsReadBack()
    {
        const int size = 600;
        var drawn = new ChannelNoiseCorrelation(Correlation(0.12, 0.05, 0.04));
        var rng = new Random(11);
        var fields = drawn.Mix([.. Enumerable.Range(0, 3).Select(_ => NoiseField.White(size, size, rng))]);
        var absent = new BitMatrix(size, size);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var ring = x < 20 || y < 20;
                    absent[y, x] = ring;
                    var star = 0.0;
                    for (var s = 0; s < 40; s++)
                    {
                        var (sx, sy) = (40 + (s * 37 % 540), 40 + (s * 101 % 540));
                        var r2 = ((x - sx) * (x - sx)) + ((y - sy) * (y - sy));
                        star += r2 < 64 ? 0.05 * Math.Exp(-r2 / 4.0) : 0.0;
                    }
                    plane[y, x] = ring ? 0f : (float)(0.1 + (0.02 * c) + (1e-5 * x) + star + (0.002 * (1 + c) * fields[c][(y * size) + x]));
                }
            }
            planes[c] = plane;
        }
        var plate = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var read = ChannelNoiseCorrelation.Measure(plate, absent);
        output.WriteLine($"read {read.InWords()}, drawn {drawn.InWords()}");
        read.Channels.ShouldBe(3);
        read[0, 1].ShouldBe(0.12, 0.02);
        read[1, 2].ShouldBe(0.05, 0.02);
        read[0, 2].ShouldBe(0.04, 0.02);
        read[1, 0].ShouldBe(read[0, 1]);

        var flat = new Image([new float[64, 64], new float[64, 64], new float[64, 64]], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var none = ChannelNoiseCorrelation.Measure(flat, null);
        (none[0, 1], none[1, 2], none[0, 2]).ShouldBe((0.0, 0.0, 0.0));
        var mono = ChannelNoiseCorrelation.Measure(new Image([planes[0]], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()), absent);
        mono.Channels.ShouldBe(1);
        mono.InWords().ShouldBe("one channel");
    }

    /// <summary>
    /// Synthetics S4's mix, through the exporter's own draw: the first channel's field is the one drawn, every field the
    /// draw made without the mix is the same, each mixed channel keeps unit variance and its warped shape's lag-1
    /// correlation, and the channels carry the given correlation. A matrix that is not positive definite is shrunk until it
    /// is, and a malformed one is refused.
    /// </summary>
    [Fact]
    public void TheMixGivesTheFieldsTheCorrelationAndKeepsEachOnesShape()
    {
        const int size = 512;
        var correlation = new ChannelNoiseCorrelation(Correlation(0.12, 0.05, 0.04));
        var plain = DatasetDegradationExporter.NoiseFieldsFor(DatasetDegradationExporter.NoiseShape.Warped, 0.5, size, 30, new Random(7), 3, null);
        var mixed = DatasetDegradationExporter.NoiseFieldsFor(DatasetDegradationExporter.NoiseShape.Warped, 0.5, size, 30, new Random(7), 3, correlation);
        var again = DatasetDegradationExporter.NoiseFieldsFor(DatasetDegradationExporter.NoiseShape.Warped, 0.5, size, 30, new Random(7), 3, null);
        for (var c = 0; c < 3; c++)
        {
            again[c].ShouldBe(plain[c], "without a correlation the fields are the ones drawn");
        }
        mixed[0].ShouldBe(plain[0]);
        var mismatched = DatasetDegradationExporter.NoiseFieldsFor(DatasetDegradationExporter.NoiseShape.Warped, 0.5, size, 30, new Random(7), 1, correlation);
        mismatched[0].ShouldBe(plain[0], "a mono draw is never mixed");

        for (var c = 0; c < 3; c++)
        {
            var (variance, lag) = (Variance(mixed[c]), LagOne(mixed[c], size));
            output.WriteLine($"channel {c}: variance {variance:F4}, lag-1 {lag:F3} against {LagOne(plain[c], size):F3} unmixed");
            variance.ShouldBe(1.0, 0.02);
            lag.ShouldBe(LagOne(plain[c], size), 0.02);
        }
        LagOne(plain[0], size).ShouldBeGreaterThan(0.2, "the warped shape correlates neighbours");
        var (rg, gb, rb) = (Pearson(mixed[0], mixed[1]), Pearson(mixed[1], mixed[2]), Pearson(mixed[0], mixed[2]));
        output.WriteLine($"mixed R-G {rg:F3}, G-B {gb:F3}, R-B {rb:F3}; unmixed R-G {Pearson(plain[0], plain[1]):F3}");
        rg.ShouldBe(0.12, 0.02);
        gb.ShouldBe(0.05, 0.02);
        rb.ShouldBe(0.04, 0.02);

        var impossible = new ChannelNoiseCorrelation(Correlation(0.9, 0.9, -0.9));
        impossible.Shrink.ShouldBeLessThan(1.0);
        impossible[0, 1].ShouldBe(0.9, "the matrix is kept as given; only its factor is shrunk");
        correlation.Shrink.ShouldBe(1.0);
        Should.Throw<ArgumentException>(() => new ChannelNoiseCorrelation(new double[,] { { 1, 0.1 }, { 0.2, 1 } }));
        Should.Throw<ArgumentException>(() => new ChannelNoiseCorrelation(new double[,] { { 0.9, 0.1 }, { 0.1, 1 } }));
        Should.Throw<ArgumentException>(() => new ChannelNoiseCorrelation(new double[,] { { 1, 1 }, { 1, 1 } }));
    }

    // Synthetics S4b: three 3 x 3 kernels over one shared field, R and G on the same pixel and B on the diagonal, as an
    // ASI533's R-B sits (+0.050 at offset 1,1 against +0.010 at 0,0). What a structure is built from is the correlation the
    // MEASURE would read of them, at every offset up to two pixels, through the 5 x 5 residual; the support of a 3 x 3
    // kernel's correlation ends at two, so it is reachable by the fit.
    private static (double[][,] Cross, double[] Own) KnownStructure()
    {
        double[] Bump(int cy, int cx, double centre, double edge, double corner)
        {
            var k = new double[25];
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    k[((cy + dy + 2) * 5) + cx + dx + 2] = (dy, dx) == (0, 0) ? centre : (dy == 0 || dx == 0 ? edge : corner);
                }
            }
            return k;
        }
        double[][] kernels = [Bump(0, 0, 0.30, 0.12, 0.06), Bump(0, 0, 0.28, 0.10, 0.05), Bump(1, 1, 0.28, 0.12, 0.04)];
        // The fields' own cross-covariance at offset (dy, dx): the kernels' cross-correlation.
        double Raw(int a, int b, int dy, int dx)
        {
            var sum = 0.0;
            for (var ey = -2; ey <= 2; ey++)
            {
                for (var ex = -2; ex <= 2; ex++)
                {
                    var (ay, ax) = (ey + dy, ex + dx);
                    if (Math.Abs(ay) <= 2 && Math.Abs(ax) <= 2)
                    {
                        sum += kernels[a][((ay + 2) * 5) + ax + 2] * kernels[b][((ey + 2) * 5) + ex + 2];
                    }
                }
            }
            return sum;
        }
        // What the instrument reads of it: the residual from a 5 x 5 mean, h = delta - box / 25, whose autocorrelation K
        // takes the covariance at d to the sum over r of K(r) times the covariance at d + r, over K(0), the variance white
        // noise keeps. The measure's median agrees with it to 0.005 (a draw of 0.144 reads 0.098).
        double K(int ry, int rx)
        {
            var sum = 0.0;
            for (var uy = -2; uy <= 2; uy++)
            {
                for (var ux = -2; ux <= 2; ux++)
                {
                    var (vy, vx) = (uy + ry, ux + rx);
                    if (Math.Abs(vy) <= 2 && Math.Abs(vx) <= 2)
                    {
                        var h = (((uy, ux) == (0, 0) ? 1.0 : 0.0) - (1.0 / 25), ((vy, vx) == (0, 0) ? 1.0 : 0.0) - (1.0 / 25));
                        sum += h.Item1 * h.Item2;
                    }
                }
            }
            return sum;
        }
        var cross = new List<double[,]>();
        for (var a = 0; a < 3; a++)
        {
            for (var b = a + 1; b < 3; b++)
            {
                var m = new double[5, 5];
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var read = 0.0;
                        for (var ry = -4; ry <= 4; ry++)
                        {
                            for (var rx = -4; rx <= 4; rx++)
                            {
                                read += K(ry, rx) * Raw(a, b, dy + ry, dx + rx);
                            }
                        }
                        m[dy + 2, dx + 2] = read / K(0, 0);
                    }
                }
                cross.Add(m);
            }
        }
        return ([.. cross], [0.2, 0.18, 0.16]);
    }

    /// <summary>A field less the mean of the 5 x 5 pixels around each pixel, the interior only: the instrument's residual, as a mean.</summary>
    private static float[] BoxResidual(float[] field, int size)
    {
        var inner = size - 4;
        var result = new float[inner * inner];
        for (var y = 0; y < inner; y++)
        {
            for (var x = 0; x < inner; x++)
            {
                var sum = 0.0;
                for (var dy = 0; dy < 5; dy++)
                {
                    for (var dx = 0; dx < 5; dx++)
                    {
                        sum += field[((y + dy) * size) + x + dx];
                    }
                }
                result[(y * inner) + x] = (float)(field[((y + 2) * size) + x + 2] - (sum / 25.0));
            }
        }
        return result;
    }

    /// <summary>Pearson correlation of <paramref name="u"/> at a pixel with <paramref name="v"/> at that pixel plus (dy, dx), over the overlap.</summary>
    private static double Lagged(float[] u, float[] v, int size, int dy, int dx)
    {
        var (a, b) = (new List<float>(), new List<float>());
        for (var y = Math.Max(0, -dy); y < Math.Min(size, size - dy); y++)
        {
            for (var x = Math.Max(0, -dx); x < Math.Min(size, size - dx); x++)
            {
                a.Add(u[(y * size) + x]);
                b.Add(v[((y + dy) * size) + x + dx]);
            }
        }
        return Pearson(a.ToArray(), b.ToArray());
    }

    /// <summary>A field's lag-1 correlation, the horizontal and the vertical neighbours together.</summary>
    private static double LagOneBoth(float[] field, int size)
    {
        var (sum, count) = (0.0, 0);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (x + 1 < size)
                {
                    sum += (double)field[(y * size) + x] * field[(y * size) + x + 1];
                    count++;
                }
                if (y + 1 < size)
                {
                    sum += (double)field[(y * size) + x] * field[((y + 1) * size) + x];
                    count++;
                }
            }
        }
        return sum / count / Variance(field);
    }

    /// <summary>
    /// Synthetics S4b's draw, through the exporter's own: a structure built from a known lagged cross-structure (R-B on
    /// the diagonal) is reached by the fit with nothing shrunk, and fields drawn with it, white and warped, carry every
    /// pair's correlation at every offset up to two pixels, each channel keeps unit variance and the lag-1 its field had,
    /// and the fields the draw's own stream makes are the ones it made without the structure.
    /// </summary>
    [Theory]
    [InlineData(DatasetDegradationExporter.NoiseShape.White)]
    [InlineData(DatasetDegradationExporter.NoiseShape.Warped)]
    public void TheDrawReproducesTheLaggedCrossStructureAndHoldsEachChannelsLagOne(DatasetDegradationExporter.NoiseShape shape)
    {
        const int size = 512;
        var (cross, own) = KnownStructure();
        var structure = new ChannelNoiseStructure(3, cross, own);
        output.WriteLine($"fit: shrink {structure.Shrink:F2}, rms {structure.FitRms:F5}, shared {structure.SharedVariance(0):F3} / {structure.SharedVariance(1):F3} / {structure.SharedVariance(2):F3}");
        structure.Shrink.ShouldBe(1.0);
        structure.FitRms.ShouldBeLessThan(0.002);
        structure.IsIdentity.ShouldBeFalse();

        var plainRng = new Random(7);
        var plain = DatasetDegradationExporter.NoiseFieldsFor(shape, 0.0, size, 30, plainRng, 3, null);
        var drawnRng = new Random(7);
        var drawn = DatasetDegradationExporter.NoiseFieldsFor(shape, 0.0, size, 30, drawnRng, 3, null, structure, new Random(99));
        drawnRng.Next().ShouldBe(plainRng.Next(), "the draw's own stream is left where it was");
        var again = DatasetDegradationExporter.NoiseFieldsFor(shape, 0.0, size, 30, new Random(7), 3, null, structure, new Random(99));
        var other = DatasetDegradationExporter.NoiseFieldsFor(shape, 0.0, size, 30, new Random(7), 3, null, structure, new Random(100));
        for (var c = 0; c < 3; c++)
        {
            again[c].ShouldBe(drawn[c], "the same streams draw the same fields");
            other[c].ShouldNotBe(drawn[c], "the shared field is the stream's own");
            var (variance, lag) = (Variance(drawn[c]), LagOneBoth(drawn[c], size));
            output.WriteLine($"channel {c}: variance {variance:F4}, lag-1 {lag:F3} against {LagOneBoth(plain[c], size):F3} before");
            variance.ShouldBe(1.0, 0.02);
            lag.ShouldBe(LagOneBoth(plain[c], size), 0.02);
        }

        // Read as the measure reads: the residual from a 5 x 5 mean (the median's linear stand-in), a Pearson correlation
        // of it at each offset, against the correlation the structure was given.
        var (residual, inner) = (drawn.Select(f => BoxResidual(f, size)).ToArray(), size - 4);
        var index = 0;
        for (var a = 0; a < 3; a++)
        {
            for (var b = a + 1; b < 3; b++, index++)
            {
                var (peakDrawn, peakGiven) = ((0, 0), (0, 0));
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var read = Lagged(residual[a], residual[b], inner, dy, dx);
                        read.ShouldBe(cross[index][dy + 2, dx + 2], 0.025, $"pair {a}-{b} at offset {dy},{dx}");
                        peakDrawn = read > Lagged(residual[a], residual[b], inner, peakDrawn.Item1, peakDrawn.Item2) ? (dy, dx) : peakDrawn;
                        peakGiven = cross[index][dy + 2, dx + 2] > cross[index][peakGiven.Item1 + 2, peakGiven.Item2 + 2] ? (dy, dx) : peakGiven;
                    }
                }
                output.WriteLine($"pair {a}-{b}: peak at {peakDrawn}, given {peakGiven}; zero lag {Lagged(residual[a], residual[b], inner, 0, 0):F3} against {cross[index][2, 2]:F3}");
                peakDrawn.ShouldBe(peakGiven);
            }
        }
        PeakOfRb(cross).ShouldNotBe((0, 0), "R-B is the diagonal's");

        static (int, int) PeakOfRb(double[][,] cross)
        {
            var best = (0, 0);
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    best = cross[1][dy + 2, dx + 2] > cross[1][best.Item1 + 2, best.Item2 + 2] ? (dy, dx) : best;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Synthetics S4b's measure and draw closed on one another: a plate whose noise is drawn with a known structure (the
    /// correlations the instrument is to read), on a sloped sky with stars and an absent ring, reads back that structure at
    /// every offset through the real 5 x 5 median, and holds the diagonal's place; a noise-free plate reads none and draws
    /// nothing, a mono one is its identity.
    /// </summary>
    [Fact]
    public void APlatesLaggedCrossStructureIsReadBack()
    {
        const int size = 600;
        var (cross, own) = KnownStructure();
        var truth = new ChannelNoiseStructure(3, cross, own);
        var fields = DatasetDegradationExporter.NoiseFieldsFor(DatasetDegradationExporter.NoiseShape.White, 0.0, size, 30, new Random(11), 3, null, truth, new Random(5));
        var absent = new BitMatrix(size, size);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var ring = x < 20 || y < 20;
                    absent[y, x] = ring;
                    var star = 0.0;
                    for (var s = 0; s < 40; s++)
                    {
                        var (sx, sy) = (40 + (s * 37 % 540), 40 + (s * 101 % 540));
                        var r2 = ((x - sx) * (x - sx)) + ((y - sy) * (y - sy));
                        star += r2 < 64 ? 0.05 * Math.Exp(-r2 / 4.0) : 0.0;
                    }
                    plane[y, x] = ring ? 0f : (float)(0.1 + (0.02 * c) + (1e-5 * x) + star + (0.002 * (1 + c) * fields[c][(y * size) + x]));
                }
            }
            planes[c] = plane;
        }
        var plate = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var read = ChannelNoiseStructure.Measure(plate, absent);
        output.WriteLine($"read {read.InWords()}");
        read.Channels.ShouldBe(3);
        var worst = 0.0;
        for (var a = 0; a < 3; a++)
        {
            for (var b = a + 1; b < 3; b++)
            {
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var (r, t) = (read.Cross(a, b, dy, dx), truth.Cross(a, b, dy, dx));
                        worst = Math.Max(worst, Math.Abs(r - t));
                        r.ShouldBe(t, 0.03, $"pair {a}-{b} at offset {dy},{dx}");
                    }
                }
            }
        }
        output.WriteLine($"worst gap to the structure drawn {worst:F3}");
        read.Cross(0, 2, -1, -1).ShouldBeGreaterThan(read.Cross(0, 2, 0, 0) + 0.03, "R-B sits on the diagonal");
        for (var c = 0; c < 3; c++)
        {
            read.OwnLag1(c).ShouldBeInRange(-0.15, 0.05, "white noise's residual from a median reads a little under zero at lag-1");
        }

        var flat = new Image([new float[64, 64], new float[64, 64], new float[64, 64]], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var none = ChannelNoiseStructure.Measure(flat, null);
        none.IsIdentity.ShouldBeTrue("a plate with no noise has no structure to draw");
        var rng = new Random(3);
        var given = new[] { NoiseField.White(16, 16, new Random(1)), NoiseField.White(16, 16, new Random(2)), NoiseField.White(16, 16, new Random(4)) };
        none.Mix(given, 16, rng).ShouldBeSameAs(given);
        rng.Next().ShouldBe(new Random(3).Next(), "nothing is drawn when nothing is shared");
        var mono = ChannelNoiseStructure.Measure(new Image([planes[0]], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()), absent);
        mono.Channels.ShouldBe(1);
        mono.IsIdentity.ShouldBeTrue();
        mono.InWords().ShouldBe("one channel");
        var monoFields = DatasetDegradationExporter.NoiseFieldsFor(DatasetDegradationExporter.NoiseShape.White, 0.0, 16, 30, new Random(7), 1, null, mono, new Random(5));
        monoFields.Length.ShouldBe(1);
    }

    /// <summary>
    /// Synthetics S4b's fit says when it cannot reach a measure: three channels asked to correlate 0.9, 0.9 and -0.9 at zero
    /// lag cannot, and are shrunk toward independent until the fit is within its tolerance and no channel is more than half
    /// shared; the measure is kept as given. A malformed measure is refused, and the two noise arms are not given together.
    /// </summary>
    [Fact]
    public void AMeasureTheFitCannotReachIsShrunkTowardIndependent()
    {
        double[,] Zero() => new double[5, 5];
        var rg = Zero();
        var gb = Zero();
        var rb = Zero();
        (rg[2, 2], gb[2, 2], rb[2, 2]) = (0.9, 0.9, -0.9);
        var impossible = new ChannelNoiseStructure(3, [rg, rb, gb], [0.2, 0.2, 0.2]);
        output.WriteLine($"shrink {impossible.Shrink:F3}, rms {impossible.FitRms:F4}");
        impossible.Shrink.ShouldBeLessThan(1.0);
        impossible.FitRms.ShouldBeLessThanOrEqualTo(ChannelNoiseStructure.FitTolerance);
        impossible.Cross(0, 1, 0, 0).ShouldBe(0.9, "the measure is kept as given; only what is drawn is shrunk");
        for (var c = 0; c < 3; c++)
        {
            impossible.SharedVariance(c).ShouldBeLessThanOrEqualTo(ChannelNoiseStructure.MaxSharedVariance * 1.001);
        }

        var (cross, own) = KnownStructure();
        var wrongSize = new double[5, 4];
        Should.Throw<ArgumentException>(() => new ChannelNoiseStructure(3, [wrongSize, cross[1], cross[2]], own));
        var outside = Zero();
        outside[0, 0] = 1.0;
        Should.Throw<ArgumentException>(() => new ChannelNoiseStructure(3, [outside, cross[1], cross[2]], own));
        Should.Throw<ArgumentOutOfRangeException>(() => new ChannelNoiseStructure(3, [cross[0]], own));

        var structure = new ChannelNoiseStructure(3, cross, own);
        Should.Throw<ArgumentException>(() => DatasetDegradationExporter.NoiseFieldsFor(DatasetDegradationExporter.NoiseShape.White, 0.0, 16, 30, new Random(7), 3,
            new ChannelNoiseCorrelation(Correlation(0.1, 0.1, 0.1)), structure, new Random(1)));
        Should.Throw<ArgumentNullException>(() => DatasetDegradationExporter.NoiseFieldsFor(DatasetDegradationExporter.NoiseShape.White, 0.0, 16, 30, new Random(7), 3,
            null, structure, null));
    }

    private static double[,] Correlation(double rg, double gb, double rb) => new double[,] { { 1, rg, rb }, { rg, 1, gb }, { rb, gb, 1 } };

    private static double Gaussian(Random rng) => Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());

    private static double Pearson(ReadOnlySpan<float> u, ReadOnlySpan<float> v)
    {
        double su = 0, sv = 0, suu = 0, svv = 0, suv = 0;
        for (var i = 0; i < u.Length; i++)
        {
            su += u[i];
            sv += v[i];
            suu += (double)u[i] * u[i];
            svv += (double)v[i] * v[i];
            suv += (double)u[i] * v[i];
        }
        var n = u.Length;
        return ((suv / n) - (su / n * (sv / n))) / Math.Sqrt(((suu / n) - (su / n * (su / n))) * ((svv / n) - (sv / n * (sv / n))));
    }

    private static double Variance(ReadOnlySpan<float> u)
    {
        var mean = 0.0;
        foreach (var x in u)
        {
            mean += x;
        }
        mean /= u.Length;
        var sum = 0.0;
        foreach (var x in u)
        {
            sum += (x - mean) * (x - mean);
        }
        return sum / u.Length;
    }

    private static double LagOne(float[] field, int size)
    {
        var (sum, count) = (0.0, 0);
        for (var y = 0; y < size; y++)
        {
            for (var x = 1; x < size; x++)
            {
                sum += (double)field[(y * size) + x] * field[(y * size) + x - 1];
                count++;
            }
        }
        return sum / count / Variance(field);
    }

    /// <summary>
    /// S2's luminosity function: fitted to a catalogue drawn from a known power law, complete above 10 sigma, half complete
    /// from 5 to 10 and empty below 5, it reads the slope back, and its deficit is what the law puts between the floor and 10
    /// sigma less what the catalogue holds there; the extra stars it draws all lie in [floor, 10), spread as that deficit is.
    /// </summary>
    [Fact]
    public void TheLuminosityFunctionReadsItsSlopeAndDrawsTheDeficitBelowTheCatalogue()
    {
        const double alpha = 0.8;
        var (catalogue, _) = PowerLawSignificances(alpha, 40_000, new Random(5));
        var lf = LuminosityFunction.Fit(catalogue, 3.0).ShouldNotBeNull();
        output.WriteLine($"alpha {lf.Alpha:F3} over {lf.FitStars}; deficit {lf.Deficit:F0}, {lf.ExtraPerCountedStar:P1} of {lf.Counted}");
        lf.Alpha.ShouldBe(alpha, 0.03);

        // What the law puts in [lo, hi), normalised on the fit's own range, as the fit does.
        var inFit = catalogue.Count(static s => s >= LuminosityFunction.FitLowSigma && s < LuminosityFunction.FitHighSigma);
        double Law(double lo, double hi) => inFit * (Math.Pow(lo, -alpha) - Math.Pow(hi, -alpha))
            / (Math.Pow(LuminosityFunction.FitLowSigma, -alpha) - Math.Pow(LuminosityFunction.FitHighSigma, -alpha));
        var expected = Law(3.0, 5.0) + Law(5.0, 10.0) - catalogue.Count(static s => s >= 5.0 && s < 10.0);
        lf.Deficit.ShouldBe(expected, 0.05 * expected);

        var rng = new Random(9);
        var drawn = Enumerable.Range(0, 20_000).Select(_ => lf.DrawSignificance(rng)).ToArray();
        drawn.ShouldAllBe(static s => s >= 3.0 && s < LuminosityFunction.FitLowSigma);
        // Below 5 the catalogue is empty and the whole law is lacking; above it, half. Within a bin the draw follows the law,
        // not the deficit's shape, so read the share at the last bin edge under 5 (10^(5/8) = 4.217), where it is exact.
        var edge = Math.Pow(10.0, 5 * LuminosityFunction.BinDex);
        var belowEdge = drawn.Count(s => s < edge) / (double)drawn.Length;
        belowEdge.ShouldBe(Law(3.0, edge) / expected, 0.03);
        // Within [3, edge) the law's own slope: as many under the geometric middle as the law puts there.
        var middle = Math.Sqrt(3.0 * edge);
        var lower = drawn.Count(s => s < middle) / (double)drawn.Count(s => s < edge);
        lower.ShouldBe(Law(3.0, middle) / Law(3.0, edge), 0.03);

        // A catalogue complete down to the floor lacks nothing to speak of (a bin's noise is all max(0, ...) can leave).
        var (complete, _) = PowerLawSignificances(alpha, 40_000, new Random(6), halfFrom: 10.0, emptyBelow: 0.0);
        var none = LuminosityFunction.Fit(complete, 3.0).ShouldNotBeNull();
        none.Deficit.ShouldBeLessThan(0.02 * complete.Count(static s => s >= 3.0 && s < 10.0));

        // The draw count is the deficit's share of a cell's request, rounded at random to the exact mean.
        var counts = Enumerable.Range(0, 4000).Select(_ => lf.DrawCount(100, rng)).ToArray();
        counts.Average().ShouldBe(100 * lf.ExtraPerCountedStar, 0.05);
        counts.ShouldAllBe(c => c == (int)Math.Floor(100 * lf.ExtraPerCountedStar) || c == (int)Math.Floor(100 * lf.ExtraPerCountedStar) + 1);

        LuminosityFunction.Fit(catalogue.Where(static s => s < 10.0 || s > 20.0).Take(10).ToArray(), 3.0).ShouldBeNull();
        Should.Throw<ArgumentOutOfRangeException>(() => LuminosityFunction.Fit(catalogue, 1.5));
        Should.Throw<ArgumentOutOfRangeException>(() => LuminosityFunction.Fit(catalogue, 10.0));
    }

    /// <summary>
    /// S2 in the plan: a population built with a floor adds the cell's faint stars from their own stream, as many as its
    /// request times the master's deficit share, each a faint catalogued star's colour scaled to its drawn significance and
    /// flagged faint, unsaturated and placed in the cell; every other star is the plan's without them, to the bit.
    /// </summary>
    [Fact]
    public void FaintStarsComeFromTheirOwnStreamAndMoveNoOtherStar()
    {
        var (plain, faint) = (BuildPowerLawPopulation(null), BuildPowerLawPopulation(3.0));
        var lf = faint.Luminosity.ShouldNotBeNull();
        plain.Luminosity.ShouldBeNull();
        lf.ExtraPerCountedStar.ShouldBeGreaterThan(0.1);
        const int cell = 256;
        var (requested, added) = (0, 0);
        var significances = new List<double>();
        for (var seed = 0; seed < 40; seed++)
        {
            var (cx, cy) = (256 * (seed % 3), 256 * (seed / 3 % 3));
            var without = plain.Plan(cx, cy, cell, 8, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.5, new Random(seed));
            var with = faint.Plan(cx, cy, cell, 8, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.5, new Random(seed), faintRng: new Random(1000 + seed));
            without.Faint.ShouldBeEmpty();
            (with.Requested, with.Placed, with.SaturatedFallback).ShouldBe((without.Requested, without.Placed, without.SaturatedFallback));
            with.Stars.Length.ShouldBe(without.Stars.Length);
            for (var i = 0; i < without.Stars.Length; i++)
            {
                var (a, b) = (with.Stars[i], without.Stars[i]);
                (a.X, a.Y, a.Saturated, a.Faint).ShouldBe((b.X, b.Y, b.Saturated, false));
                a.Amplitudes.ShouldBe(b.Amplitudes);
            }
            requested += with.Requested;
            added += with.Faint.Length;
            foreach (var s in with.Faint)
            {
                (s.Faint, s.Saturated, s.Lobe).ShouldBe((true, false, false));
                s.X.ShouldBeInRange(cx - 8.5, cx + cell + 8.5);
                s.Y.ShouldBeInRange(cy - 8.5, cy + cell + 8.5);
                // The population's amplitudes are its significance over 1000 (PowerLawStar), so a faint star's reads back its draw.
                significances.Add(s.Amplitudes[0] * 1000.0);
            }
        }
        output.WriteLine($"{added} faint stars over {requested} requested, {lf.ExtraPerCountedStar:P1} expected");
        // Placement can drop a star near a site, so at most the expectation, and within a few of it on this sparse field.
        ((double)added).ShouldBeInRange(0.9 * requested * lf.ExtraPerCountedStar, 1.03 * requested * lf.ExtraPerCountedStar);
        significances.ShouldAllBe(static s => s >= 3.0 - 1e-9 && s < LuminosityFunction.FitLowSigma + 1e-9);

        Should.Throw<ArgumentNullException>(() => faint.Plan(0, 0, cell, 8, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.5, new Random(1)));
        Should.Throw<InvalidOperationException>(() =>
            faint.Plan(0, 0, cell, 8, InjectionPlacement.AtSite, StarProfileFamily.Moffat, 0.5, new Random(1), faintRng: new Random(2)));
        var thin = ImmutableArray.Create(PowerLawStar(100, 100, 20.0), PowerLawStar(200, 200, 7.0));
        Should.Throw<InvalidOperationException>(() => InjectionPopulation.Build(thin, Image(256, 0.1f), Image(256, 0.1f), 1.0, [(2.5, 3.0)], null, faintFloor: 3.0));
    }

    /// <summary>
    /// The owner's cap on the faint stars (2026-10-23): a tile keeps its faint stars in their drawn order while the light of its
    /// stars under 5 sigma, the catalogue's own counted first, is under the cap, so no tile passes it by more than the last
    /// star kept; the cap draws nothing, so what it keeps is the plan's own prefix, and a cap no tile reaches keeps everything.
    /// </summary>
    [Fact]
    public void TheCapStopsEachTilesFaintStarsWhereTheirLightReachesItAndDrawsNothing()
    {
        const int cell = 256;
        // Amplitudes are significance over 1000 (PowerLawStar), so at a sky sigma of 0.001 a star's peak reads its significance.
        double[] sigma = [0.001];
        var population = BuildPowerLawPopulation(3.0);
        var tiles = new List<(InjectionPlan Plan, InjectedStar[] Stars, InjectedStar[] Faint, int Seed, int Cx, int Cy)>();
        for (var seed = 0; seed < 40; seed++)
        {
            var (cx, cy) = (256 * (seed % 3), 256 * (seed / 3 % 3));
            var plan = population.Plan(cx, cy, cell, 8, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.5, new Random(seed), faintRng: new Random(1000 + seed));
            tiles.Add((plan, [.. plan.Stars.Select(s => s with { X = s.X - cx, Y = s.Y - cy })], [.. plan.Faint.Select(s => s with { X = s.X - cx, Y = s.Y - cy })], seed, cx, cy));
        }
        // The cap at the tiles' median uncapped level, so it touches about half of them.
        var uncapped = tiles.Select(t => t.Stars.Concat(t.Faint).Sum(s => LuminosityFunction.TileLevel(s, cell, sigma))).Order().ToArray();
        var cap = uncapped[uncapped.Length / 2];
        var (touched, untouched) = (0, 0);
        foreach (var (plan, stars, faint, seed, cx, cy) in tiles)
        {
            var again = population.Plan(cx, cy, cell, 8, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.5, new Random(seed), faintRng: new Random(1000 + seed));
            var kept = LuminosityFunction.KeptUnderCap(stars, faint, cell, sigma, cap);
            LuminosityFunction.KeptUnderCap(stars, faint, cell, sigma, double.MaxValue).ShouldBe(faint.Length);
            // Nothing drawn: the same plan again holds the same faint stars, the cap's prefix among them.
            again.Faint.Length.ShouldBe(plan.Faint.Length);
            for (var i = 0; i < plan.Faint.Length; i++)
            {
                (again.Faint[i].X, again.Faint[i].Y, again.Faint[i].Amplitudes[0]).ShouldBe((plan.Faint[i].X, plan.Faint[i].Y, plan.Faint[i].Amplitudes[0]));
            }

            var level = stars.Sum(s => LuminosityFunction.TileLevel(s, cell, sigma));
            var before = level;
            for (var i = 0; i < kept; i++)
            {
                before = level;
                level += LuminosityFunction.TileLevel(faint[i], cell, sigma);
            }
            if (kept > 0)
            {
                before.ShouldBeLessThan(cap);
            }
            if (kept < faint.Length)
            {
                level.ShouldBeGreaterThanOrEqualTo(cap);
                touched++;
            }
            else
            {
                untouched++;
            }
            // Past the cap by the last star kept at most.
            level.ShouldBeLessThanOrEqualTo(cap + (kept > 0 ? LuminosityFunction.TileLevel(faint[kept - 1], cell, sigma) : 0.0) + 1e-12);
        }
        output.WriteLine($"cap {cap:F5} (uncapped levels {uncapped[0]:F5} to {uncapped[^1]:F5}): {touched} tiles touched, {untouched} not");
        touched.ShouldBeGreaterThan(0);
        untouched.ShouldBeGreaterThan(0);

        // The measure: a 4-sigma Moffat at the tile's centre adds pi alpha^2 A / (beta - 1) over sigma, spread over the tile;
        // a star at 5 sigma or over, a saturated star and one centred outside the tile add nothing.
        StarProfile[] moffat = [new StarProfile(StarProfileFamily.Moffat, 2.5, 3.0, 1.0, 0.0)];
        InjectedStar At(double x, double amplitude, bool saturated = false) => new(x, 128, [amplitude], [.. moffat], saturated, []);
        var alpha = 2.5 / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / 3.0) - 1.0));
        var four = At(128, 0.004);
        LuminosityFunction.TileLevel(four, cell, sigma).ShouldBe(Math.PI * alpha * alpha * 4.0 / 2.0 / (cell * cell), 1e-12);
        LuminosityFunction.TileLevel(At(128, 0.005), cell, sigma).ShouldBe(0.0);
        LuminosityFunction.TileLevel(At(128, 0.004, saturated: true), cell, sigma).ShouldBe(0.0);
        LuminosityFunction.TileLevel(At(-0.5, 0.004), cell, sigma).ShouldBe(0.0);
        // The catalogue's own faint light counts first: a tile it already fills takes no faint star at all.
        LuminosityFunction.KeptUnderCap([four], [At(64, 0.003)], cell, sigma, 0.5 * LuminosityFunction.TileLevel(four, cell, sigma)).ShouldBe(0);
        LuminosityFunction.KeptUnderCap([four], [At(64, 0.003)], cell, sigma, 2.0 * LuminosityFunction.TileLevel(four, cell, sigma)).ShouldBe(1);
    }

    // Significances from dN/dln S ~ S^-alpha over [1, 1000): all kept from 10 up, half from halfFrom to 10, none under emptyBelow.
    private static (float[] Significances, Random Rng) PowerLawSignificances(double alpha, int n, Random rng, double halfFrom = 5.0, double emptyBelow = 5.0)
    {
        var (pl, ph) = (1.0, Math.Pow(1000.0, -alpha));
        var kept = new List<float>(n);
        for (var i = 0; i < n; i++)
        {
            var s = Math.Pow(pl - rng.NextDouble() * (pl - ph), -1.0 / alpha);
            if (s < emptyBelow || (s < 10.0 && s >= halfFrom && rng.NextDouble() < 0.5))
            {
                continue;
            }
            kept.Add((float)s);
        }
        return ([.. kept], rng);
    }

    // A 1024 px mono master whose catalogue follows a power law (PowerLawSignificances), amplitude its significance over 1000.
    private static InjectionPopulation BuildPowerLawPopulation(double? faintFloor)
    {
        const int size = 1024;
        var (significances, rng) = PowerLawSignificances(0.8, 2500, new Random(3));
        var catalogue = ImmutableArray.CreateRange(significances.Select(s => PowerLawStar(
            (float)(4 + rng.NextDouble() * (size - 8)), (float)(4 + rng.NextDouble() * (size - 8)), s)));
        var master = Image(size, 0.1f);
        return InjectionPopulation.Build(catalogue, master, master, 1.0, [(2.5, 3.0)], null, faintFloor: faintFloor);
    }

    private static FittedStar PowerLawStar(float x, float y, double significance)
    {
        var amplitude = (float)(significance / 1000.0);
        return new FittedStar(x, y, (float)significance, amplitude, 1f, 0.1f, 0.01f, StarFitOutcome.Subtracted, false, false, float.NaN, float.NaN, false,
            float.NaN, StarFitModel.Moffat, [amplitude]);
    }

    /// <summary>
    /// R2s2's calibration: on a master whose real stars carry more (or less) light per amplitude just outside their core than
    /// its field profile draws, each channel on its own and differently for its bright stars than its faint ones, the measure
    /// finds each band's and channel's wing gain, and reads the drawn light short of (or past) the real light beforehand.
    /// </summary>
    [Fact]
    public void AStarShapeFindsEachChannelsWingGainPerBrightnessFromTheRealStars()
    {
        const int size = 1024;
        var field = ColourField((2.6, 0.004f), (2.2, 0.004f), (2.4, 0.004f));
        double[][] trueGains = [[1.8, 1.0, 0.6], [1.3, 2.0, 1.0]];
        float[] significance = [500f, 5000f];
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    planes[c][y, x] = 0.01f;
                }
            }
        }
        var rng = new Random(4);
        var catalogue = ImmutableArray.CreateBuilder<FittedStar>();
        for (var gy = 0; gy < 8; gy++)
        {
            for (var gx = 0; gx < 8; gx++)
            {
                var band = (gx + gy) % 2;
                var (sx, sy) = (60 + gx * 120 + rng.NextDouble(), 60 + gy * 120 + rng.NextDouble());
                for (var c = 0; c < 3; c++)
                {
                    var ch = field.Channels[c];
                    var real = new StarProfile(StarProfileFamily.Field, ch.Fwhm, ch.Beta, 1.0, 0.0, ch.Table, ch.TableAlpha, Math.Log(trueGains[band][c]));
                    for (var y = (int)sy - StarShape.StampRadius; y <= (int)sy + StarShape.StampRadius + 1; y++)
                    {
                        for (var x = (int)sx - StarShape.StampRadius; x <= (int)sx + StarShape.StampRadius + 1; x++)
                        {
                            planes[c][y, x] += (float)real.PixelMean(x, y, sx, sy);
                        }
                    }
                }
                catalogue.Add(new FittedStar((float)sx, (float)sy, significance[band], 1f, 1f, 0.01f, 0.001f, StarFitOutcome.Subtracted, false, false,
                    float.NaN, float.NaN, false, float.NaN, StarFitModel.Moffat, [1f, 1f, 1f], SkyAbove: 0f, Texture: 1f));
            }
        }
        var master = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());

        var row = StarShape.Measure("synthetic", master, catalogue.ToImmutable(), field, null, bandEdges: [100f, 1000f]);
        row.Bands.Length.ShouldBe(2);
        for (var b = 0; b < 2; b++)
        {
            row.Bands[b].Stars.ShouldBe(32);
            for (var c = 0; c < 3; c++)
            {
                row.Bands[b].Channels[c].WingGain.ShouldBe(trueGains[b][c], 0.02 * trueGains[b][c], $"band {b} channel {c}");
            }
            StarShape.LogGains(row, significance[b]).ShouldBe([.. trueGains[b].Select(static g => Math.Log(g))], tolerance: 0.02);
        }
        row.Bands[0].Channels[0].DrawnLight.ShouldBeLessThan(row.Bands[0].Channels[0].RealLight, "a real wing 1.8 times the field's");
        row.Bands[0].Channels[2].DrawnLight.ShouldBeGreaterThan(row.Bands[0].Channels[2].RealLight, "a real wing 0.6 times the field's");
        StarShape.LogGains(row, 50f).ShouldBe(StarShape.LogGains(row, 500f), "a star fainter than every band takes the faintest's");

        // By default one band, from 1000: the bright stars' gains, which every star then takes.
        var bright = StarShape.Measure("synthetic", master, catalogue.ToImmutable(), field, null);
        bright.Bands.Length.ShouldBe(1);
        StarShape.LogGains(bright, 200f).ShouldBe([.. trueGains[1].Select(static g => Math.Log(g))], tolerance: 0.02);

        // The rings read the whole profile, core to wing: drawn at the gain found, each meets the real stars', which were drawn
        // from that profile at that gain.
        for (var c = 0; c < 3; c++)
        {
            var ch = bright.Bands[0].Channels[c];
            ch.RealRings.Length.ShouldBe(StarShape.RingEdgesPx.Length - 1);
            for (var k = 0; k < ch.RealRings.Length; k++)
            {
                ch.RealRings[k].ShouldBeGreaterThan(0.0);
                ch.DrawnRings[k].ShouldBe(ch.RealRings[k], 0.02 * ch.RealRings[k], $"channel {c} ring {k}");
            }
        }

        // A master with no readable star still writes its rings, unknown, never an empty array the writer cannot serialise.
        var none = StarShape.Measure("none", master, [], field, null);
        foreach (var ch in none.Bands[0].Channels)
        {
            ch.RealRings.IsDefault.ShouldBeFalse();
            ch.DrawnRings.IsDefault.ShouldBeFalse();
            ch.RealRings.ShouldAllBe(static v => double.IsNaN(v));
        }
        Should.NotThrow(() => System.Text.Json.JsonSerializer.Serialize(none, StarShapeJsonContext.Default.StarShapeRow));
    }

    /// <summary>
    /// A master calibrated to its real stars (R2s2) draws each star at its brightness band's measured wing gains, a saturated
    /// one at the brightest band's, and refuses a channel variation, which would undo the calibration.
    /// </summary>
    [Fact]
    public void ACalibratedMasterDrawsEveryStarAtItsBandsGains()
    {
        var own = ColourField((3.0, 0.002f), (2.3, 0.006f), (2.6, 0.004f));
        static StarShapeBand Band(float from, params double[] gains)
            => new StarShapeBand(from, 20, [.. gains.Select(static g => new StarShapeChannel(1.0, 1.0, g))]);
        var shape = new StarShapeRow("own", [Band(100f, 1.7, 1.4, 1.5), Band(1000f, 1.2, 2.0, 1.1)]);
        var population = BuildColourPopulation(own, shape);
        var plan = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21));
        plan.Stars.Count(static s => s.Saturated).ShouldBe(1);
        foreach (var star in plan.Stars)
        {
            // The colour population's stars were all found at significance 100: the faint band; the saturated one is the bright band's.
            var gains = star.Saturated ? StarShape.LogGains(shape, float.PositiveInfinity) : StarShape.LogGains(shape, 100f);
            for (var c = 0; c < 3; c++)
            {
                star.Profiles[c].HaloLogGain.ShouldBe(gains[c]);
            }
        }
        var a = ChannelRelation.Of("a", ColourField((2.2, 0.003f), (2.0, 0.005f), (2.4, 0.003f)));
        Should.Throw<InvalidOperationException>(() =>
            population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21), new ChannelVariation([a]), new Random(1)));
    }

    // A 1024 px three-channel master of a 16 x 16 grid of stars 60 px apart, each in one of the bands by (gx * 7 + gy * 3), drawn
    // from the field profile at that band's amplitude and its own true wing gain, on a sky of 0.01 plus exponential noise: a
    // skewed sky, whose mean is above its median, so the light annulus reads a level the sky annulus does not (the bias).
    private static (Image Master, ImmutableArray<FittedStar> Catalogue) BuildBandedMaster(StarlessFieldProfile field, float[] significance, double[] amplitude,
        Func<int, int, double> trueGain, double skew, int seed)
    {
        const int size = 1024;
        var rng = new Random(seed);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    planes[c][y, x] = (float)(0.01 - skew * Math.Log(1.0 - rng.NextDouble()));
                }
            }
        }
        var catalogue = ImmutableArray.CreateBuilder<FittedStar>();
        for (var gy = 0; gy < 16; gy++)
        {
            for (var gx = 0; gx < 16; gx++)
            {
                var band = (gx * 7 + gy * 3) % significance.Length;
                var (sx, sy) = (40 + gx * 60 + rng.NextDouble(), 40 + gy * 60 + rng.NextDouble());
                var a = amplitude[band];
                for (var c = 0; c < 3; c++)
                {
                    var ch = field.Channels[c];
                    var real = new StarProfile(StarProfileFamily.Field, ch.Fwhm, ch.Beta, 1.0, 0.0, ch.Table, ch.TableAlpha, Math.Log(trueGain(band, c)));
                    for (var y = (int)sy - StarShape.StampRadius; y <= (int)sy + StarShape.StampRadius + 1; y++)
                    {
                        for (var x = (int)sx - StarShape.StampRadius; x <= (int)sx + StarShape.StampRadius + 1; x++)
                        {
                            planes[c][y, x] += (float)(a * real.PixelMean(x, y, sx, sy));
                        }
                    }
                }
                catalogue.Add(new FittedStar((float)sx, (float)sy, significance[band], (float)a, 1f, 0.01f, 0.001f, StarFitOutcome.Subtracted, false, false,
                    float.NaN, float.NaN, false, float.NaN, StarFitModel.Moffat, [(float)a, (float)a, (float)a], SkyAbove: 0f, Texture: 1f));
            }
        }
        return (new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()), catalogue.ToImmutable());
    }

    /// <summary>
    /// #1241: the faint bands were unreadable because the light annulus is a sum over the sky annulus's median, so a skewed sky
    /// leaves a fixed level in every star's light, which per catalogued amplitude is large on a faint star and nothing on a
    /// bright one. On a field whose true wing gain is the same at every brightness, the uncorrected measure reads the faint band
    /// off by that level; with the blank sky's bias taken off every band reads the one gain and the slope reads zero.
    /// </summary>
    [Fact]
    public void AFlatTruthReadsTheSameGainInEveryBandOnceTheSkyBiasIsTakenOff()
    {
        var field = ColourField((2.6, 0.004f), (2.2, 0.004f), (2.4, 0.004f));
        double[] truth = [1.4, 1.0, 0.7];
        float[] significance = [30f, 100f, 400f];
        var (master, catalogue) = BuildBandedMaster(field, significance, [0.1, 0.3, 1.0], (_, c) => truth[c], skew: 0.001, seed: 11);
        ImmutableArray<float> edges = [20f, 60f, 200f];

        var row = StarShape.Measure("flat", master, catalogue, field, null, edges, correctSkyBias: true);
        row.Bands.Select(static b => b.MedianSignificance).ShouldBe(significance);
        for (var b = 0; b < 3; b++)
        {
            for (var c = 0; c < 3; c++)
            {
                var ch = row.Bands[b].Channels[c];
                ch.SkyBias.ShouldBeGreaterThan(1e-4, "a skewed sky leaves a level");
                ch.Stars.ShouldBeGreaterThan(40);
                ch.Weight.ShouldBeGreaterThan(0.0);
                ch.WingGain.ShouldBe(truth[c], 0.06 * truth[c], $"band {b} channel {c}");
            }
        }
        row.LogSlopes.Length.ShouldBe(3);
        row.LogSlopes.ShouldAllBe(static s => Math.Abs(s) < 0.05);

        // Left uncorrected, the faintest band reads another gain, which a slope would take for a brightness trend.
        var raw = StarShape.Measure("flat", master, catalogue, field, null, edges, correctSkyBias: false);
        raw.Bands[0].Channels[0].SkyBias.ShouldBe(0.0);
        var rawError = Math.Abs(Math.Log(raw.Bands[0].Channels[0].WingGain / truth[0]));
        var error = Math.Abs(Math.Log(row.Bands[0].Channels[0].WingGain / truth[0]));
        rawError.ShouldBeGreaterThan(0.1, "the uncorrected faint band");
        error.ShouldBeLessThan(0.5 * rawError, "the correction took out most of it");
        output.WriteLine($"faint band, channel 0: truth {truth[0]}, uncorrected {raw.Bands[0].Channels[0].WingGain:F3}, corrected {row.Bands[0].Channels[0].WingGain:F3}; " +
            $"slopes uncorrected {string.Join(" ", raw.LogSlopes.Select(static s => s.ToString("F3")))}, corrected {string.Join(" ", row.LogSlopes.Select(static s => s.ToString("F3")))}");
    }

    /// <summary>
    /// #1241: a master whose true wing gain rises with brightness as a power of the significance is read as that slope, per
    /// channel and about the master's own level, and the trend draws a star at the gain the truth gives it, clamped to the
    /// significance range the bands measured.
    /// </summary>
    [Fact]
    public void ARisingTruthIsReadAsASlopeAndDrawnAsATrend()
    {
        var field = ColourField((2.6, 0.004f), (2.2, 0.004f), (2.4, 0.004f));
        double[] level = [1.2, 1.0, 0.8];
        float[] significance = [30f, 100f, 400f];
        const double slope = 0.4;
        double Truth(int band, int c) => level[c] * Math.Pow(significance[band] / 100.0, slope);
        var (master, catalogue) = BuildBandedMaster(field, significance, [0.1, 0.3, 1.0], Truth, skew: 0.001, seed: 12);

        var row = StarShape.Measure("rising", master, catalogue, field, null, [20f, 60f, 200f], rig: "ASI294MC", correctSkyBias: true);
        row.Rig.ShouldBe("ASI294MC");
        for (var c = 0; c < 3; c++)
        {
            row.LogSlopes[c].ShouldBe(slope, 0.08, $"channel {c}'s slope");
        }
        for (var b = 0; b < 3; b++)
        {
            StarShape.LogGains(row, significance[b]).ShouldBe([.. Enumerable.Range(0, 3).Select(c => Math.Log(Truth(b, c)))], tolerance: 0.1);
        }
        // Between the bands the trend interpolates, in the log.
        var between = StarShape.LogGains(row, 200f);
        var (faint, bright) = (StarShape.LogGains(row, 100f), StarShape.LogGains(row, 400f));
        for (var c = 0; c < 3; c++)
        {
            between[c].ShouldBeGreaterThan(faint[c]);
            between[c].ShouldBeLessThan(bright[c]);
        }
        // Outside the measured range it holds the end's gain: fainter than the faintest band, or saturated.
        StarShape.LogGains(row, 5f).ShouldBe(StarShape.LogGains(row, 30f));
        StarShape.LogGains(row, float.PositiveInfinity).ShouldBe(StarShape.LogGains(row, 400f));
    }

    private static StarShapeRow TrendRow(string id, string rig, double[] brightGain, double[] slope, double weight = 50.0)
    {
        float[] significance = [30f, 100f, 400f];
        var bands = significance.Select(s => new StarShapeBand(s * 0.7f, 50,
            [.. brightGain.Select((g, c) => new StarShapeChannel(1.0, 1.0, g * Math.Pow(s / 400.0, slope[c]), Stars: 50, Weight: weight))], s));
        return new StarShapeRow(id, [.. bands], rig);
    }

    /// <summary>
    /// A rig's masters pool one slope per channel over their bands, each keeping its own level at the bright band: the pooled
    /// slope is the weighted mean of what they show, another rig's is its own, and a master with no rig keeps its own fit.
    /// </summary>
    [Fact]
    public void ARigsMastersPoolOneSlopeAndEachKeepsItsOwnLevel()
    {
        var m1 = TrendRow("m1", "ASI294MC", [1.8, 1.6, 1.4], [0.5, 0.5, 0.5]);
        var m2 = TrendRow("m2", "ASI294MC", [1.2, 1.0, 0.9], [0.3, 0.3, 0.3]);
        var other = TrendRow("o", "ASI585MC", [1.5, 1.5, 1.5], [0.0, 0.0, 0.0]);
        var orphan = TrendRow("x", "", [1.5, 1.5, 1.5], [0.2, 0.2, 0.2]);
        var rows = new[] { m1, m2, other, orphan }.ToDictionary(static r => r.SessionId, static r => r with { LogSlopes = StarShape.OwnSlopes(r) });
        rows["m1"].LogSlopes.ShouldBe([0.5, 0.5, 0.5], tolerance: 1e-9);
        rows["m2"].LogSlopes.ShouldBe([0.3, 0.3, 0.3], tolerance: 1e-9);

        var pooled = StarShape.PoolSlopesPerRig(rows);
        pooled["m1"].LogSlopes.ShouldBe([0.4, 0.4, 0.4], tolerance: 1e-9);
        pooled["m2"].LogSlopes.ShouldBe([0.4, 0.4, 0.4], tolerance: 1e-9);
        pooled["o"].LogSlopes.ShouldBe([0.0, 0.0, 0.0], tolerance: 1e-9);
        pooled["x"].LogSlopes.ShouldBe([0.2, 0.2, 0.2], 1e-9, "a master of no rig keeps its own");
        rows["m1"].LogSlopes[0].ShouldBe(0.5, 1e-12, "the input is unchanged");

        // Each master draws its brightest stars at its own level and the rig's slope below them.
        StarShape.LogGains(pooled["m1"], 400f).ShouldBe([.. new[] { 1.8, 1.6, 1.4 }.Select(static g => Math.Log(g))], tolerance: 1e-9);
        StarShape.LogGains(pooled["m2"], 400f).ShouldBe([.. new[] { 1.2, 1.0, 0.9 }.Select(static g => Math.Log(g))], tolerance: 1e-9);
        StarShape.LogGains(pooled["m2"], 100f)[0].ShouldBe(Math.Log(1.2) + 0.4 * Math.Log(100.0 / 400.0), 1e-9);

        // A band the sky's error swamps counts for nothing: m2's slope pools at its weight.
        var weak = TrendRow("m2", "ASI294MC", [1.2, 1.0, 0.9], [0.3, 0.3, 0.3], weight: 5.0);
        var heavy = StarShape.PoolSlopesPerRig(new Dictionary<string, StarShapeRow> { ["m1"] = rows["m1"], ["m2"] = weak with { LogSlopes = [0.3, 0.3, 0.3] } });
        heavy["m1"].LogSlopes[0].ShouldBe((0.5 * 50 + 0.3 * 5) / 55, 1e-9);
    }

    /// <summary>
    /// A row measured before the trend has no slope, and reads back and draws exactly as it did: every star at its band's gain.
    /// A row of the new shape round-trips its slopes, and a slope of zero is the band's gain too.
    /// </summary>
    [Fact]
    public void ARowWrittenBeforeTheTrendDrawsAtItsBandsGains()
    {
        const string old = """
            {"SessionId":"old","Bands":[
            {"MinSignificance":100,"Stars":20,"Channels":[{"RealLight":1,"DrawnLight":0.9,"WingGain":1.7},{"RealLight":1,"DrawnLight":0.9,"WingGain":1.4}]},
            {"MinSignificance":1000,"Stars":30,"Channels":[{"RealLight":1,"DrawnLight":0.9,"WingGain":1.2},{"RealLight":1,"DrawnLight":0.9,"WingGain":2.0}]}]}
            """;
        var row = System.Text.Json.JsonSerializer.Deserialize(old, StarShapeJsonContext.Default.StarShapeRow);
        row.ShouldNotBeNull();
        row.LogSlopes.IsDefaultOrEmpty.ShouldBeTrue();
        row.Rig.ShouldBe("");
        StarShape.LogGains(row, 50f).ShouldBe([Math.Log(1.7), Math.Log(1.4)]);
        StarShape.LogGains(row, 100f).ShouldBe([Math.Log(1.7), Math.Log(1.4)]);
        StarShape.LogGains(row, 999f).ShouldBe([Math.Log(1.7), Math.Log(1.4)]);
        StarShape.LogGains(row, 1000f).ShouldBe([Math.Log(1.2), Math.Log(2.0)]);
        StarShape.LogGains(row, float.PositiveInfinity).ShouldBe([Math.Log(1.2), Math.Log(2.0)]);

        // Pooling leaves a row with no bands' weight alone, and it has no slope to give.
        var pooledOld = StarShape.PoolSlopesPerRig(new Dictionary<string, StarShapeRow> { ["old"] = row with { Rig = "cam" } });
        StarShape.LogGains(pooledOld["old"], 50f).ShouldBe([Math.Log(1.7), Math.Log(1.4)]);

        // A zero slope draws the band's gain, whatever the bands.
        var zero = row with { LogSlopes = [0.0, 0.0] };
        StarShape.LogGains(zero, 50f).ShouldBe(StarShape.LogGains(row, 50f));
        StarShape.LogGains(zero, 500f).ShouldBe(StarShape.LogGains(row, 500f));

        // The new shape round-trips, and a row with no slope still writes.
        var trend = TrendRow("new", "ASI294MC", [1.8, 1.6], [0.5, 0.5]);
        trend = trend with { LogSlopes = StarShape.OwnSlopes(trend) };
        var json = System.Text.Json.JsonSerializer.Serialize(trend, StarShapeJsonContext.Default.StarShapeRow);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, StarShapeJsonContext.Default.StarShapeRow);
        back.ShouldNotBeNull();
        back.Rig.ShouldBe("ASI294MC");
        back.LogSlopes.ShouldBe(trend.LogSlopes);
        back.Bands[0].MedianSignificance.ShouldBe(30f);
        back.Bands[0].Channels[0].Weight.ShouldBe(50.0);
        Should.NotThrow(() => System.Text.Json.JsonSerializer.Serialize(new StarShapeRow("bare", trend.Bands), StarShapeJsonContext.Default.StarShapeRow));
    }

    /// <summary>
    /// The trend is opt-in (#1837 review): a run that names nothing measures the row R2s2 and synth11 were drawn from, one band
    /// from 1000 sigma, the light read with no sky bias taken off, and no slope, however the master's stars rise with brightness;
    /// and <c>star-shapes</c> with no options pools nothing and corrects nothing.
    /// </summary>
    [Fact]
    public void TheDefaultMeasureIsTheOneBandRowWithNoBiasAndNoSlope()
    {
        var field = ColourField((2.6, 0.004f), (2.2, 0.004f), (2.4, 0.004f));
        float[] significance = [30f, 100f, 1500f];
        double[] level = [1.2, 1.0, 0.8];
        var (master, catalogue) = BuildBandedMaster(field, significance, [0.1, 0.3, 1.0], (band, c) => level[c] * Math.Pow(significance[band] / 100.0, 0.4), skew: 0.001, seed: 13);

        var row = StarShape.Measure("default", master, catalogue, field, null, rig: "ASI294MC");
        row.Bands.Length.ShouldBe(1);
        row.Bands[0].MinSignificance.ShouldBe(1000f);
        row.Bands[0].Channels.ShouldAllBe(static c => c.SkyBias == 0.0, "the light is read as it is");
        row.LogSlopes.IsDefaultOrEmpty.ShouldBeTrue("one band has no slope to write");
        StarShape.LogGains(row, 30f).ShouldBe(StarShape.LogGains(row, 1500f), "every star takes the bright band's gains");

        // Asked for, the same master reads a trend: the options are what turn it on.
        var asked = StarShape.Measure("asked", master, catalogue, field, null, [20f, 60f, 200f], "ASI294MC", correctSkyBias: true);
        asked.Bands.Length.ShouldBe(3);
        asked.LogSlopes.Length.ShouldBe(3);
        asked.Bands[0].Channels[0].SkyBias.ShouldBeGreaterThan(0.0);

        var options = new TianWen.AI.Imaging.StarShapeRun.Options();
        options.BandEdges.IsDefaultOrEmpty.ShouldBeTrue();
        options.PoolByRig.ShouldBeFalse();
        options.CorrectSkyBias.ShouldBeFalse();
    }

    /// <summary>
    /// #1241 (#1837 review): the trend's level and the ends of its clamp are the bands that were READ. A brightest band with too
    /// few stars carries a borrowed gain at Weight 0 and a reference far above the others; anchoring on it would pull every
    /// fainter star's gain down by the slope times the distance to it. The readable bands' trend is what is drawn, and with
    /// fewer than two read bands a channel draws its band's gain.
    /// </summary>
    [Fact]
    public void AnUnreadableBrightestBandDoesNotAnchorTheTrend()
    {
        const double slope = 0.4;
        float[] significance = [30f, 100f, 400f];
        double[] level = [1.8, 1.4, 1.2];
        StarShapeChannel Read(double gain) => new(1.0, 1.0, gain, Stars: 50, Weight: 50.0);
        // The 2000 sigma band read nothing: the 400 band's gain lent, Weight 0, its median far above the readable ones.
        StarShapeChannel Lent(double gain) => new(double.NaN, 1.0, gain, Stars: 3, Weight: 0.0);
        var bands = significance.Select(s => new StarShapeBand(s * 0.7f, 50, [.. level.Select(g => Read(g * Math.Pow(s / 400.0, slope)))], s)).ToList();
        bands.Add(new StarShapeBand(1000f, 3, [.. level.Select(Lent)], 2000f));
        var row = new StarShapeRow("lent", [.. bands], "ASI294MC");
        row = row with { LogSlopes = StarShape.OwnSlopes(row) };
        row.LogSlopes.ShouldBe([slope, slope, slope], tolerance: 1e-9);

        for (var b = 0; b < 3; b++)
        {
            StarShape.LogGains(row, significance[b]).ShouldBe([.. level.Select(g => Math.Log(g * Math.Pow(significance[b] / 400.0, slope)))], tolerance: 1e-9);
        }
        // Past the brightest READ band it holds that band's gain, which the unread 2000 band does not move.
        StarShape.LogGains(row, 3000f).ShouldBe([.. level.Select(static g => Math.Log(g))], tolerance: 1e-9);
        StarShape.LogGains(row, float.PositiveInfinity).ShouldBe(StarShape.LogGains(row, 400f));
        StarShape.LogGains(row, 5f).ShouldBe(StarShape.LogGains(row, 30f));

        // With one read band there is no trend to anchor: the channel draws its band's gain.
        var one = new StarShapeRow("one", [bands[2], bands[3]], "ASI294MC", [slope, slope, slope]);
        StarShape.LogGains(one, 400f).ShouldBe([.. level.Select(static g => Math.Log(g))], tolerance: 1e-9);
        StarShape.LogGains(one, 100f).ShouldBe([.. level.Select(static g => Math.Log(g))], tolerance: 1e-9, customMessage: "below the only band it takes that band's gain");
    }

    // A rig's row with a brightness trend per channel: bands at 30, 100 and 400 sigma, the gains at 400 the bright ones.
    private static StarShapeRow TrendShape()
    {
        float[] significance = [30f, 100f, 400f];
        double[] bright = [1.8, 1.4, 1.2];
        double[] slope = [0.6, 0.4, 0.2];
        var bands = significance.Select(s => new StarShapeBand(s * 0.7f, 50,
            [.. bright.Select((g, c) => new StarShapeChannel(1.0, 1.0, g * Math.Pow(s / 400.0, slope[c]), Stars: 50, Weight: 5000.0))], s));
        return new StarShapeRow("trend", [.. bands], "ASI294MC", slope.ToImmutableArray());
    }

    // A master's row of the real light per amplitude, one array of channels a band; a band read to better than the stars' scatter.
    private static StarShapeRow RealRow(string id, params double[][] lights)
        => new StarShapeRow(id, [.. lights.Select((l, b) => new StarShapeBand(100f * (b + 1), 50,
            [.. l.Select(static v => new StarShapeChannel(v, v, 1.0, Stars: 50, Weight: 5000.0))], 100f * (b + 1)))]);

    // A star's channel relation as the envelope reads it: the light 3 to 8 px out per amplitude of channel c over green's, as a log.
    private static double LogRelation(ImmutableArray<StarProfile> profiles, int c)
    {
        static double Light(StarProfile p) => StarShape.DrawnLight(p with { AxisRatio = 1.0, PositionAngleRad = 0.0 }, 0.0, 0.0);
        return Math.Log(Light(profiles[c]) / Light(profiles[ChannelWingEnvelope.ReferenceChannel(profiles.Length)]));
    }

    /// <summary>
    /// #1241: with a brightness trend a star is drawn at the gain of ITS significance, so a brighter star has the stronger wing
    /// in every channel with a rising slope, and a saturated one the brightest band's.
    /// </summary>
    [Fact]
    public void ATrendDrawsBrighterStarsWithStrongerWings()
    {
        var shape = TrendShape();
        var population = BuildColourPopulation(ColourField((3.0, 0.002f), (2.3, 0.006f), (2.6, 0.004f)), shape, spreadSignificance: true);
        var plan = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 0.0, new Random(21));
        var stars = plan.Stars.Where(static s => !s.Saturated).OrderBy(static s => s.Amplitudes[0]).ToArray();
        stars.Length.ShouldBeGreaterThan(10);
        for (var c = 0; c < 3; c++)
        {
            var gains = stars.Select(s => s.Profiles[c].HaloLogGain).ToArray();
            gains.ShouldBe([.. gains.Order()], $"channel {c}: the wing never weakens with brightness");
            gains[^1].ShouldBeGreaterThan(gains[0] + 0.05, $"channel {c}: the brightest star's wing is stronger than the faintest's");
        }
        foreach (var star in stars)
        {
            var expected = StarShape.LogGains(shape, 20f + 1000f * (float)star.Amplitudes[0]);
            for (var c = 0; c < 3; c++)
            {
                star.Profiles[c].HaloLogGain.ShouldBe(expected[c], 1e-9);
            }
        }
        var saturated = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21)).Stars.Single(static s => s.Saturated);
        for (var c = 0; c < 3; c++)
        {
            saturated.Profiles[c].HaloLogGain.ShouldBe(StarShape.LogGains(shape, float.PositiveInfinity)[c], 1e-9);
        }
    }

    /// <summary>
    /// The envelope is each channel's least and greatest light ratio to green's over the real masters' reliable bands: a band whose
    /// light the sky's error swamps, a mono master, and a row measured before the weight was kept say nothing.
    /// </summary>
    [Fact]
    public void AnEnvelopeIsTheRealMastersSpanInTheirReadableBands()
    {
        var unreliable = new StarShapeRow("noisy", [new StarShapeBand(10f, 50, [.. new[] { 5.0, 1.0, 0.1 }.Select(static v => new StarShapeChannel(v, v, 1.0, Stars: 50, Weight: 10.0))], 10f)]);
        var envelope = ChannelWingEnvelope.Read([
            RealRow("a", [0.9, 1.0, 1.1], [1.1, 1.0, 0.95]),
            RealRow("b", [1.0, 1.0, 1.3]),
            unreliable,
            RealRow("mono", [1.0]),
            RealRow("pair", [2.0, 1.0])]);
        var bounds = envelope.BoundsFor(3).ShouldNotBeNull();
        bounds[0].ShouldBe((Math.Log(0.9), Math.Log(1.1)));
        bounds[1].ShouldBe((0.0, 0.0), "green is the reference");
        bounds[2].ShouldBe((Math.Log(0.95), Math.Log(1.3)));
        envelope.BandsByChannels[3].ShouldBe(3, "the noisy band is not read");
        envelope.BoundsFor(1).ShouldBeNull();
        envelope.BoundsFor(2).ShouldNotBeNull()[0].ShouldBe((Math.Log(2.0), Math.Log(2.0)));
        envelope.BoundsFor(4).ShouldBeNull();

        var before = new StarShapeRow("old", [new StarShapeBand(1000f, 20, [.. Enumerable.Repeat(new StarShapeChannel(1.0, 1.0, 1.2), 3)])]);
        var none = ChannelWingEnvelope.Read([before]);
        none.BoundsFor(3).ShouldBeNull("a row with no weight is no reading");
        none.InWords().ShouldContain("nothing is clamped");
    }

    /// <summary>The clamp's two readings per channel make the light a line in the gain, as the real measure draws it.</summary>
    [Fact]
    public void AWingGainDrawsALineInTheLightPerAmplitude()
    {
        var field = ColourField((3.0, 0.002f), (2.3, 0.006f), (2.6, 0.004f));
        var round = field.Channels.Select(static c => new StarProfile(StarProfileFamily.Field, c.Fwhm, c.Beta, 1.0, 0.0, c.Table, c.TableAlpha)).ToImmutableArray();
        var clamp = new WingRelationClamp(round, [(-1.0, 1.0), (0.0, 0.0), (-1.0, 1.0)]);
        for (var c = 0; c < 3; c++)
        {
            foreach (var gain in new[] { 0.3, 1.0, 3.0, 7.0 })
            {
                var read = StarShape.DrawnLight(round[c] with { HaloLogGain = Math.Log(gain) }, 0.0, 0.0);
                // The light is read off a float plane, so a line to a part in 1e7 is a line.
                clamp.LightAt(c, gain).ShouldBe(read, 1e-5 * Math.Abs(read), $"channel {c} at gain {gain}");
            }
        }
    }

    /// <summary>
    /// #1241: the trend moves each channel's wing on its own, so a drawn star's channels can leave every real master's relation;
    /// the clamp puts them back, and no drawn star (lobes, saturated, faint alike) stays outside. It moves green never, draws no
    /// random number (the same stars land at the same places with the same colours), and counts the stars it moved.
    /// </summary>
    [Fact]
    public void AClampedStarsChannelsStayInsideTheEnvelopeTheRealMastersSpan()
    {
        // One width in every channel, so the wing gain alone can reach any relation the envelope asks for.
        var field = ColourField((2.4, 0.002f), (2.4, 0.006f), (2.4, 0.004f));
        var shape = TrendShape();
        var envelope = ChannelWingEnvelope.Read([RealRow("a", [0.9, 1.0, 1.1], [1.1, 1.0, 0.95]), RealRow("b", [1.0, 1.0, 1.05])]);
        var bounds = envelope.BoundsFor(3).ShouldNotBeNull();
        var free = BuildColourPopulation(field, shape, spreadSignificance: true);
        var clamped = BuildColourPopulation(field, shape, spreadSignificance: true, wingEnvelope: envelope);
        free.ClampsWingRelations.ShouldBeFalse();
        clamped.ClampsWingRelations.ShouldBeTrue();

        var outsideWithout = 0;
        var moved = 0;
        for (var seed = 21; seed < 25; seed++)
        {
            var plan = free.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(seed));
            var held = clamped.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(seed));
            held.Stars.Length.ShouldBe(plan.Stars.Length);
            var changed = 0;
            for (var i = 0; i < plan.Stars.Length; i++)
            {
                var (before, after) = (plan.Stars[i], held.Stars[i]);
                (after.X, after.Y, after.Saturated).ShouldBe((before.X, before.Y, before.Saturated));
                after.Amplitudes.ShouldBe(before.Amplitudes, "a star keeps its brightness and colour");
                after.Profiles[1].HaloLogGain.ShouldBe(before.Profiles[1].HaloLogGain, "green is never moved");
                for (var c = 0; c < 3; c += 2)
                {
                    var (lo, hi) = bounds[c];
                    var wasOutside = LogRelation(before.Profiles, c) is var r && (r < lo - 1e-5 || r > hi + 1e-5);
                    outsideWithout += wasOutside ? 1 : 0;
                    LogRelation(after.Profiles, c).ShouldBeInRange(lo - 1e-5, hi + 1e-5, $"seed {seed} star {i} channel {c}");
                    if (!wasOutside)
                    {
                        after.Profiles[c].HaloLogGain.ShouldBe(before.Profiles[c].HaloLogGain, "a channel inside the envelope is left as the trend drew it");
                    }
                }
                changed += after.Profiles.SequenceEqual(before.Profiles) ? 0 : 1;
            }
            held.WingClamped.ShouldBe(changed);
            held.WingUnreached.ShouldBe(0, "the wing gain reaches every edge here");
            plan.WingClamped.ShouldBe(0);
            moved += changed;
        }
        outsideWithout.ShouldBeGreaterThan(0, "the trend must leave the envelope somewhere, or this tests nothing");
        moved.ShouldBeGreaterThan(0);

        // An envelope as wide as no real master could be moves nothing.
        var wide = ChannelWingEnvelope.Read([RealRow("w", [0.001, 1.0, 0.001], [1000.0, 1.0, 1000.0])]);
        var open = BuildColourPopulation(field, shape, spreadSignificance: true, wingEnvelope: wide)
            .Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21));
        open.WingClamped.ShouldBe(0);
        open.Stars.Select(static s => s.Profiles).ShouldBe(free.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 1.0, new Random(21)).Stars.Select(static s => s.Profiles));
    }

    /// <summary>
    /// A channel much wider than green carries more of the 3 to 8 px light in its core alone than a tight envelope allows, whatever
    /// its wing: the clamp takes the gain to its bound, the nearest the draw can be, and says so rather than claim the star is held.
    /// </summary>
    [Fact]
    public void AnEdgeTheWingGainCannotReachIsSaidAndTakenToTheGainsBound()
    {
        var field = ColourField((3.0, 0.002f), (2.3, 0.006f), (2.6, 0.004f));
        var envelope = ChannelWingEnvelope.Read([RealRow("tight", [1.0, 1.0, 1.0])]);
        var population = BuildColourPopulation(field, TrendShape(), spreadSignificance: true, wingEnvelope: envelope);
        var plan = population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 0.0, new Random(21));
        plan.WingClamped.ShouldBeGreaterThan(0);
        plan.WingUnreached.ShouldBeGreaterThan(0);
        plan.Stars.ShouldContain(static s => Math.Abs(s.Profiles[0].HaloLogGain - Math.Log(StarShape.MinGain)) < 1e-12, "red, whose core is the widest, sits at the least gain");
    }

    /// <summary>A clamp holds the field profile's stars to the real envelope: a wing cut or a variation departs from it on
    /// purpose, and a profile that is not the field's is not the envelope's measure.</summary>
    [Fact]
    public void AWingRelationClampRefusesWhatDepartsFromTheEnvelope()
    {
        var field = ColourField((3.0, 0.002f), (2.3, 0.006f), (2.6, 0.004f));
        var envelope = ChannelWingEnvelope.Read([RealRow("a", [0.9, 1.0, 1.1])]);
        var population = BuildColourPopulation(field, TrendShape(), spreadSignificance: true, wingEnvelope: envelope);
        Should.Throw<InvalidOperationException>(() => population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Field, 0.0, new Random(1),
            variationRng: new Random(2), wingCut: new ChannelWingCut(0.5)));
        Should.Throw<InvalidOperationException>(() => population.Plan(0, 0, 256, 8, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.0, new Random(1)));
        Should.Throw<ArgumentException>(() => BuildColourPopulation(field: null, wingEnvelope: envelope));
        // An envelope with no master of the channel count is no clamp.
        BuildColourPopulation(field, wingEnvelope: ChannelWingEnvelope.Read([RealRow("mono", [1.0])])).ClampsWingRelations.ShouldBeFalse();
    }

    // A three-channel field profile: each channel's FWHM (beta 3) and a flat halo from 2 to 10 px at the field's width.
    private static StarlessFieldProfile ColourField((double Fwhm, float Halo) red, (double Fwhm, float Halo) green, (double Fwhm, float Halo) blue)
    {
        static FieldChannelProfile Channel((double Fwhm, float Halo) c)
        {
            var alpha = MoffatPsf.AlphaFor(c.Fwhm, 3.0);
            return new FieldChannelProfile(alpha, 3.0, alpha, [.. Enumerable.Range(0, 60).Select(i => i is >= 8 and < 40 ? c.Halo : 0f)]);
        }
        return new StarlessFieldProfile([Channel(red), Channel(green), Channel(blue)], Channel(green));
    }

    // BuildPopulation in three channels, each star a little redder than blue, the master's saturated star cored at 0.9 in all.
    // With spreadSignificance each star's significance rises with its amplitude (20 + 1000 x amplitude, 30 to 230), not all 100.
    private static InjectionPopulation BuildColourPopulation(StarlessFieldProfile? field = null, StarShapeRow? shape = null, bool spreadSignificance = false,
        ChannelWingEnvelope? wingEnvelope = null)
    {
        const int size = 256;
        var rng = new Random(2);
        var catalogue = ImmutableArray.CreateBuilder<FittedStar>();
        FittedStar ColourStar(float x, float y, float amplitude, bool saturated)
            => new FittedStar(x, y, spreadSignificance ? 20f + 1000f * amplitude : 100f, amplitude, 1f, 0.1f, 0.01f, StarFitOutcome.Subtracted, saturated, false,
                float.NaN, float.NaN, false, float.NaN, StarFitModel.Moffat, [amplitude, 0.8f * amplitude, 0.6f * amplitude]);
        for (var i = 0; i < 30; i++)
        {
            catalogue.Add(ColourStar((float)(10 + rng.NextDouble() * (size - 20)), (float)(10 + rng.NextDouble() * (size - 20)),
                (float)(0.01 + rng.NextDouble() * 0.2), saturated: false));
        }
        catalogue.Add(ColourStar(200, 40, 3.0f, saturated: true));
        var masterPlanes = new float[3][,];
        var platePlanes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            masterPlanes[c] = new float[size, size];
            platePlanes[c] = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    masterPlanes[c][y, x] = 0.1f;
                    platePlanes[c][y, x] = 0.1f;
                }
            }
            masterPlanes[c][40, 200] = 0.9f;
        }
        var master = new Image(masterPlanes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var plate = new Image(platePlanes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        return InjectionPopulation.Build(catalogue.ToImmutable(), master, plate, 1.0, [(2.5, 3.0), (2.5, 3.0), (2.5, 3.0)], null, field, shape: shape,
            wingEnvelope: wingEnvelope);
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
