using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R7a's ghost (docs/plans/planetary-restoration.md): J1 against its tables, the distance outside an object, and a ghost of known
/// strength, offset and defocus fitted back off a disk beside a glow, then a flare told from it by the residual.
/// </summary>
public class PlanetaryGhostTests
{
    // Small, so a fit's every Fourier transform is 256 on a side: the model is rebuilt for each step of the fit.
    private const int Size = 128;

    // A drawn disk has no blurred limb below the source's threshold, so these fit from 3 px out, nearer than a real stack's default.
    private const double Margin = 3;

    [Theory]
    [InlineData(0.5, 0.2422684577)]
    [InlineData(1.0, 0.4400505857)]
    [InlineData(2.9, 0.3754274818)]
    [InlineData(5.0, -0.3275791376)]
    [InlineData(10.0, 0.0434727462)]
    [InlineData(-1.0, -0.4400505857)]
    public void BesselJ1MatchesItsTables(double x, double expected) => PlanetaryGhost.BesselJ1(x).ShouldBe(expected, 1e-7);

    [Fact]
    public void TheDistanceOutsideASquareIsItsChamferDistance()
    {
        const int size = 21;
        var mask = new bool[size * size];
        for (var y = 8; y <= 12; y++)
        {
            for (var x = 8; x <= 12; x++)
            {
                mask[(y * size) + x] = true;
            }
        }
        var d = PlanetaryGhost.DistanceOutside(mask, size, size);
        d[(10 * size) + 10].ShouldBe(0);
        d[(10 * size) + 15].ShouldBe(3, 1e-5);
        d[(15 * size) + 15].ShouldBe(4, 1e-5);
    }

