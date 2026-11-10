using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A master's measured bright-star halo (<see cref="BrightHalo"/>): the per-pixel table an <see cref="OuterHalo"/> draws, the class
/// a star takes by its significance, and the file the exporter reads.
/// </summary>
[Collection("Imaging")]
public class BrightHaloTests : IDisposable
{
    private readonly TempFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly ImmutableArray<float> Ramp = [0f, 0f, 0.02f, 0.01f, 0.004f, 0.001f];

    [Fact]
    public void ATableIsReadLinearlyBetweenItsEntriesAndIsNothingPastTheLast()
    {
        var halo = new OuterHalo(0, 0, 0, Ramp);
        halo.IsNone.ShouldBeFalse();
        halo.At(2, 0).ShouldBe(0.02, 1e-7);
        halo.At(0, 3).ShouldBe(0.01, 1e-7, "round");
        halo.AtRadius(2.5).ShouldBe(0.015, 1e-7);
        halo.AtRadius(5).ShouldBe(0.001, 1e-7);
        halo.AtRadius(5.5).ShouldBe(0.0);
        halo.AtRadius(40).ShouldBe(0.0);
        halo.PixelMean(30, 0).ShouldBe(0.0);
    }

    [Fact]
    public void ATableReachesAsFarAsItsLastEntryAtTheFraction()
    {
        var halo = new OuterHalo(0, 0, 0, Ramp);
        halo.RadiusAtFraction(0.005).ShouldBe(4.0);
        halo.RadiusAtFraction(0.0005).ShouldBe(5.0);
        halo.RadiusAtFraction(0.5).ShouldBe(0.0, "never reached");
        var profile = StarProfile.Round(StarProfileFamily.Moffat, 2.0, 4.0);
        (profile with { Outer = halo }).RadiusAtFraction(0.0005).ShouldBeGreaterThanOrEqualTo(5.0);
    }

    [Fact]
    public void ANegativeTableNeverDrawsNegativeLightNorShortensTheReach()
    {
        // A measured saturated star's table is negative where the drawn profile is brighter than the star.
        var profile = StarProfile.Round(StarProfileFamily.Moffat, 2.0, 4.0);
        var under = profile with { Outer = new OuterHalo(0, 0, 0, [0f, 0f, -0.5f, -0.5f, -0.5f, -0.5f, -0.5f]) };
        profile.PixelMean(4, 0, 0, 0).ShouldBeGreaterThan(0.0);
        under.PixelMean(4, 0, 0, 0).ShouldBe(0.0, "the table takes more than the profile holds there");
        under.PixelMean(0, 0, 0, 0).ShouldBe(profile.PixelMean(0, 0, 0, 0), 1e-12, "the table is nothing at the centre");
        under.At(4, 0).ShouldBe(profile.At(4, 0), "the point value is the profile's alone; the halo is drawn in the pixel mean");
        under.RadiusAtFraction(1e-4).ShouldBe(profile.RadiusAtFraction(1e-4), "the stamp is the profile's, never smaller");
    }

    [Fact]
    public void AMoffatAndATableAreAddedTogether()
    {
        var moffat = new OuterHalo(4e-3, 6.0, 1.2);
        var both = moffat with { Table = Ramp };
        both.AtRadius(3).ShouldBe(moffat.AtRadius(3) + 0.01f, 1e-9, "the table holds floats");
    }

    [Fact]
    public void AStarTakesItsClassByItsSignificance()
    {
        ImmutableArray<ImmutableArray<float>> two(float v) => [[0f, v], [0f, v]];
        var halo = new BrightHalo("s", two(0.1f), two(0.2f));
        halo.For(999f, 2).IsDefault.ShouldBeTrue("under the bright class");
        halo.For(BrightHalo.MinSignificance, 2).Select(h => h.AtRadius(1)).ShouldAllBe(v => Math.Abs(v - 0.1) < 1e-7);
        halo.For(float.PositiveInfinity, 2).Select(h => h.AtRadius(1)).ShouldAllBe(v => Math.Abs(v - 0.2) < 1e-7, "saturated");
        halo.For(float.PositiveInfinity, 3).IsDefault.ShouldBeTrue("a table of another channel count draws none");
        new BrightHalo("s", [], two(0.2f)).For(5000f, 2).IsDefault.ShouldBeTrue("a class with no table draws none");
        halo.For(float.PositiveInfinity, 2)[0].Source.ShouldBe("s:saturated");
    }

