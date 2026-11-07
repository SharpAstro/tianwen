using Shouldly;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        var measured = InjectionPopulation.MeasureSaturated([plane], size, size, cx, cy, 1.0, [profile], [clip]);
        measured.ShouldNotBeNull();
        var (amplitudes, halo) = measured.Value;
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
        InjectionPopulation.MeasureSaturated([plane], size, size, 10, 50, 1.0, [profile], [clip]).ShouldBeNull();
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
