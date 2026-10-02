using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The metrics a planetary stack is judged by (docs/plans/planetary-restoration.md, R3), each pinned on a banded, limb-darkened
/// disk whose every change the test makes itself: a blur, noise, a shift, a sharpening.
/// </summary>
public class PlanetaryMetricsTests
{
    internal const int Size = 128;
    internal static readonly MetricDisk Disk = new MetricDisk(63.7, 64.2, 30);

    [Fact]
    public void AStackIsItsOwnTruthInEveryBand()
    {
        var truth = Blur(Banded(), 1.0);
        var fidelity = PlanetaryMetrics.Fidelity(truth, truth, Size, Size, Disk);
        fidelity.Length.ShouldBe(PlanetaryMetrics.Bands);
        foreach (var band in fidelity)
        {
            band.Transfer.ShouldBe(1, 1e-9);
            band.Error.ShouldBe(0, 1e-9);
        }
    }

    [Fact]
    public void ABlurLosesTheFinestBandsFirst()
    {
        // Blurred further, the stack keeps less of each band the finer it is, and leaves more error there.
        var truth = Blur(Banded(), 1.0);
        var fidelity = PlanetaryMetrics.Fidelity(Blur(truth, 1.5), truth, Size, Size, Disk);
        foreach (var band in fidelity)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"band {band.Band}: transfer {band.Transfer:0.000}, error {band.Error:0.000}");
        }
        for (var j = 1; j < fidelity.Length; j++)
        {
            fidelity[j - 1].Transfer.ShouldBeLessThan(fidelity[j].Transfer);
        }
        fidelity[0].Transfer.ShouldBeLessThan(0.5);
        fidelity[^1].Transfer.ShouldBeGreaterThan(0.8);
    }

    [Fact]
    public void AShiftMovesTheDiskByExactlyThatAndBack()
    {
        // A band-limited disk moved a fraction of a pixel and back is itself, which interpolating would not leave it: it would
        // blur the finest band on each move.
        var truth = Blur(Banded(), 1.5);
        var there = PlanetaryMetrics.Shift(truth, Size, Size, 0.3, -0.7);
        var back = PlanetaryMetrics.Shift(there, Size, Size, -0.3, 0.7);
        var worst = truth.Select((v, i) => (double)Math.Abs(v - back[i])).Max();
        var (cx, cy) = Centroid(truth);
        var (sx, sy) = Centroid(there);
        TestContext.Current.TestOutputHelper?.WriteLine($"moved {sx - cx:+0.0000;-0.0000}, {sy - cy:+0.0000;-0.0000}; back to within {worst:E2}");
        (sx - cx).ShouldBe(0.3, 1e-3);
        (sy - cy).ShouldBe(-0.7, 1e-3);
        worst.ShouldBeLessThan(1e-3);
    }

    [Fact]
    public void TwoHalvesAgreeWhereTheDetailIsAndNotWhereTheNoiseIs()
    {
        // The same disk under two draws of noise: the coarse bands, where the disk's detail is, agree; the finest, mostly noise,
        // does not. The same disk twice agrees in every band.
        var truth = Blur(Banded(), 1.0);
        var random = new Random(3);
        var (a, b) = (Noisy(truth, 0.05, random), Noisy(truth, 0.05, random));
        var agreement = PlanetaryMetrics.SplitHalf(a, b, Size, Size, Disk);
        foreach (var band in agreement)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"band {band.Band}: correlation {band.Correlation:0.000}, shared {band.Shared:E2}, noise {band.Noise:E2}");
        }
        agreement[0].Correlation.ShouldBeLessThan(0.5);
        agreement[3].Correlation.ShouldBeGreaterThan(0.9);
        agreement[0].Noise.ShouldBeGreaterThan(agreement[3].Noise);
        PlanetaryMetrics.SplitHalf(truth, truth, Size, Size, Disk).ShouldAllBe(band => Math.Abs(band.Correlation - 1) < 1e-9);
    }

    [Fact]
    public void ASharpenedLimbUndershootsTheSkyAndItsProfileLeavesTheTruths()
    {
        // An unsharp mask rings outside a bright edge: its profile dips below the sky, which a plain blur never does.
        var truth = Blur(Banded(), 1.0);
        var soft = Blur(truth, 1.0);
        var sharpened = new float[soft.Length];
        var smooth = Blur(soft, 2.0);
        for (var i = 0; i < soft.Length; i++)
        {
            sharpened[i] = soft[i] + (2 * (soft[i] - smooth[i]));
        }
        var random = new Random(5);
        var (plain, rung) = (Noisy(soft, 0.01, random), Noisy(sharpened, 0.01, random));
        var (plainUnder, rungUnder) = (PlanetaryMetrics.LimbUndershoot(plain, Size, Size, Disk), PlanetaryMetrics.LimbUndershoot(rung, Size, Size, Disk));
        var (plainError, rungError) = (PlanetaryMetrics.LimbProfileError(plain, truth, Size, Size, Disk), PlanetaryMetrics.LimbProfileError(rung, truth, Size, Size, Disk));
        TestContext.Current.TestOutputHelper?.WriteLine($"undershoot: plain {plainUnder:0.0000}, sharpened {rungUnder:0.0000} of the disk; limb profile error: plain {plainError:0.0000}, sharpened {rungError:0.0000}");
        // The ring is a few percent of the disk deep; the blur's profile only touches the noise.
        rungUnder.ShouldBeGreaterThan(0.02);
        plainUnder.ShouldBeLessThan(0.01);
        PlanetaryMetrics.LimbProfileError(truth, truth, Size, Size, Disk).ShouldBe(0, 1e-12);
        rungError.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ARingAboveTheSkyReboundsOutsideTheLimbWhereABlurOnlyFalls()
    {
        // A blurred disk's profile only falls outside its limb, so it never climbs back (#1168). A faint ring laid 0.22 radii
        // out climbs back by about its own height, above the sky, where the undershoot sees nothing.
        var soft = Blur(Blur(Banded(), 1.0), 1.0);
        var ringed = new float[soft.Length];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var d = (Disk.RadiiAt(x, y) - 1.22) * Disk.Radius;
                ringed[(y * Size) + x] = soft[(y * Size) + x] + (float)(0.02 * Math.Exp(-(d * d) / 2));
            }
        }
        var (softRebound, ringedRebound) = (PlanetaryMetrics.LimbRebound(soft, Size, Size, Disk), PlanetaryMetrics.LimbRebound(ringed, Size, Size, Disk));
        var ringedUnder = PlanetaryMetrics.LimbUndershoot(ringed, Size, Size, Disk);
        TestContext.Current.TestOutputHelper?.WriteLine($"rebound: blurred {softRebound:0.0000}, with a ring of 0.02 {ringedRebound:0.0000} (its undershoot {ringedUnder:0.0000})");
        softRebound.ShouldBe(0, 1e-6);
        ringedRebound.ShouldBe(0.02, 0.005);
        ringedUnder.ShouldBeLessThan(0.001);
    }

    [Fact]
    public void PowerPastTheCutoffIsWhatNoiseAddsAndNothingPastNyquist()
    {
        // A blurred disk holds little power past 0.35 cycles a pixel; noise adds there. Past the grid's corner there is nothing
        // to measure.
        var truth = Blur(Banded(), 2.0);
        var noisy = Noisy(truth, 0.05, new Random(7));
        var (clean, dirty) = (PlanetaryMetrics.PowerAbove(truth, Size, Size, Disk, 0.35), PlanetaryMetrics.PowerAbove(noisy, Size, Size, Disk, 0.35));
        TestContext.Current.TestOutputHelper?.WriteLine($"power past 0.35 cycles a pixel: {clean:E2} clean, {dirty:E2} noisy");
        dirty.ShouldBeGreaterThan(10 * clean);
        double.IsNaN(PlanetaryMetrics.PowerAbove(truth, Size, Size, Disk, 1.1)).ShouldBeTrue();
    }

    [Fact]
    public void NormalisingPutsTheSkyAtZeroAndTheDiskAtOne()
    {
        var plane = Blur(Banded(), 1.0).Select(v => (float)((v * 37.5) + 16.8)).ToArray();
        var normalised = PlanetaryMetrics.Normalise(plane, Size, Size, Disk);
        double sky = 0, disk = 0;
        int skyCount = 0, diskCount = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r = Disk.RadiiAt(x, y);
                if (r >= 2.5)
                {
                    sky += normalised[(y * Size) + x];
                    skyCount++;
                }
                else if (r < 0.8)
                {
                    disk += normalised[(y * Size) + x];
                    diskCount++;
                }
            }
        }
        (sky / skyCount).ShouldBe(0, 1e-4);
        (disk / diskCount).ShouldBe(1, 1e-4);
    }

    // A disk darkened to its limb (mu^0.8), crossed by belts at two angles, on a sky of zero.
    internal static float[] Banded()
    {
        var plane = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r = Disk.RadiiAt(x, y);
                if (r < 1)
                {
                    var mu = Math.Sqrt(1 - (r * r));
                    plane[(y * Size) + x] = (float)(Math.Pow(mu, 0.8) * (1 + (0.2 * Math.Sin(2 * Math.PI * y / 7)) + (0.1 * Math.Sin(2 * Math.PI * (x + y) / 11))));
                }
            }
        }
        return plane;
    }

    // A separable Gaussian blur of sigma pixels, the edges held.
    internal static float[] Blur(float[] plane, double sigma)
    {
        var reach = (int)Math.Ceiling(4 * sigma);
        var kernel = Enumerable.Range(-reach, (2 * reach) + 1).Select(k => Math.Exp(-k * k / (2 * sigma * sigma))).ToArray();
        var sum = kernel.Sum();
        var rows = new float[plane.Length];
        var result = new float[plane.Length];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                double v = 0;
                for (var k = -reach; k <= reach; k++)
                {
                    v += kernel[k + reach] * plane[(y * Size) + Math.Clamp(x + k, 0, Size - 1)];
                }
                rows[(y * Size) + x] = (float)(v / sum);
            }
        }
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                double v = 0;
                for (var k = -reach; k <= reach; k++)
                {
                    v += kernel[k + reach] * rows[(Math.Clamp(y + k, 0, Size - 1) * Size) + x];
                }
                result[(y * Size) + x] = (float)(v / sum);
            }
        }
        return result;
    }

    internal static float[] Noisy(float[] plane, double sigma, Random random)
    {
        return [.. plane.Select(v => (float)(v + (sigma * PhaseScreen.Gaussian(random))))];
    }

    private static (double X, double Y) Centroid(float[] plane)
    {
        double sum = 0, sx = 0, sy = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var v = plane[(y * Size) + x];
                sum += v;
                sx += v * x;
                sy += v * y;
            }
        }
        return (sx / sum, sy / sum);
    }
}