    [Fact]
    public void ASaturatedStarIsMeasuredAtTheAmplitudeItsOwnLightMatchesWithTheRestAsItsHalo()
    {
        const int size = 101;
        const double cx = 50.3, cy = 49.8, amplitude = 5.0, sky = 0.01, clip = 1.0;
        var profile = StarProfile.Round(StarProfileFamily.Moffat, 2.5, 3.0);
        static double Halo(double r) => r >= 2 ? 2e-3 * Math.Exp(-r / 8.0) : 0.0;
        var plane = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                // Only the match ring (3 to 5 px) and the halo past it need to be exact; the core is clipped as a real one is.
                plane[(y * size) + x] = (float)Math.Min(clip + sky, sky + (amplitude * (profile.UngainedAt(r) + Halo(r))));
            }
        }
        var measured = InjectionPopulation.MeasureSaturated([plane], size, size, cx, cy, 1.0, [profile], [clip + sky]);
        measured.ShouldNotBeNull();
        var (amplitudes, clips, halo) = measured.Value;
        clips[0].ShouldBe(clip + sky, "the wings say the star was brighter than its top, so its core is its clip level");
        // The match ring holds the halo too, so the amplitude comes out a little high, by the halo's share there, never by more.
        amplitudes[0].ShouldBe(amplitude, amplitude * 0.15);
        foreach (var r in new[] { 8, 12, 20 })
        {
            var expected = ((amplitude * (profile.UngainedAt(r) + Halo(r))) - (amplitudes[0] * profile.UngainedAt(r))) / amplitudes[0];
            halo[0].AtRadius(r).ShouldBe(expected, Math.Abs(expected) * 0.2 + 2e-5, $"the halo at {r} px is the light the drawn profile misses");
        }
        halo[0].AtRadius(1).ShouldBe(0.0, "the core is the drawn profile's");
        halo[0].Source.ShouldBe("measured saturated star");
        // A stamp off the frame is not measured.
        InjectionPopulation.MeasureSaturated([plane], size, size, 10, 50, 1.0, [profile], [clip + sky]).ShouldBeNull();
    }

    private const double Sky = 0.1, Ceiling = 0.9, StarX = 128.3, StarY = 127.8;

    private static readonly StarProfile Truth = StarProfile.Round(StarProfileFamily.Moffat, 2.5, 3.0);

    private static double TruthHalo(double r) => r >= 2 ? 2e-3 * Math.Exp(-r / 8.0) : 0.0;

    // A star as a master holds it: the sky plus its amplitude times the profile's pixel mean and a halo, cut at the ceiling.
    private static float[,] StarPlane(int size, double amplitude)
    {
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var r = Math.Sqrt(((x - StarX) * (x - StarX)) + ((y - StarY) * (y - StarY)));
                plane[y, x] = (float)Math.Min(Ceiling, Sky + (amplitude * (Truth.PixelMean(x, y, StarX, StarY) + TruthHalo(r))));
            }
        }
        return plane;
    }

    private static float[] Flat(float[,] plane)
    {
        var flat = new float[plane.Length];
        Buffer.BlockCopy(plane, 0, flat, 0, plane.Length * sizeof(float));
        return flat;
    }

    // The median of the pixels whose distance from (cx, cy) rounds to ring, or to any ring of [ring, toRing].
    private static double RingMedian(float[] plane, int size, double cx, double cy, int ring, int toRing = -1)
    {
        toRing = toRing < 0 ? ring : toRing;
        var values = new List<float>();
        for (var y = (int)cy - toRing - 1; y <= (int)cy + toRing + 1; y++)
        {
            for (var x = (int)cx - toRing - 1; x <= (int)cx + toRing + 1; x++)
            {
                var k = (int)Math.Round(Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))));
                if (k >= ring && k <= toRing)
                {
                    values.Add(plane[(y * size) + x]);
                }
            }
        }
        values.Sort();
        return values[values.Count / 2];
    }

    private static FittedStar Row(float x, float y, bool saturated, params float[] amplitudes)
        => new FittedStar(x, y, 100, amplitudes[0], 1f, 0.1f, 0.01f, StarFitOutcome.Subtracted, saturated, false, float.NaN, float.NaN, false, float.NaN,
            StarFitModel.Moffat, [.. amplitudes]);

    // A two-channel master: one saturated star, clipped in its first channel (amplitude 5 under a ceiling of 0.9) and whole in
    // its second (amplitude 0.3), among unsaturated catalogue rows on a flat sky.
    private static (InjectionPopulation Population, float[][] MasterPlanes) MeasuredPopulation()
    {
        const int size = 256;
        var catalogue = ImmutableArray.CreateBuilder<FittedStar>();
        var rng = new Random(11);
        while (catalogue.Count < 20)
        {
            var (x, y) = ((float)(10 + (rng.NextDouble() * (size - 20))), (float)(10 + (rng.NextDouble() * (size - 20))));
            if (Math.Abs(x - StarX) > 50 || Math.Abs(y - StarY) > 50)
            {
                var amplitude = (float)(0.01 + (rng.NextDouble() * 0.2));
                catalogue.Add(Row(x, y, saturated: false, amplitude, amplitude));
            }
        }
        catalogue.Add(Row((float)StarX, (float)StarY, saturated: true, 5f, 0.3f));
        float[][,] planes = [StarPlane(size, 5.0), StarPlane(size, 0.3)];
        static float[,] SkyPlane()
        {
            var plate = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    plate[y, x] = (float)Sky;
                }
            }
            return plate;
        }
        var master = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var starless = new Image([SkyPlane(), SkyPlane()], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var population = InjectionPopulation.Build(catalogue.ToImmutable(), master, starless, 1.0, [(2.5, 3.0), (2.5, 3.0)], null, measuredSaturated: true);
        return (population, [Flat(planes[0]), Flat(planes[1])]);
    }

    [Fact]
    public void AChannelTheStarNeverClippedTakesNoClipLevelAndKeepsItsOwnAmplitude()
    {
        const int size = 256;
        float[][] planes = [Flat(StarPlane(size, 5.0)), Flat(StarPlane(size, 0.3))];
        ImmutableArray<double> cores = [planes[0].Max(), planes[1].Max()];
        cores[0].ShouldBe(Ceiling, 1e-6, "the first channel's top is the ceiling");
        cores[1].ShouldBeLessThan(0.5, "the second channel's top is the star's own peak");

        var measured = InjectionPopulation.MeasureSaturated(planes, size, size, StarX, StarY, 1.0, [Truth, Truth], cores);
        measured.ShouldNotBeNull();
        var (amplitudes, clips, _) = measured.Value;
        clips[0].ShouldBe(Ceiling, 1e-6, "clipped: its wings extrapolate well past its top");
        double.IsPositiveInfinity(clips[1]).ShouldBeTrue("a core that is the star's own peak is no clip level: as one it flat-tops the channel");
        amplitudes[0].ShouldBe(5.0, 5.0 * 0.15);
        amplitudes[1].ShouldBe(0.3, 0.3 * 0.15, "not held up to a multiple of a clip it never had");
    }

    [Fact]
    public void AMeasuredStarIsDrawnOnTheProfileItWasMeasuredAgainstWhateverTheCellAsksOfItsStars()
    {
        const int size = 256;
        var (population, masterPlanes) = MeasuredPopulation();
        population.SaturatedPool.ShouldBe(1);
        // A held beta of 1.5 (R3i's lever) on every star of the cell.
        var plan = population.Plan(0, 0, size, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 1.0, new Random(8), starBeta: new StarBeta(1.5));
        plan.SaturatedFallback.ShouldBeFalse();
        var saturated = plan.Stars.Single(static s => s.Saturated);
        plan.Stars.Where(static s => !s.Saturated).ShouldAllBe(static s => s.Profiles.All(static p => p.Beta == 1.5), "the cell's other stars take the held beta");
        saturated.Profiles.ShouldAllBe(static p => p.Beta == 3.0 && p.AxisRatio == 1.0 && !p.Outer.IsNone,
            "the measured star keeps the round profile its halo is the residual against");
        saturated.ClipLevels[0].ShouldBe(Ceiling, 1e-6);
        double.IsPositiveInfinity(saturated.ClipLevels[1]).ShouldBeTrue();

        // Rendered on a flat plate where its source stood, it carries its source's light ring by ring, in both channels. Each is
        // read as the measurement reads it: the ring's median less the median 32 to 36 px out.
        float[][] plate = [new float[size * size], new float[size * size]];
        Array.Fill(plate[0], (float)Sky);
        Array.Fill(plate[1], (float)Sky);
        var render = StarInjection.Render(plate, size, size, null, [saturated with { X = StarX, Y = StarY }], [1e-7, 1e-7], new Random(3));
        for (var c = 0; c < 2; c++)
        {
            var sourceSky = RingMedian(masterPlanes[c], size, StarX, StarY, InjectionPopulation.MeasuredSkyFromPx, InjectionPopulation.MeasuredSkyToPx);
            foreach (var ring in new[] { 4, 8, 12, 20 })
            {
                var source = RingMedian(masterPlanes[c], size, StarX, StarY, ring) - sourceSky;
                var drawn = RingMedian(render.Planes[c], size, StarX, StarY, ring) - Sky;
                drawn.ShouldBe(source, source * 0.2, $"channel {c} at {ring} px");
            }
        }
    }

    [Fact]
    public void AStarTheMeasurementCannotReachIsDrawnFromItsCataloguedEntryWithTheCellsProfiles()
    {
        const int size = 256;
        // Saturated, 20 px from the frame's edge: its stamp leaves the frame, so it is not measured.
        var catalogue = ImmutableArray.CreateBuilder<FittedStar>();
        for (var i = 0; i < 10; i++)
        {
            catalogue.Add(Row(40 + (i * 20), 200, saturated: false, 0.1f));
        }
        catalogue.Add(Row(128, 20, saturated: true, 3f));
        var masterPlane = new float[size, size];
        var platePlane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                masterPlane[y, x] = (float)Sky;
                platePlane[y, x] = (float)Sky;
            }
        }
        masterPlane[20, 128] = (float)Ceiling;
        var master = new Image([masterPlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var plate = new Image([platePlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var population = InjectionPopulation.Build(catalogue.ToImmutable(), master, plate, 1.0, [(2.5, 3.0)], null, measuredSaturated: true);
        population.SaturatedPool.ShouldBe(1);
        var plan = population.Plan(0, 0, size, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 1.0, new Random(8), starBeta: new StarBeta(1.5));
        var saturated = plan.Stars.Single(static s => s.Saturated);
        saturated.Amplitudes[0].ShouldBe(3.0, 1e-6, "the catalogued amplitude");
        saturated.ClipLevels[0].ShouldBe(Ceiling, 1e-6);
        saturated.Profiles[0].Beta.ShouldBe(1.5, "with no measurement of its own it is a star of the cell");
        saturated.Profiles[0].Outer.IsNone.ShouldBeTrue();
    }

    [Fact]
    public void AtItsOwnShareASiteIsDrawnSaturatedAsOftenAsTheMastersSitesAre()
    {
        const int size = 256;
        // 20 unsaturated sites and 5 saturated ones, each on a core of 0.9: a share of 0.2.
        var catalogue = ImmutableArray.CreateBuilder<FittedStar>();
        var masterPlane = new float[size, size];
        var platePlane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                masterPlane[y, x] = (float)Sky;
                platePlane[y, x] = (float)Sky;
            }
        }
        for (var i = 0; i < 25; i++)
        {
            var (x, y) = (30 + ((i % 5) * 45), 30 + ((i / 5) * 45));
            var saturated = i % 5 == 0;
            catalogue.Add(Row(x, y, saturated, saturated ? 3f : 0.1f));
            if (saturated)
            {
                masterPlane[y, x] = (float)Ceiling;
            }
        }
        var master = new Image([masterPlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var plate = new Image([platePlane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var population = InjectionPopulation.Build(catalogue.ToImmutable(), master, plate, 1.0, [(2.5, 3.0)], null);
        population.SaturatedShare.ShouldBe(0.2, 1e-12);
        population.SaturatedPool.ShouldBe(5);

        var (drawn, clipped, without) = (0, 0, 0);
        for (var seed = 0; seed < 40; seed++)
        {
            var plan = population.Plan(0, 0, size, 0, InjectionPlacement.AtSite, StarProfileFamily.Moffat, 0.0, new Random(seed), saturatedAtOwnShare: true);
            plan.SaturatedFallback.ShouldBeFalse();
            drawn += plan.Stars.Length;
            clipped += plan.Stars.Count(static s => s.Saturated);
            plan.Stars.Where(static s => s.Saturated).ShouldAllBe(static s => s.Amplitudes[0] == 3.0 && s.ClipLevels[0] > 0.89 && s.ClipLevels[0] < 0.91);
            without += population.Plan(0, 0, size, 0, InjectionPlacement.AtSite, StarProfileFamily.Moffat, 0.0, new Random(seed)).Stars.Count(static s => s.Saturated);
        }
        drawn.ShouldBe(40 * 25);
        without.ShouldBe(0, "without the option no site is drawn saturated");
        // 1000 draws at 0.2: 200, with a standard deviation of 12.6.
        clipped.ShouldBeInRange(150, 250);
    }

    [Fact]
    public void AFlipIsTheSameMaster() => BrightHalo.KeyOf("a/b|cam|obj|filt|flip=a").ShouldBe("a/b|cam|obj|filt");

    [Fact]
    public async Task TheFileIsReadPerSessionAndClass()
    {
        var path = Path.Combine(_temp.Create("bright-halos").FullName, "bright-halos.json");
        await File.WriteAllTextAsync(path, """
            {"a|cam|obj|f": {"bright": [[0, 0, 0.5], [0, 0, 0.25]], "saturated": [[0, 0, 1], [0, 0, 2]]},
             "b|cam|obj|f|flip=b": {"saturated": [[0, 1]]}}
            """, TestContext.Current.CancellationToken);
        var halos = await BrightHalo.ReadAsync(path, TestContext.Current.CancellationToken);
        halos.Count.ShouldBe(2);
        halos["a|cam|obj|f"].Bright.Length.ShouldBe(2);
        halos["a|cam|obj|f"].Saturated[1][2].ShouldBe(2f);
        halos["b|cam|obj|f"].Bright.IsDefaultOrEmpty.ShouldBeTrue();
        halos["b|cam|obj|f"].For(float.PositiveInfinity, 1)[0].AtRadius(1).ShouldBe(1.0, 1e-7);
    }
}
