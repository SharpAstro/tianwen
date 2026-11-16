using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// An unsaturated bright star drawn as its master shows it (<see cref="InjectionPopulation.MeasureBright"/>, --bright-measured,
/// #2192): matched on its core so its flank comes out as its own, with the rest of its light as its halo table; which pool stars
/// it takes, and how <see cref="InjectionPopulation.Plan"/> draws one.
/// </summary>
[Collection("Imaging")]
public class MeasuredBrightStarTests
{
    private const double Sky = 0.01;
    private static readonly (double Fwhm, double Beta) FieldPsf = (2.5, 3.0);

    private static StarProfile Truth((double Fwhm, double Beta) psf) => StarProfile.Round(StarProfileFamily.Moffat, psf.Fwhm, psf.Beta);

    // The median of f over the pixels whose distance from the centre rounds to k, as the measurement bins its rings.
    private static double RingMedian(Func<int, int, double> f, double cx, double cy, int k)
    {
        var values = new List<double>();
        for (var y = (int)cy - k - 2; y <= (int)cy + k + 2; y++)
        {
            for (var x = (int)cx - k - 2; x <= (int)cx + k + 2; x++)
            {
                if ((int)Math.Round(Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)))) == k)
                {
                    values.Add(f(x, y));
                }
            }
        }
        return StatisticsHelper.MedianFast(values.ToArray());
    }

    // The star's own light at ring k: its ring median less what the measurement takes as sky (32 to 36 px).
    private static double OwnLight(Func<int, int, double> star, double cx, double cy, int k)
    {
        var sky = new List<double>();
        for (var y = (int)cy - 37; y <= (int)cy + 37; y++)
        {
            for (var x = (int)cx - 37; x <= (int)cx + 37; x++)
            {
                var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                if (r >= InjectionPopulation.MeasuredSkyFromPx && r <= InjectionPopulation.MeasuredSkyToPx)
                {
                    sky.Add(star(x, y));
                }
            }
        }
        return RingMedian(star, cx, cy, k) - StatisticsHelper.MedianFast(sky.ToArray());
    }

    private static float[][] OneStar(int size, double cx, double cy, double amplitude, (double Fwhm, double Beta)[] psfs)
    {
        var planes = new float[psfs.Length][];
        for (var c = 0; c < psfs.Length; c++)
        {
            var truth = Truth(psfs[c]);
            planes[c] = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    planes[c][(y * size) + x] = (float)(Sky + (amplitude * truth.PixelMean(x, y, cx, cy)));
                }
            }
        }
        return planes;
    }

    private static StarProfile[] Drawn(int channels) => [.. Enumerable.Range(0, channels).Select(_ => Truth(FieldPsf))];

    private static void ShouldBeDrawnBack(
        (ImmutableArray<double> Amplitudes, ImmutableArray<OuterHalo> Halo) m, StarProfile field, (double Fwhm, double Beta) psf, double amplitude, int channel, double cx, double cy)
    {
        var own = Truth(psf);
        var drawn = field with { Outer = m.Halo[channel] };
        foreach (var k in new[] { 2, 3, 4 })
        {
            var want = amplitude * OwnLight((x, y) => own.PixelMean(x, y, cx, cy), cx, cy, k);
            var got = m.Amplitudes[channel] * RingMedian((x, y) => drawn.PixelMean(x, y, cx, cy), cx, cy, k);
            (got / want).ShouldBe(1.0, 0.03, $"channel {channel}, {k} px");
        }
    }

    [Theory]
    [InlineData(6.0)]
    [InlineData(1.8)]
    public void AStarNarrowerOrWiderOnItsFlankThanTheFieldIsDrawnBackAtItsOwnCoreHeight(double beta)
    {
        const int size = 101;
        const double cx = 50.3, cy = 49.8, amplitude = 0.3;
        var planes = OneStar(size, cx, cy, amplitude, [(FieldPsf.Fwhm, beta)]);
        var m = InjectionPopulation.MeasureBright(planes, null, null, size, size, cx, cy, 1.0, Drawn(1)).ShouldNotBeNull();
        m.Amplitudes[0].ShouldBe(amplitude, amplitude * 0.1, "matched on its core, not its wing");
        ShouldBeDrawnBack(m, Drawn(1)[0], (FieldPsf.Fwhm, beta), amplitude, 0, cx, cy);
        // Narrower: the field profile is brighter than the star on the flank, so the table is negative; wider, positive.
        (beta > FieldPsf.Beta ? m.Halo[0].Table[4] < 0f : m.Halo[0].Table[4] > 0f).ShouldBeTrue($"table at 4 px is {m.Halo[0].Table[4]}");
    }

    [Fact]
    public void EachChannelKeepsItsOwnFlank()
    {
        const int size = 101;
        const double cx = 50.3, cy = 49.8, amplitude = 0.3;
        (double Fwhm, double Beta)[] psfs = [(FieldPsf.Fwhm, 6.0), FieldPsf, (FieldPsf.Fwhm, 1.8)];
        var planes = OneStar(size, cx, cy, amplitude, psfs);
        var field = Drawn(3);
        var m = InjectionPopulation.MeasureBright(planes, null, null, size, size, cx, cy, 1.0, field).ShouldNotBeNull();
        for (var c = 0; c < 3; c++)
        {
            ShouldBeDrawnBack(m, field[c], psfs[c], amplitude, c, cx, cy);
        }
        m.Halo[0].Table[4].ShouldBeLessThan(0f, "red is narrower");
        m.Halo[2].Table[4].ShouldBeGreaterThan(0f, "blue is wider");
        Math.Abs(m.Halo[1].Table[4]).ShouldBeLessThan(Math.Abs(m.Halo[0].Table[4]), "green is the field's");
    }

    [Fact]
    public void AStampWithAnAbsentOrNonFinitePixelOrOffTheFrameIsNotMeasured()
    {
        const int size = 101;
        const double cx = 50.3, cy = 49.8;
        var planes = OneStar(size, cx, cy, 0.3, [FieldPsf]);
        InjectionPopulation.MeasureBright(planes, null, null, size, size, 20.0, cy, 1.0, Drawn(1)).ShouldBeNull("the sky ring leaves the frame");
        planes[0][(50 * size) + 70] = float.NaN;
        InjectionPopulation.MeasureBright(planes, null, null, size, size, cx, cy, 1.0, Drawn(1)).ShouldBeNull("a NaN in the stamp");
    }

    // ---- the pool: which stars are measured ----

    private sealed record Spec(double X, double Y, float Significance, double Amplitude, bool Saturated = false, bool SecondPass = false);

    private const int Width = 700, Height = 620, GoodStars = 30;

    private static IEnumerable<Spec> Good(int count)
        => Enumerable.Range(0, count).Select(i => new Spec(60.3 + (100 * (i % 6)), 60.6 + (100 * (i / 6)), 200f, 0.2));

    private static FittedStar Row(Spec s)
        => new FittedStar((float)s.X, (float)s.Y, s.Significance, (float)s.Amplitude, 1f, 0.1f, 0.01f, StarFitOutcome.Subtracted, s.Saturated, false,
            float.NaN, float.NaN, s.SecondPass, float.NaN, StarFitModel.Moffat, [(float)s.Amplitude]);

    private static InjectionPopulation Population(
        IEnumerable<Spec> specs, bool measuredBright, bool lobes = false, float? nanAt = null, bool zeroStrip = false, bool passAbsent = false)
    {
        var list = specs.ToList();
        var master = new float[Height, Width];
        var plate = new float[Height, Width];
        var truth = Truth(FieldPsf);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                plate[y, x] = (float)Sky;
                master[y, x] = (float)Sky;
            }
        }
        foreach (var s in list)
        {
            for (var y = Math.Max(0, (int)s.Y - 45); y <= Math.Min(Height - 1, (int)s.Y + 45); y++)
            {
                for (var x = Math.Max(0, (int)s.X - 45); x <= Math.Min(Width - 1, (int)s.X + 45); x++)
                {
                    master[y, x] = Math.Min(0.9f, master[y, x] + (float)(s.Amplitude * truth.PixelMean(x, y, s.X, s.Y)));
                }
            }
        }
        if (nanAt is { } nx)
        {
            master[560, (int)nx] = float.NaN;
        }
        if (zeroStrip)
        {
            for (var y = 0; y < Height; y++)
            {
                for (var x = 660; x < Width; x++)
                {
                    master[y, x] = 0f;
                    plate[y, x] = 0f;
                }
            }
        }
        var masterImage = new Image([master], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var plateImage = new Image([plate], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var absent = passAbsent ? plateImage.AbsentPixels().ShouldNotBeNull() : null;
        return InjectionPopulation.Build([.. list.Select(Row)], masterImage, plateImage, 1.0, [FieldPsf], absent, lobes: lobes, measuredBright: measuredBright);
    }

    private static InjectionPopulation Without(IEnumerable<Spec> specs) => Population(specs, measuredBright: false);

    [Fact]
    public void EveryEligibleStarOfASessionIsMeasuredAndDrawnOnItsOwnProfiles()
    {
        var pop = Population(Good(GoodStars), measuredBright: true);
        pop.BrightMeasured.ShouldBe(GoodStars);
        pop.BrightFellBack.ShouldBe(0);
        pop.BrightSessionFellBack.ShouldBeFalse();
        var plan = pop.Plan(0, 0, 700, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.0, new Random(3));
        plan.Stars.ShouldNotBeEmpty();
        foreach (var s in plan.Stars)
        {
            s.Measured.ShouldBeTrue();
            s.Saturated.ShouldBeFalse();
            s.Profiles[0].Outer.IsNone.ShouldBeFalse("its own table");
            s.Amplitudes[0].ShouldBe(0.2, 0.02);
        }
    }

    public static TheoryData<string, int, int> Fallbacks => new()
    {
        { "saturated", GoodStars, 0 },
        { "low significance", GoodStars, 1 },
        { "crowded", GoodStars, 2 },
        { "non-finite pixel", GoodStars, 1 },
        { "absent pixel", GoodStars, 1 },
    };

    [Theory]
    [MemberData(nameof(Fallbacks))]
    public void AStarThatIsNotEligibleStaysOnTheFieldProfile(string kind, int measured, int fellBack)
    {
        var specs = Good(GoodStars).ToList();
        var extra = new Spec(60.2, 560.4, 200f, 0.2);
        switch (kind)
        {
            case "saturated":
                specs.Add(extra with { Amplitude = 5.0, Saturated = true });
                break;
            case "low significance":
                specs.Add(extra with { Significance = 60f });
                break;
            case "crowded":
                specs.Add(extra);
                specs.Add(new Spec(68.2, 560.4, 200f, 0.1));
                break;
            case "non-finite pixel":
                specs.Add(extra);
                break;
            case "absent pixel":
                specs.Add(extra with { X = 640.2 });
                break;
        }
        var pop = Population(specs, measuredBright: true, nanAt: kind == "non-finite pixel" ? 80f : null, zeroStrip: kind == "absent pixel", passAbsent: kind == "absent pixel");
        pop.BrightMeasured.ShouldBe(measured, kind);
        pop.BrightFellBack.ShouldBe(fellBack, kind);
        pop.BrightSessionFellBack.ShouldBeFalse();
    }

    [Fact]
    public void WithoutTheAbsentMatrixTheSameStarNearTheCanvasRingIsMeasured()
    {
        var specs = Good(GoodStars).Append(new Spec(640.2, 560.4, 200f, 0.2));
        var pop = Population(specs, measuredBright: true, zeroStrip: true, passAbsent: false);
        pop.BrightMeasured.ShouldBe(GoodStars + 1, "the zeros are finite, so only the matrix refuses the stamp");
    }

    [Fact]
    public void ALobeIsNeitherMeasuredNorACrowdingNeighbourOfItsStar()
    {
        var specs = Good(GoodStars).Concat([new Spec(60.2, 560.4, 200f, 0.2), new Spec(64.7, 560.4, 150f, 0.1, SecondPass: true)]);
        var pop = Population(specs, measuredBright: true, lobes: true);
        pop.BrightMeasured.ShouldBe(GoodStars + 1, "the star is measured; its lobe is part of it");
        pop.BrightFellBack.ShouldBe(0);
        var plan = pop.Plan(0, 0, 700, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.0, new Random(5));
        plan.Stars.Any(static s => s.Lobe).ShouldBeFalse("a measured star draws no lobes of its own");
    }

    [Fact]
    public void UnderTwentyMeasuredStarsTheSessionDrawsAsWithoutTheOption()
    {
        var specs = Good(InjectionPopulation.MinBrightMeasured - 1).ToList();
        var pop = Population(specs, measuredBright: true);
        pop.BrightMeasured.ShouldBe(0);
        pop.BrightSessionFellBack.ShouldBeTrue();
        ShouldPlanAlike(Without(specs), pop);
    }

    [Fact]
    public void TheOptionOffPlansIdenticallyToBuildWithoutTheArgument()
    {
        var specs = Good(GoodStars).ToList();
        var off = Population(specs, measuredBright: false);
        off.BrightMeasured.ShouldBe(0);
        off.BrightSessionFellBack.ShouldBeFalse();
        var plain = InjectionPopulation.Build([.. specs.Select(Row)], Flat(specs, out var plate), plate, 1.0, [FieldPsf], null);
        ShouldPlanAlike(plain, off);
        off.Plan(0, 0, 700, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.0, new Random(9)).Stars.ShouldAllBe(static s => !s.Measured);
    }

    // Master and plate built exactly as Population does, for a Build called without the new argument.
    private static Image Flat(List<Spec> specs, out Image plate)
    {
        var master = new float[Height, Width];
        var plane = new float[Height, Width];
        var truth = Truth(FieldPsf);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                plane[y, x] = (float)Sky;
                master[y, x] = (float)Sky;
            }
        }
        foreach (var s in specs)
        {
            for (var y = Math.Max(0, (int)s.Y - 45); y <= Math.Min(Height - 1, (int)s.Y + 45); y++)
            {
                for (var x = Math.Max(0, (int)s.X - 45); x <= Math.Min(Width - 1, (int)s.X + 45); x++)
                {
                    master[y, x] = Math.Min(0.9f, master[y, x] + (float)(s.Amplitude * truth.PixelMean(x, y, s.X, s.Y)));
                }
            }
        }
        plate = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        return new Image([master], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }

    private static void ShouldPlanAlike(InjectionPopulation expected, InjectionPopulation actual)
    {
        var a = expected.Plan(0, 0, 700, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.0, new Random(11));
        var b = actual.Plan(0, 0, 700, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 0.0, new Random(11));
        b.Stars.Length.ShouldBe(a.Stars.Length);
        for (var i = 0; i < a.Stars.Length; i++)
        {
            b.Stars[i].X.ShouldBe(a.Stars[i].X);
            b.Stars[i].Y.ShouldBe(a.Stars[i].Y);
            b.Stars[i].Amplitudes.ShouldBe(a.Stars[i].Amplitudes);
            b.Stars[i].Saturated.ShouldBe(a.Stars[i].Saturated);
            b.Stars[i].Measured.ShouldBe(a.Stars[i].Measured);
            b.Stars[i].Profiles[0].Outer.IsNone.ShouldBe(a.Stars[i].Profiles[0].Outer.IsNone);
            b.Stars[i].Profiles[0].Fwhm.ShouldBe(a.Stars[i].Profiles[0].Fwhm);
        }
    }
}
