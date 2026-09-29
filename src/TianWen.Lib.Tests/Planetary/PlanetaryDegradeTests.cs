using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The synthetic capture (docs/plans/planetary-restoration.md, R2 part 2, #1050): the screen that evolves between frames, the
/// camera's terms solved from what the frames recorded, the frames themselves, and the statistics that compare a synthetic
/// capture with a real one, each pinned against a quantity the test knows independently.
/// </summary>
public class PlanetaryDegradeTests
{
    private static readonly DateTimeOffset Night = new DateTimeOffset(2022, 9, 3, 12, 10, 0, TimeSpan.Zero);

    [Fact]
    public void AnEvolvingScreenKeepsKolmogorovsStructureFunctionAsItMoves()
    {
        const int n = 256;
        const double spacing = 0.01;
        const double r0 = 0.1;
        var screen = new EvolvingPhaseScreen(n, spacing, r0, new Random(7));
        var phase = new double[n * n];
        int[] separations = [4, 8];
        var sums = new double[separations.Length];
        var counts = new long[separations.Length];
        for (var step = 0; step < 20; step++)
        {
            // Half the air renewed a step, the rest carried 30 cm: well mixed, so the steps are close to independent draws.
            screen.Step(10, 5, 0.03, 0.5);
            screen.Fill(phase);
            for (var s = 0; s < separations.Length; s++)
            {
                var d = separations[s];
                for (var y = 0; y < n - d; y++)
                {
                    for (var x = 0; x < n - d; x++)
                    {
                        var p = phase[(y * n) + x];
                        var dx = phase[(y * n) + x + d] - p;
                        var dy = phase[((y + d) * n) + x] - p;
                        sums[s] += (dx * dx) + (dy * dy);
                        counts[s] += 2;
                    }
                }
            }
        }
        for (var s = 0; s < separations.Length; s++)
        {
            var measured = sums[s] / counts[s];
            var expected = 6.88 * Math.Pow(separations[s] * spacing / r0, 5.0 / 3.0);
            TestContext.Current.TestOutputHelper?.WriteLine($"r = {separations[s] * spacing:0.00} m: D = {measured:0.000} rad^2, Kolmogorov {expected:0.000}");
            measured.ShouldBe(expected, expected * 0.15);
        }
    }

    [Fact]
    public void WithNothingRenewedTheScreenIsTheSameAirMovedOn()
    {
        const int n = 64;
        const double spacing = 0.01;
        var screen = new EvolvingPhaseScreen(n, spacing, 0.1, new Random(3));
        var before = new double[n * n];
        var after = new double[n * n];
        screen.Fill(before);
        // Three samples in x and one in y, in one step, keeping everything.
        screen.Step(3 * spacing, spacing, 1, 1);
        screen.Fill(after);

        // Where nothing wrapped in, the new screen is the old one moved, up to the mean each is made zero about.
        double sum = 0, sumSquares = 0;
        var count = 0;
        for (var y = 1; y < n; y++)
        {
            for (var x = 3; x < n; x++)
            {
                var d = after[(y * n) + x] - before[((y - 1) * n) + x - 3];
                sum += d;
                sumSquares += d * d;
                count++;
            }
        }
        var mean = sum / count;
        var spread = Math.Sqrt(Math.Max(0, (sumSquares / count) - (mean * mean)));
        TestContext.Current.TestOutputHelper?.WriteLine($"moved screen against the old one: spread {spread:E3} rad, the mean's shift {mean:+0.000;-0.000} rad");
        // Up to the transforms' rounding: 2e-7 rad measured, on a screen of tens of radians.
        spread.ShouldBeLessThan(1e-5);
    }

    [Theory]
    [InlineData(16.99, 0.15)]
    [InlineData(16.99, 0.3)]
    [InlineData(40.5, 0.6)]
    [InlineData(40.2, 1.5)]
    public void TheReadNoiseIsRecoveredThroughTheRounding(double offset, double sigma)
    {
        // What a camera records: the offset plus the noise, rounded to whole ADU. Its spread is what a sky measures.
        var random = new Random(11);
        double sum = 0, sumSquares = 0;
        const int draws = 400_000;
        for (var i = 0; i < draws; i++)
        {
            var v = Math.Round(offset + (sigma * PhaseScreen.Gaussian(random)));
            sum += v;
            sumSquares += v * v;
        }
        var mean = sum / draws;
        var recorded = Math.Sqrt((sumSquares / draws) - (mean * mean));

        var solved = PlanetaryDegrade.ReadNoiseFor(offset, recorded);

        TestContext.Current.TestOutputHelper?.WriteLine($"offset {offset}, noise {sigma}: recorded {recorded:0.0000}, solved {solved:0.0000}");
        solved.ShouldBe(sigma, sigma * 0.03);
    }

    [Fact]
    public async Task ASyntheticFramesDiskLandsWhereItsShiftPutsItAtTheLevelAsked()
    {
        ImmutableArray<double> shiftX = [0, 3.4, -2.25];
        ImmutableArray<double> shiftY = [0, -1.7, 0.5];
        var frames = await MakeAsync(shiftX, shiftY, size: 96, radius: 20, r0M: 0.1);

        var (x0, y0, _) = CentroidAndLevel(frames[0], 96, 20);
        for (var i = 0; i < frames.Length; i++)
        {
            var (x, y, level) = CentroidAndLevel(frames[i], 96, 20);
            TestContext.Current.TestOutputHelper?.WriteLine($"frame {i}: centroid moved {x - x0:+0.000;-0.000}, {y - y0:+0.000;-0.000} (asked {shiftX[i]:+0.000;-0.000}, {shiftY[i]:+0.000;-0.000}); disk level {level:0.0}");
            (x - x0).ShouldBe(shiftX[i], 0.02);
            (y - y0).ShouldBe(shiftY[i], 0.02);
            level.ShouldBe(DiskLevel, DiskLevel * 0.03);
        }
    }

    [Fact]
    public async Task TheStatisticsSplitAKnownMotionIntoTheMountsAndTheSeeings()
    {
        // A drift of 3 px/s under a seeing that shakes the disk by a known RMS, at 200 frames a second for four seconds, four
        // of the running mean's windows.
        const int frames = 800;
        const double fps = 200;
        var random = new Random(5);
        var drawnX = ImmutableArray.CreateBuilder<double>(frames);
        var drawnY = ImmutableArray.CreateBuilder<double>(frames);
        double seeingSquares = 0;
        for (var i = 0; i < frames; i++)
        {
            var (sx, sy) = (0.6 * PhaseScreen.Gaussian(random), 0.6 * PhaseScreen.Gaussian(random));
            seeingSquares += (sx * sx) + (sy * sy);
            drawnX.Add((3 * i / fps) + sx);
            drawnY.Add(sy);
        }
        var (shiftX, shiftY) = (drawnX.MoveToImmutable(), drawnY.MoveToImmutable());
        var made = await MakeAsync(shiftX, shiftY, size: 64, radius: 12, r0M: 0.3);
        var planes = new float[frames][,];
        var times = new DateTimeOffset[frames];
        for (var i = 0; i < frames; i++)
        {
            planes[i] = new float[64, 64];
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 64; x++)
                {
                    planes[i][y, x] = made[i][(y * 64) + x] / (float)FullScale;
                }
            }
            times[i] = Night + TimeSpan.FromSeconds(i / fps);
        }

        using var stream = new InMemoryFrameStream(planes, times);
        var statistics = await PlanetaryCaptureStatistics.MeasureAsync(stream, new CaptureStatisticsOptions(0.935) { FullScaleAdu = FullScale, Pairs = 50 },
            cancellationToken: TestContext.Current.CancellationToken);

        statistics.ShouldNotBeNull();
        var s = statistics;
        // The shifts are measured against the sharpest frame, so it is their differences that must come back.
        double worst = 0, squares = 0;
        for (var i = 0; i < frames; i++)
        {
            var ex = s.ShiftX[i] - s.ShiftX[0] - (shiftX[i] - shiftX[0]);
            var ey = s.ShiftY[i] - s.ShiftY[0] - (shiftY[i] - shiftY[0]);
            worst = Math.Max(worst, Math.Max(Math.Abs(ex), Math.Abs(ey)));
            squares += (ex * ex) + (ey * ey);
        }
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"shift error {Math.Sqrt(squares / (2 * frames)):0.000} px RMS, worst {worst:0.000} px; seeing {s.SeeingRms:0.000} px RMS (drawn {Math.Sqrt(seeingSquares / (2 * frames)):0.000}); mount {s.MountRate:0.000} px/s (drawn 3); fps {s.FramesPerSecond:0.0}");
        // The global aligner's own error on a disk 12 px in radius (0.127 px RMS measured), which the seeing's RMS then carries
        // in quadrature: the statistics add nothing of their own.
        Math.Sqrt(squares / (2 * frames)).ShouldBeLessThan(0.2);
        s.MountRate.ShouldBe(3, 0.15);
        s.FramesPerSecond.ShouldBe(fps, 0.5);
        // The running mean over a second keeps a little of the seeing, 1/sqrt(200) of its RMS, so it reads a hair low.
        s.SeeingRms.ShouldBe(Math.Sqrt(seeingSquares / (2 * frames)), 0.05);
    }

    private const double FullScale = 65535;
    private const double DiskLevel = 20000;

    // Frames of a banded Jupiter (the aligners want texture, as a real one has), bright and nearly noiseless (a large gain, no
    // read noise, 16 bits), under seeing of `r0M`.
    private static async Task<ushort[][]> MakeAsync(ImmutableArray<double> shiftX, ImmutableArray<double> shiftY, int size, double radius, double r0M)
    {
        var values = new float[360 * 180];
        for (var row = 0; row < 180; row++)
        {
            var latitude = (89.5 - row) * Math.PI / 180;
            for (var column = 0; column < 360; column++)
            {
                values[(row * 360) + column] = (float)(1 - (0.3 * Math.Pow(Math.Sin(4 * latitude), 2)) - (0.2 * Math.Exp(-Math.Pow((column - 90) / 15.0, 2) - Math.Pow((row - 112) / 6.0, 2))));
            }
        }
        var map = new PlanetMap(values, 360, 180);
        var times = ImmutableArray.CreateBuilder<DateTimeOffset>(shiftX.Length);
        for (var i = 0; i < shiftX.Length; i++)
        {
            times.Add(Night + TimeSpan.FromMilliseconds(5 * i));
        }
        var options = new DegradeOptions(new Pupil(0.254, ObstructionRatio: 0.23, Vanes: 4, VaneWidthM: 0.001), 650e-9)
        {
            R0M = r0M,
            FullScaleAdu = FullScale,
            OffsetAdu = 100,
            ReadNoiseAdu = 0,
            ElectronsPerAdu = 1000,
            DiskLevelAdu = DiskLevel,
            ScreenSamples = 128,
        };
        var frames = new ushort[shiftX.Length][];
        var reference = new DiskPlacement((size / 2) - 0.3, (size / 2) + 0.2, radius, NorthAngleDeg: -80);
        await PlanetaryDegrade.MakeAsync(map, CatalogIndex.Jupiter, times.MoveToImmutable(), reference, 0.49, shiftX, shiftY, size, size, options,
            (index, samples) => frames[index] = samples, cancellationToken: TestContext.Current.CancellationToken);
        return frames;
    }

    // The disk's centroid over the offset, and its mean over the offset inside 0.8 equatorial radii of it: the level asked for.
    private static (double X, double Y, double Level) CentroidAndLevel(ushort[] frame, int size, double radius)
    {
        double sum = 0, sx = 0, sy = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var v = frame[(y * size) + x] - 100.0;
                sum += v;
                sx += v * x;
                sy += v * y;
            }
        }
        var (cx, cy) = (sx / sum, sy / sum);
        double inside = 0;
        var count = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if ((dx * dx) + (dy * dy) < 0.64 * radius * radius)
                {
                    inside += frame[(y * size) + x] - 100.0;
                    count++;
                }
            }
        }
        return (cx, cy, inside / count);
    }
}