    // A disk darkened to its limb, 8 x 8 cells to a pixel, of radius 20 in the middle of the plane.
    private static float[] Disk()
    {
        var plane = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                double sum = 0;
                for (var v = 0; v < 8; v++)
                {
                    for (var u = 0; u < 8; u++)
                    {
                        var (dx, dy) = (x - 63.5 + ((u + 0.5) / 8) - 0.5, y - 63.5 + ((v + 0.5) / 8) - 0.5);
                        var r = Math.Sqrt((dx * dx) + (dy * dy)) / 20;
                        sum += r < 1 ? Math.Pow(Math.Sqrt(1 - (r * r)), 0.9) : 0;
                    }
                }
                plane[(y * Size) + x] = (float)(sum / 64);
            }
        }
        return plane;
    }

    [Fact]
    public void AGhostOfKnownStrengthOffsetAndDefocusIsFittedBack()
    {
        var disk = Disk();
        var source = new PlanetaryGhost.Source(disk, Size, Size);
        var ghost = source.Ghost(0.02, 5, -3, 12);
        var glow = source.Glow(0.01, 2.5, 1);
        var random = new Random(3);
        var plane = disk.Select((v, i) => v + ghost[i] + glow[i] + (float)(0.0005 * Normal(random))).ToArray();

        var fit = PlanetaryGhost.FitGhost(plane, new PlanetaryGhost.Source(plane, Size, Size, Margin));
        TestContext.Current.TestOutputHelper?.WriteLine($"strength {fit.Strength:0.0000}, shift ({fit.ShiftX:0.00}, {fit.ShiftY:0.00}), radius {fit.Radius:0.00}, axis ratio {fit.AxisRatio:0.00} at {fit.AngleDeg:0.0}, rms {fit.Rms:0.00000}");
        fit.Strength.ShouldBe(0.02, 0.002);
        fit.ShiftX.ShouldBe(5, 1);
        fit.ShiftY.ShouldBe(-3, 1);
        fit.Radius.ShouldBe(12, 1.2);
    }

    // An unmoved copy's strength is fixed only together with its shape beside a free round glow (its round part is the glow's to
    // take), so what comes back is its shell: the non-round part, band by band, and its axis.
    [Fact]
    public void AnEllipticalGhostsShellIsFittedBack()
    {
        var disk = Disk();
        var source = new PlanetaryGhost.Source(disk, Size, Size);
        var ghost = source.Ghost(0.05, 0, 0, 16, 0.6, 120);
        var glow = source.Glow(0.01, 2.5, 1);
        var random = new Random(4);
        var plane = disk.Select((v, i) => v + ghost[i] + glow[i] + (float)(0.0005 * Normal(random))).ToArray();

        var fitted = new PlanetaryGhost.Source(plane, Size, Size, Margin);
        var fit = PlanetaryGhost.FitGhost(plane, fitted);
        (double, double)[] bands = [(3, 8), (8, 14)];
        var injected = PlanetaryGhost.Quadrupoles(ghost, fitted, bands);
        var recovered = PlanetaryGhost.Quadrupoles(fitted.Ghost(fit.Strength, fit.ShiftX, fit.ShiftY, fit.Radius, fit.AxisRatio, fit.AngleDeg, fit.Separation), fitted, bands);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("; ", injected.Zip(recovered, (i, r) => $"{i.From}-{i.To}: {i.Amplitude:0.00000}@{i.AxisDeg:0} came back {r.Amplitude:0.00000}@{r.AxisDeg:0}")));
        for (var k = 0; k < bands.Length; k++)
        {
            recovered[k].Amplitude.ShouldBe(injected[k].Amplitude, 0.1 * injected[k].Amplitude);
            recovered[k].AxisDeg.ShouldBe(injected[k].AxisDeg, 3);
        }
    }

    [Fact]
    public void TakingOutAnEllipticalCopysNonRoundPartLeavesTheHaloRound()
    {
        var disk = Disk();
        var source = new PlanetaryGhost.Source(disk, Size, Size, Margin);
        var ghost = source.Ghost(0.05, 0, 0, 14, 0.5, 30);
        var glow = source.Glow(0.01, 2.5, 1);
        var random = new Random(6);
        var plane = disk.Select((v, i) => v + ghost[i] + glow[i] + (float)(0.0002 * Normal(random))).ToArray();

        var fitted = new PlanetaryGhost.Source(plane, Size, Size, Margin);
        var fit = PlanetaryGhost.FitGhost(plane, fitted);
        var shell = PlanetaryGhost.Shell(fitted.Ghost(fit.Strength, fit.ShiftX, fit.ShiftY, fit.Radius, fit.AxisRatio, fit.AngleDeg, fit.Separation), fitted);
        (double, double)[] bands = [(3, 8), (8, 14)];
        var before = PlanetaryGhost.Quadrupoles(plane, fitted, bands);
        var after = PlanetaryGhost.Quadrupoles([.. plane.Select((v, i) => v - shell[i])], fitted, bands);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("; ", before.Zip(after, (b, a) => $"{b.From}-{b.To}: {b.Amplitude:0.00000}@{b.AxisDeg:0} to {a.Amplitude:0.00000}")));
        before[0].AxisDeg.ShouldBe(30, 3);
        for (var k = 0; k < bands.Length; k++)
        {
            after[k].Amplitude.ShouldBeLessThan(0.1 * before[k].Amplitude);
        }
    }

    [Fact]
    public void AFlareFitsAsComaBetterThanAsAGhost()
    {
        var disk = Disk();
        var source = new PlanetaryGhost.Source(disk, Size, Size);
        var flare = source.Flare(0.05, 14, 30);
        var glow = source.Glow(0.01, 2.5, 1);
        var random = new Random(5);
        // Quiet, so the residual is the model's misfit: a two-part copy, moved, imitates a flare to within noise five times this.
        var plane = disk.Select((v, i) => v + flare[i] + glow[i] + (float)(0.0001 * Normal(random))).ToArray();

        var fitted = new PlanetaryGhost.Source(plane, Size, Size, Margin);
        var (asGhost, asComa) = (PlanetaryGhost.FitGhost(plane, fitted), PlanetaryGhost.FitComa(plane, fitted));
        TestContext.Current.TestOutputHelper?.WriteLine($"as a ghost rms {asGhost.Rms:0.000000}; as coma rms {asComa.Rms:0.000000}, length {asComa.Length:0.0} at {asComa.AngleDeg:0.0} degrees");
        asComa.Rms.ShouldBeLessThan(asGhost.Rms);
    }

    private static double Normal(Random random) => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
