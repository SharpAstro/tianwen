using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
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
    public void AnOuterScaleTakesFromTheStructureFunctionAsVonKarmanSays()
    {
        // Von Karman's structure function is Kolmogorov's times 1 - 1.485 (r / L0)^(1/3) + ... for r well inside L0 (Tokovinin
        // 2002): even a 4 cm separation loses 40 % to a 2 m outer scale, the first order says, and one of a metre, half of it,
        // saturates. Written first as "the small scales keep theirs", which the screen rightly refused.
        const int n = 256;
        const double spacing = 0.01;
        const double r0 = 0.1;
        double Structure(double outerScale, int d)
        {
            var screen = new EvolvingPhaseScreen(n, spacing, r0, new Random(9), outerScale);
            var phase = new double[n * n];
            double sum = 0;
            long count = 0;
            for (var step = 0; step < 20; step++)
            {
                screen.Step(0, 0, 1, 0);
                screen.Fill(phase);
                for (var y = 0; y < n - d; y++)
                {
                    for (var x = 0; x < n - d; x++)
                    {
                        var dx = phase[(y * n) + x + d] - phase[(y * n) + x];
                        sum += dx * dx;
                        count++;
                    }
                }
            }
            return sum / count;
        }

        var (smallKolmogorov, smallVonKarman) = (Structure(double.PositiveInfinity, 4), Structure(2, 4));
        var (largeKolmogorov, largeVonKarman) = (Structure(double.PositiveInfinity, 100), Structure(2, 100));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"4 cm: {smallVonKarman:0.000} against {smallKolmogorov:0.000} rad^2; 1 m: {largeVonKarman:0.0} against {largeKolmogorov:0.0} rad^2");
        (smallVonKarman / smallKolmogorov).ShouldBe(1 - (1.485 * Math.Cbrt(0.04 / 2)), 0.08);
        (largeVonKarman / largeKolmogorov).ShouldBeLessThan(0.3);
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
    [InlineData(16.80, 0.21)]
    [InlineData(16.99, 0.15)]
    [InlineData(16.99, 0.3)]
    [InlineData(40.5, 0.6)]
    [InlineData(40.2, 1.5)]
    [InlineData(1000.3, 12)]
    public void TheLevelAndNoiseAreRecoveredThroughTheRounding(double offset, double sigma)
    {
        // What a camera records: the offset plus the noise, rounded to whole ADU, counted by value as a sky's pixels are. The
        // first case is 2022-09-03's far sky, whose rounded mean (16.92) is not its level.
        var random = new Random(11);
        const int draws = 400_000;
        var first = (int)Math.Floor(offset - (8 * sigma)) - 1;
        var counts = new long[(int)Math.Ceiling(16 * sigma) + 3];
        double sum = 0;
        for (var i = 0; i < draws; i++)
        {
            var v = Math.Round(offset + (sigma * PhaseScreen.Gaussian(random)));
            counts[(int)v - first]++;
            sum += v;
        }

        var (level, noise) = PlanetaryDegrade.RoundedGaussianFit(counts, first);

        TestContext.Current.TestOutputHelper?.WriteLine($"offset {offset}, noise {sigma}: rounded mean {sum / draws:0.0000}; fitted level {level:0.0000}, noise {noise:0.0000}");
        level.ShouldBe(offset, Math.Max(0.01, 0.01 * sigma));
        noise.ShouldBe(sigma, sigma * 0.03);
    }

    [Fact]
    public void ASkysNoiseIsItsPixelsOwnWhereItsLevelSlopes()
    {
        // 2022-09-03's sky: a read noise of 0.17 ADU on a level that falls 0.2 ADU across the frame, 300 frames rounded to whole
        // ADU. Pooled, the slope widens the fitted noise; with a level of each pixel's own, it does not.
        const int pixels = 20_000, frames = 300, bins = 9, first = 13;
        var random = new Random(17);
        var counts = new short[pixels * bins];
        var pooled = new long[bins];
        for (var p = 0; p < pixels; p++)
        {
            var level = 16.65 + (0.2 * p / pixels);
            for (var f = 0; f < frames; f++)
            {
                var bin = (int)Math.Round(level + (0.17 * PhaseScreen.Gaussian(random))) - first;
                counts[(p * bins) + bin]++;
                pooled[bin]++;
            }
        }

        var (level1, noise1) = PlanetaryDegrade.RoundedGaussianFit(pooled, first);
        var (level2, noise2) = PlanetaryCaptureStatistics.SkyByPixels(counts, bins, first, [.. Enumerable.Range(0, pixels)]);

        TestContext.Current.TestOutputHelper?.WriteLine($"pooled: level {level1:0.000}, noise {noise1:0.000}; by pixels: level {level2:0.000}, noise {noise2:0.000}");
        noise2.ShouldBe(0.17, 0.01);
        level2.ShouldBe(16.75, 0.01);
        noise1.ShouldBeGreaterThan(noise2);
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

    [Fact(Timeout = 120_000)]
    public async Task AFrameShowsTheLevelMeasuredOnceTheBlursShareIsPutBack()
    {
        // A small disk under poor seeing and the telescope's scatter: a level is read inside 0.8 radii of a frame, which the blur and
        // the scatter have taken light from, so a twin's planet is the measured level times ShownLevelGain (S3, #1233).
        ImmutableArray<double> none = [0, 0, 0, 0, 0, 0, 0, 0];
        var asMeasured = await MakeAsync(none, none, size: 96, radius: 12, r0M: 0.05, scatter: 0.05);
        var putBack = await MakeAsync(none, none, size: 96, radius: 12, r0M: 0.05, scatter: 0.05, atShownLevel: true);

        var measuredLevel = asMeasured.Average(f => CentroidAndLevel(f, 96, 12).Level);
        var putBackLevel = putBack.Average(f => CentroidAndLevel(f, 96, 12).Level);
        TestContext.Current.TestOutputHelper?.WriteLine($"level asked {DiskLevel}: the render scaled to it shows {measuredLevel:0.0}, with the blur's share put back {putBackLevel:0.0}");
        measuredLevel.ShouldBeLessThan(DiskLevel * 0.95);
        putBackLevel.ShouldBe(DiskLevel, DiskLevel * 0.01);
    }

    [Fact]
    public async Task AKeptTiltMovesTheDiskAsItsRecordSays()
    {
        // No shift given: the seeing alone moves the disk, by its screen's tilt, and the record must say where it put it.
        ImmutableArray<double> none = [0, 0, 0, 0];
        var truths = ImmutableArray<SyntheticFrame>.Empty;
        var frames = await MakeAsync(none, none, size: 96, radius: 20, r0M: 0.05, keepTilt: true, made: t => truths = t);

        var (x0, y0, _) = CentroidAndLevel(frames[0], 96, 20);
        var moved = 0.0;
        for (var i = 1; i < frames.Length; i++)
        {
            var (x, y, _) = CentroidAndLevel(frames[i], 96, 20);
            var (ex, ey) = (truths[i].ShiftX - truths[0].ShiftX, truths[i].ShiftY - truths[0].ShiftY);
            TestContext.Current.TestOutputHelper?.WriteLine($"frame {i}: centroid moved {x - x0:+0.000;-0.000}, {y - y0:+0.000;-0.000}; recorded {ex:+0.000;-0.000}, {ey:+0.000;-0.000}");
            (x - x0).ShouldBe(ex, 0.03);
            (y - y0).ShouldBe(ey, 0.03);
            moved = Math.Max(moved, Math.Abs(ex) + Math.Abs(ey));
        }
        // At r0 5 cm on a 254 mm aperture the tilt moves the disk by pixels, so the check above is not of a disk standing still.
        moved.ShouldBeGreaterThan(0.2);
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
        var statistics = await PlanetaryCaptureStatistics.MeasureAsync(stream, new CaptureStatisticsOptions(new LimbFitOptions(0.935)) { FullScaleAdu = FullScale, Pairs = 50 },
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

        // The single frames' limb fits follow each disk where it was drawn, closer than the aligner does, and read the same
        // seeing from it.
        var fits = s.FrameLimbs.Where(f => f.Fit is not null).Select(f => (f.Frame, Fit: f.Fit.GetValueOrDefault())).ToArray();
        var (meanX, meanY) = (fits.Average(f => f.Fit.CenterX - shiftX[f.Frame]), fits.Average(f => f.Fit.CenterY - shiftY[f.Frame]));
        var limbError = Math.Sqrt(fits.Average(f => Math.Pow(f.Fit.CenterX - shiftX[f.Frame] - meanX, 2) + Math.Pow(f.Fit.CenterY - shiftY[f.Frame] - meanY, 2)) / 2);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{fits.Length} limb fits: {limbError:0.000} px RMS from where each disk was drawn; seeing by the limb {s.LimbSeeingRms:0.000} px RMS; the aligner off by {s.AlignerErrorRms:0.000} px; {s.LimbOutliers} left out");
        fits.Length.ShouldBe(frames / 4);
        limbError.ShouldBeLessThan(0.05);
        s.LimbOutliers.ShouldBe(0);
        s.LimbSeeingRms.ShouldBe(Math.Sqrt(seeingSquares / (2 * frames)), 0.05);

        // The far sky holds a small part of the planet's scattered light, where the ring beside the disk holds a lot of it. Not
        // none: in a frame 64 px across three radii is 36 px from the disk's centre, still in the seeing halo's reach.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"sky {s.Camera.SkyLevel:0.000} ADU at 1.3 to 1.6 radii, {s.Camera.FarSkyLevel:0.000} beyond 3 (offset 100), noise {s.Camera.FarSkyNoise:0.000}");
        (s.Camera.FarSkyLevel - 100).ShouldBeLessThan(0.05 * (s.Camera.SkyLevel - 100));

        // Saved and read back whole, and only under the key it was saved with.
        var path = Path.Combine(Path.GetTempPath(), $"capture-statistics-{Guid.NewGuid():N}.json");
        try
        {
            await PlanetaryCaptureStatistics.SaveAsync(s, "this capture", path, TestContext.Current.CancellationToken);
            var back = await PlanetaryCaptureStatistics.TryLoadAsync(path, "this capture", TestContext.Current.CancellationToken);
            back.ShouldNotBeNull();
            back.SeeingRms.ShouldBe(s.SeeingRms);
            back.LimbSeeingRms.ShouldBe(s.LimbSeeingRms);
            back.ShiftX.ShouldBe(s.ShiftX);
            back.Noise.ShouldBe(s.Noise);
            back.Warp.Curve.ShouldBe(s.Warp.Curve);
            back.FrameLimbs.Select(f => f.Fit?.CenterX).ShouldBe(s.FrameLimbs.Select(f => f.Fit?.CenterX));
            (await PlanetaryCaptureStatistics.TryLoadAsync(path, "another capture", TestContext.Current.CancellationToken)).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task AWarpedFrameKeepsTheLightThatReachedThePupil()
    {
        // A screen bends rays and never adds or takes light, so a warped frame holds the unwarped one's light; moved without its
        // Jacobian, the total jittered with the warp's divergence and doubled a Saturn twin's flux variation (S3, #1233). A small
        // disk and a short warp, where the jitter is largest.
        ImmutableArray<double> none = [0, 0, 0, 0, 0, 0];
        var still = await MakeAsync(none, none, size: 96, radius: 12, r0M: 0.1);
        var warped = await MakeAsync(none, none, size: 96, radius: 12, r0M: 0.1, warpRms: 0.8);
        for (var i = 0; i < still.Length; i++)
        {
            var (a, b) = (Light(still[i]), Light(warped[i]));
            TestContext.Current.TestOutputHelper?.WriteLine($"frame {i}: warped light {b / a - 1:+0.00000;-0.00000} of the unwarped");
            (b / a).ShouldBe(1, 2e-4);
        }

        // The light over the offset, in ADU.
        static double Light(ushort[] frame) => frame.Sum(v => v - 100.0);
    }

    [Fact]
    public async Task AWarpMovesSurfaceBrightnessWithoutChangingIt()
    {
        // A lossless screen keeps the radiance: a warp moves the disk's surface brightness about and ripples it nowhere, so the
        // levels of the disk's flat middle, sorted, are the unwarped twin's up to the one gain a frame that keeps its light (S3);
        // a flux-conserving Jacobian would spread them by its few percent. A map without belts, since a belt carried across the
        // region's edge moves the levels too (0.8 % on the banded map), and only the quartiles of the middle, where the limb
        // darkening is gentle.
        ImmutableArray<double> none = [0, 0, 0, 0];
        var still = await MakeAsync(none, none, size: 96, radius: 30, r0M: 0.1, flat: true);
        var warped = await MakeAsync(none, none, size: 96, radius: 30, r0M: 0.1, warpRms: 0.5, flat: true);
        var moved = 0.0;
        for (var i = 0; i < still.Length; i++)
        {
            var (a, b) = (Interior(still[i]), Interior(warped[i]));
            Array.Sort(a);
            Array.Sort(b);
            var middle = (int)(0.5 * (a.Length - 1));
            var gain = b[middle] / a[middle];
            gain.ShouldBe(1, 0.03, "the frame's one gain, which keeps its light");
            foreach (var q in new[] { 0.25, 0.75 })
            {
                var k = (int)(q * (a.Length - 1));
                TestContext.Current.TestOutputHelper?.WriteLine($"frame {i}, quantile {q}: {b[k] / a[k] / gain - 1:+0.00000;-0.00000} of the unwarped level, beside the frame's gain {gain - 1:+0.00000;-0.00000}");
                // Up to the edge's trade (0.35 % measured); a Jacobian at this warp, 0.5 px over 10 px, ripples by about 14 %.
                (b[k] / a[k] / gain).ShouldBe(1, 5e-3);
            }
            for (var p = 0; p < still[i].Length; p++)
            {
                moved = Math.Max(moved, Math.Abs(still[i][p] - warped[i][p]));
            }
        }
        // The warp did move something: a pixel changed by more than the rounding.
        moved.ShouldBeGreaterThan(20);

        static double[] Interior(ushort[] frame)
        {
            var values = new System.Collections.Generic.List<double>();
            for (var y = 0; y < 96; y++)
            {
                for (var x = 0; x < 96; x++)
                {
                    var (dx, dy) = (x - 47.7, y - 48.2);
                    if ((dx * dx) + (dy * dy) < 12 * 12)
                    {
                        values.Add(frame[(y * 96) + x] - 100.0);
                    }
                }
            }
            return [.. values];
        }
    }

    [Fact]
    public async Task EachFramesWarpIsReportedWhereItsWindowLanded()
    {
        // A warp is the truth a dewarp is scored against (R5 part 2), so it must come with the frame it moved, at the window the
        // frame's whole-pixel shift put it: frames 3 px apart report windows 3 px apart, and no warp reports nothing. The screen's
        // tilt is kept, so the shift given is where the window lands (taken out, the PSF's centroid moves it by up to a pixel more).
        ImmutableArray<double> shiftX = [0, 3, 3.4, -2];
        ImmutableArray<double> shiftY = [0, 0, 1, 0];
        var reported = new SyntheticWarp[4];
        var seen = 0;
        await MakeAsync(shiftX, shiftY, size: 96, radius: 30, r0M: 0.1, keepTilt: true, warpRms: 0.5, warps: (index, warp) => { reported[index] = warp; seen++; });
        seen.ShouldBe(4);
        (reported[1].OriginX - reported[0].OriginX).ShouldBe(3);
        (reported[2].OriginX - reported[0].OriginX, reported[2].OriginY - reported[0].OriginY).ShouldBe((3, 1));
        (reported[3].OriginX - reported[0].OriginX).ShouldBe(-2);
        reported[0].Field.IsEmpty.ShouldBeFalse();
        var (x, y) = reported[0].AtWindow(40, 40);
        (Math.Abs(x) + Math.Abs(y)).ShouldBeGreaterThan(0);

        var none = 0;
        await MakeAsync(shiftX, shiftY, size: 96, radius: 30, r0M: 0.1, warps: (_, _) => none++);
        none.ShouldBe(0);
    }

    [Fact]
    public async Task ALayerEveryPointSeesAlikeMakesTheOnePsfFrames()
    {
        // At no altitude every field point looks through the same air: the points' tilt-removed blurs blended by their tents, and the frame
        // moved by their one tilt, are the one-PSF frame up to the rounding of its samples. The gain is high enough that shot noise is a
        // twentieth of an ADU: a sky sample whose round-off differs draws its Poisson value from the stream in one path and not the other,
        // which puts every later draw out of step, so at the helper's gain the two frames would differ by their noise.
        var none = ImmutableArray.Create(0.0, 0.0, 0.0, 0.0);
        var one = await MakeAsync(none, none, size: 96, radius: 20, r0M: 0.1, keepTilt: true, localR0M: 0.05, electronsPerAdu: 1e7);
        var layered = await MakeAsync(none, none, size: 96, radius: 20, r0M: 0.1, keepTilt: true, localR0M: 0.05, highR0M: 1e6, highAltitudeM: 0, electronsPerAdu: 1e7);
        var (differing, largest, total) = (0, 0, 0);
        for (var f = 0; f < one.Length; f++)
        {
            for (var i = 0; i < one[f].Length; i++)
            {
                var d = Math.Abs(one[f][i] - layered[f][i]);
                (differing, largest, total) = (differing + (d > 0 ? 1 : 0), Math.Max(largest, d), total + 1);
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"{differing} of {total} samples differ, by at most {largest} ADU");
        largest.ShouldBeLessThanOrEqualTo(1);
        (differing / (double)total).ShouldBeLessThan(0.01);
    }

    [Fact]
    public void APointsTiltDecorrelatesFromAnothersAsTheirFootprintsPart()
    {
        // The layer at an altitude alone (the air at the pupil made negligible, no still layer): the correlation of two points' tilts along
        // the line joining their footprints is the theory's for a 25.4 cm aperture on von Karman air of a 1 m outer scale. The theory's,
        // from the integral of Phi(f) (2 pi f_x)^2 [2 J1(pi D f) / (pi D f)]^2 cos(2 pi f_x s) over the plane, over its value at s = 0:
        // +0.775 at 6.25 cm, -0.276 at 25 cm and -0.151 at 50 cm (it turns negative once the footprints part by about half the aperture).
        var options = new DegradeOptions(new Pupil(0.254, ObstructionRatio: 0.23, Vanes: 4, VaneWidthM: 0.001), 650e-9)
        {
            R0M = 1e6,
            HighR0M = 0.1,
            HighOuterScaleM = 1,
            ScreenSamples = 128,
            Seed = 3,
        };
        var seeing = new SeeingPsfSequence(options, 0.49, highReachM: 0.6);
        var scratch = new SeeingPsfSequence.ExposureScratch();
        var psf = new double[PlanetaryDegrade.PsfGrid * PlanetaryDegrade.PsfGrid];
        double[] separations = [0, 0.0625, 0.25, 0.5];
        double[] theory = [1, 0.775, -0.276, -0.151];
        const int draws = 400;
        var tilts = new double[separations.Length, draws];
        for (var d = 0; d < draws; d++)
        {
            // A long step renews all of the layer's air, so each draw is independent of the last.
            seeing.Step(100);
            seeing.Freeze();
            for (var s = 0; s < separations.Length; s++)
            {
                seeing.ExposureAt(separations[s], 0, psf, scratch);
                tilts[s, d] = PlanetaryDegrade.Centroid(psf).X;
            }
        }
        double Correlation(int s)
        {
            double a = 0, b = 0, ab = 0, aa = 0, bb = 0;
            for (var d = 0; d < draws; d++)
            {
                (a, b) = (a + tilts[0, d], b + tilts[s, d]);
            }
            (a, b) = (a / draws, b / draws);
            for (var d = 0; d < draws; d++)
            {
                var (x, y) = (tilts[0, d] - a, tilts[s, d] - b);
                (ab, aa, bb) = (ab + (x * y), aa + (x * x), bb + (y * y));
            }
            return ab / Math.Sqrt(aa * bb);
        }
        var correlations = Enumerable.Range(0, separations.Length).Select(Correlation).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine($"tilt correlation at {string.Join(", ", separations.Select((s, i) => $"{s:0.000} m {correlations[i]:0.000}"))}");
        // 400 draws read a correlation to about 0.05 near zero and better near one.
        for (var i = 0; i < separations.Length; i++)
        {
            correlations[i].ShouldBe(theory[i], 0.12);
        }
    }

    [Fact]
    public async Task OnlyALayerAtAnAltitudeVariesTheBlurOverTheDisk()
    {
        // Each field point's Strehl ratio and its tilt over the frame's: one value for every point when they all see the same air, spread
        // over the disk once the layer is high enough for their footprints to part.
        var none = ImmutableArray.Create(0.0, 0.0, 0.0);
        var at = new System.Collections.Generic.Dictionary<double, System.Collections.Generic.List<SyntheticFieldFrame>>();
        foreach (var altitude in new[] { 0.0, 10_000.0 })
        {
            var frames = new System.Collections.Generic.List<SyntheticFieldFrame>();
            await MakeAsync(none, none, size: 96, radius: 20, r0M: 0.2, keepTilt: true, highR0M: 0.1, highAltitudeM: altitude, field: (_, f) => frames.Add(f));
            at[altitude] = frames;
        }
        (double StrehlSpread, double TiltRms, double MeanBand1, double MeanBand4) Read(System.Collections.Generic.List<SyntheticFieldFrame> frames)
        {
            double spread = 0, tilt = 0, band1 = 0, band4 = 0;
            var count = 0;
            foreach (var f in frames)
            {
                var mean = f.Strehl.Average(v => (double)v);
                spread += Math.Sqrt(f.Strehl.Average(v => (v - mean) * (v - mean))) / mean;
                for (var p = 0; p < f.Points.Length; p++)
                {
                    tilt += (f.TiltX[p] * f.TiltX[p]) + (f.TiltY[p] * f.TiltY[p]);
                    band1 += f.Gain(p, 1);
                    band4 += f.Gain(p, 4);
                    count++;
                }
            }
            return (spread / frames.Count, Math.Sqrt(tilt / count / 2), band1 / count, band4 / count);
        }
        var (flat, high) = (Read(at[0]), Read(at[10_000.0]));
        TestContext.Current.TestOutputHelper?.WriteLine($"no altitude: Strehl spread {flat.StrehlSpread:0.0000}, tilt {flat.TiltRms:0.0000} px; 10 km: {high.StrehlSpread:0.0000}, {high.TiltRms:0.0000} px; band 1 and 4 gains {high.MeanBand1:0.000}, {high.MeanBand4:0.000}");
        flat.StrehlSpread.ShouldBeLessThan(1e-9);
        flat.TiltRms.ShouldBeLessThan(1e-6);
        high.StrehlSpread.ShouldBeGreaterThan(0.02);
        high.TiltRms.ShouldBeGreaterThan(0.05);
        // A point's finest band is transferred least, its coarsest most.
        high.MeanBand1.ShouldBeLessThan(high.MeanBand4);
        high.MeanBand4.ShouldBeInRange(0.5, 1.05);
    }

    [Fact]
    public void AFieldFileReadsBackWhatWasWritten()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tianwen-field-{Guid.NewGuid():N}.field");
        try
        {
            ImmutableArray<(double X, double Y)> points = [(10, 20), (16, 20)];
            var first = new SyntheticFieldFrame(points, 3, -4, [0.1f, -0.2f], [0.3f, 0.4f], [0.5f, 0.6f], [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f]);
            var second = first with { OriginX = 5, TiltX = [1f, 2f] };
            using (var writer = new SyntheticFieldFile.Writer(path, PlanetaryDegrade.FieldPatchPx))
            {
                writer.Append(first);
                writer.Append(second);
            }
            var (patch, frames) = SyntheticFieldFile.Read(path) ?? throw new InvalidOperationException("not a field file");
            patch.ShouldBe(PlanetaryDegrade.FieldPatchPx);
            frames.Length.ShouldBe(2);
            frames[0].Points.ShouldBe(points);
            (frames[1].OriginX, frames[1].OriginY).ShouldBe((5, -4));
            frames[1].TiltX.ShouldBe(second.TiltX);
            frames[0].Gain(1, 3).ShouldBe(0.7f);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void AWarpFileReadsBackWhatWasWritten()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tianwen-warp-{Guid.NewGuid():N}.warp");
        try
        {
            var first = new SyntheticWarp(10, -3, new SyntheticWarpField(2, [0.1f, 0.2f, 0.3f, 0.4f], [-0.1f, 0f, 0.5f, 1f]));
            var second = new SyntheticWarp(12, -2, new SyntheticWarpField(2, [1f, 2f, 3f, 4f], [4f, 3f, 2f, 1f]));
            using (var writer = new SyntheticWarpFile.Writer(path))
            {
                writer.Append(first);
                writer.Append(second);
            }
            var read = SyntheticWarpFile.Read(path) ?? throw new InvalidOperationException("not a warp file");
            read.Length.ShouldBe(2);
            (read[1].OriginX, read[1].OriginY).ShouldBe((12, -2));
            read[0].Field.X.ShouldBe(first.Field.X);
            read[1].Field.Y.ShouldBe(second.Field.Y);
            // Between the nodes it reads bilinearly: halfway along the first row of the first frame.
            read[0].AtWindow(SyntheticWarpField.GridStep / 2.0, 0).X.ShouldBe(0.15, 1e-6);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AWarpedFrameHoldsNoGridOfItsOwn()
    {
        // A warp stretches and squeezes the disk smoothly, so a warped frame over its unwarped twin varies smoothly too. The
        // warp is kept on a grid of nodes 4 px apart, and a Jacobian taken from the interpolated displacement jumped at every
        // node line: pixel-to-pixel steps of the ratio well above its smooth trend.
        ImmutableArray<double> none = [0, 0, 0, 0];
        var still = await MakeAsync(none, none, size: 96, radius: 30, r0M: 0.1);
        var warped = await MakeAsync(none, none, size: 96, radius: 30, r0M: 0.1, warpRms: 0.35);
        double steps = 0, trend = 0;
        var count = 0;
        for (var i = 0; i < still.Length; i++)
        {
            for (var y = 30; y < 66; y++)
            {
                for (var x = 30; x < 65; x++)
                {
                    double Ratio(int px) => (warped[i][(y * 96) + px] - 100.0) / (still[i][(y * 96) + px] - 100.0);
                    // The step to the next pixel against the mean of the steps either side: a jump stands out of its neighbours.
                    var step = Ratio(x + 1) - Ratio(x);
                    var around = (Ratio(x + 2) - Ratio(x - 1)) / 3;
                    steps += (step - around) * (step - around);
                    trend += around * around;
                    count++;
                }
            }
        }
        var (jumpRms, trendRms) = (Math.Sqrt(steps / count), Math.Sqrt(trend / count));
        TestContext.Current.TestOutputHelper?.WriteLine($"the ratio's pixel steps depart from their neighbours' by {jumpRms:0.00000} RMS; its smooth trend steps {trendRms:0.00000} a pixel");
        jumpRms.ShouldBeLessThan(0.001);
    }

    [Fact]
    public async Task ALayerAtTheTelescopeBlursWithoutMovingTheDisk()
    {
        // Turbulence whose outer scale is a fifth of the pupil (the tube's air) against the free atmosphere's, their r0 chosen for
        // a like loss of peak: the tilt, which only scales past the outer scale carry, is a small part of the free air's. The
        // free air is held still between frames' draws by its own r0 alone; each frame's tilt is read off its truth.
        const int frames = 48;
        ImmutableArray<double> none = [.. Enumerable.Repeat(0.0, frames)];
        var (local, free) = (ImmutableArray<SyntheticFrame>.Empty, ImmutableArray<SyntheticFrame>.Empty);
        await MakeAsync(none, none, size: 64, radius: 12, r0M: 100, keepTilt: true, made: t => local = t, localR0M: 0.008, localOuterScaleM: 0.05);
        await MakeAsync(none, none, size: 64, radius: 12, r0M: 0.05, keepTilt: true, made: t => free = t);

        static (double Tilt, double Strehl) Measure(ImmutableArray<SyntheticFrame> made)
        {
            var (mx, my) = (made.Average(f => f.ShiftX), made.Average(f => f.ShiftY));
            return (Math.Sqrt(made.Average(f => ((f.ShiftX - mx) * (f.ShiftX - mx)) + ((f.ShiftY - my) * (f.ShiftY - my))) / 2), made.Average(f => f.Strehl));
        }
        var (localTilt, localStrehl) = Measure(local);
        var (freeTilt, freeStrehl) = Measure(free);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"local layer: tilt {localTilt:0.000} px RMS, Strehl {localStrehl:0.000}; free atmosphere: tilt {freeTilt:0.000} px RMS, Strehl {freeStrehl:0.000}");
        localStrehl.ShouldBeLessThan(0.5);
        freeStrehl.ShouldBeLessThan(0.5);
        localTilt.ShouldBeLessThan(0.3 * freeTilt);
    }

    [Fact]
    public async Task ScatteredLightLeavesTheDiskForTheSkyAndIsNotLost()
    {
        // A tenth of the light scattered wide: the frame's light is the same, the disk's inside 0.8 radii a tenth less, less its
        // own share of the scatter back, and the sky beyond the PSF grid's 32 px reach is lit where without it there is nothing.
        ImmutableArray<double> none = [0, 0];
        var still = await MakeAsync(none, none, size: 96, radius: 12, r0M: 10);
        var scattered = await MakeAsync(none, none, size: 96, radius: 12, r0M: 10, scatter: 0.1);
        static (double Total, double Disk, double FarSky) Light(ushort[] frame)
        {
            double total = 0, disk = 0, far = 0;
            var farCount = 0;
            for (var y = 0; y < 96; y++)
            {
                for (var x = 0; x < 96; x++)
                {
                    var v = frame[(y * 96) + x] - 100.0;
                    var r = Math.Sqrt(((x - 47.7) * (x - 47.7)) + ((y - 48.2) * (y - 48.2)));
                    total += v;
                    if (r < 0.8 * 12)
                    {
                        disk += v;
                    }
                    else if (r > 12 + 34)
                    {
                        far += v;
                        farCount++;
                    }
                }
            }
            return (total, disk, far / farCount);
        }
        var (a, b) = (Light(still[0]), Light(scattered[0]));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"light {b.Total / a.Total:0.0000} of the unscattered frame's; disk {b.Disk / a.Disk:0.0000}; sky past the PSF grid's reach {a.FarSky:0.000} ADU unscattered, {b.FarSky:0.000} scattered");
        (b.Total / a.Total).ShouldBe(1, 0.01);
        (b.Disk / a.Disk).ShouldBeInRange(0.9, 0.95);
        a.FarSky.ShouldBeLessThan(0.5);
        b.FarSky.ShouldBeGreaterThan(5);
    }

    [Fact]
    public async Task ATelescopesDefocusCostsTheStrehlMarechalSays()
    {
        // With the air all but still (r0 of 10 m), 50 nm RMS of defocus at 650 nm leaves exp(-(2 pi W / lambda)^2) of the peak:
        // 0.79, Marechal's approximation, good to a percent or two this close to the diffraction limit. A clear aperture: the
        // RMS is Zernike's over the whole disk, and the Newtonian's obstruction takes out the middle, where defocus is deepest
        // (it scored 0.81).
        ImmutableArray<double> none = [0, 0, 0];
        var truths = ImmutableArray<SyntheticFrame>.Empty;
        await MakeAsync(none, none, size: 64, radius: 12, r0M: 10, made: t => truths = t, defocusNm: 50, pupil: new Pupil(0.254));
        var expected = Math.Exp(-Math.Pow(2 * Math.PI * 50 / 650, 2));
        foreach (var frame in truths)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"Strehl {frame.Strehl:0.0000}, Marechal {expected:0.0000}");
            frame.Strehl.ShouldBe(expected, 0.02);
        }
    }

    [Fact]
    public async Task TheLimbsEdgeWidthIsTheBlursForAUniformDisk()
    {
        // A uniform disk blurred by a Gaussian of sigma 2 px: a straight edge's level over its steepest fall is sqrt(2 pi) sigma,
        // and a disk 30 px in radius is straight enough for that to the percent.
        const int size = 128;
        const double sigma = 2;
        var frames = new float[12][,];
        var times = new DateTimeOffset[frames.Length];
        var random = new Random(4);
        var sharp = new double[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // The disk's area in each pixel, from 8 by 8 points.
                var inside = 0;
                for (var sy = 0; sy < 8; sy++)
                {
                    for (var sx = 0; sx < 8; sx++)
                    {
                        var dx = x - 63.7 + ((sx + 0.5) / 8) - 0.5;
                        var dy = y - 64.2 + ((sy + 0.5) / 8) - 0.5;
                        inside += (dx * dx) + (dy * dy) < 30 * 30 ? 1 : 0;
                    }
                }
                sharp[y, x] = inside / 64.0;
            }
        }
        var blurred = GaussianBlur(sharp, sigma);
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    frames[i][y, x] = (float)(0.1 + (0.6 * blurred[y, x]) + (0.0005 * PhaseScreen.Gaussian(random)));
                }
            }
            times[i] = Night + TimeSpan.FromMilliseconds(5 * i);
        }

        using var stream = new InMemoryFrameStream(frames, times);
        var statistics = await PlanetaryCaptureStatistics.MeasureAsync(stream, new CaptureStatisticsOptions(new LimbFitOptions(1)) { FullScaleAdu = 1000, Pairs = 5 },
            cancellationToken: TestContext.Current.CancellationToken);

        statistics.ShouldNotBeNull();
        TestContext.Current.TestOutputHelper?.WriteLine($"limb edge width {statistics.LimbWidthAll:0.000} px (every frame), {statistics.LimbWidthBest:0.000} (best tenth); sqrt(2 pi) sigma {Math.Sqrt(2 * Math.PI) * sigma:0.000}");
        statistics.LimbWidthAll.ShouldBe(Math.Sqrt(2 * Math.PI) * sigma, 0.1);
    }

    private static double[,] GaussianBlur(double[,] image, double sigma)
    {
        var (height, width) = (image.GetLength(0), image.GetLength(1));
        var radius = (int)Math.Ceiling(4 * sigma);
        var kernel = new double[(2 * radius) + 1];
        double total = 0;
        for (var t = -radius; t <= radius; t++)
        {
            kernel[t + radius] = Math.Exp(-0.5 * t * t / (sigma * sigma));
            total += kernel[t + radius];
        }
        var rows = new double[height, width];
        var result = new double[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double s = 0;
                for (var t = -radius; t <= radius; t++)
                {
                    s += kernel[t + radius] * image[y, Math.Clamp(x + t, 0, width - 1)];
                }
                rows[y, x] = s / total;
            }
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double s = 0;
                for (var t = -radius; t <= radius; t++)
                {
                    s += kernel[t + radius] * rows[Math.Clamp(y + t, 0, height - 1), x];
                }
                result[y, x] = s / total;
            }
        }
        return result;
    }

    private const double FullScale = 65535;
    private const double DiskLevel = 20000;

    // Frames of a banded Jupiter (the aligners want texture, as a real one has), bright and nearly noiseless (a large gain, no
    // read noise, 16 bits), under seeing of `r0M`.
    private static async Task<ushort[][]> MakeAsync(ImmutableArray<double> shiftX, ImmutableArray<double> shiftY, int size, double radius, double r0M,
        bool keepTilt = false, Action<ImmutableArray<SyntheticFrame>>? made = null, double warpRms = 0, bool flat = false, double defocusNm = 0,
        Pupil? pupil = null, double localR0M = double.PositiveInfinity, double localOuterScaleM = 0.25, double scatter = 0,
        Action<int, SyntheticWarp>? warps = null, double highR0M = double.PositiveInfinity, double highAltitudeM = 10_000,
        Action<int, SyntheticFieldFrame>? field = null, double electronsPerAdu = 1000, bool atShownLevel = false)
    {
        var map = BandedMap(flat);
        var builder = ImmutableArray.CreateBuilder<DateTimeOffset>(shiftX.Length);
        for (var i = 0; i < shiftX.Length; i++)
        {
            builder.Add(Night + TimeSpan.FromMilliseconds(5 * i));
        }
        var times = builder.MoveToImmutable();
        var options = new DegradeOptions(pupil ?? new Pupil(0.254, ObstructionRatio: 0.23, Vanes: 4, VaneWidthM: 0.001), 650e-9)
        {
            R0M = r0M,
            FullScaleAdu = FullScale,
            OffsetAdu = 100,
            ReadNoiseAdu = 0,
            ElectronsPerAdu = electronsPerAdu,
            DiskLevelAdu = DiskLevel,
            ScreenSamples = 128,
            KeepScreenTilt = keepTilt,
            DefocusNm = defocusNm,
            LocalR0M = localR0M,
            LocalOuterScaleM = localOuterScaleM,
            ScatterFraction = scatter,
            WarpRmsPx = warpRms,
            WarpLengthPx = 10,
            WarpLag1 = 0.5,
            HighR0M = highR0M,
            HighAltitudeM = highAltitudeM,
        };
        var frames = new ushort[shiftX.Length][];
        var reference = new DiskPlacement((size / 2) - 0.3, (size / 2) + 0.2, radius, NorthAngleDeg: -80);
        if (atShownLevel)
        {
            options = options with { DiskLevelAdu = DiskLevel * PlanetaryDegrade.ShownLevelGain(map, CatalogIndex.Jupiter, times, reference, 0.49, options) };
        }
        var truths = await PlanetaryDegrade.MakeAsync(map, CatalogIndex.Jupiter, times, reference, 0.49, shiftX, shiftY, [], size, size, options,
            (index, samples) => frames[index] = samples, warps: warps, field: field, cancellationToken: TestContext.Current.CancellationToken);
        made?.Invoke(truths);
        return frames;
    }

    // A global map of belts and one spot (a uniform one when flat): texture enough to register on and to see a blur in.
    internal static PlanetMap BandedMap(bool flat = false)
    {
        var values = new float[360 * 180];
        for (var row = 0; row < 180; row++)
        {
            var latitude = (89.5 - row) * Math.PI / 180;
            for (var column = 0; column < 360; column++)
            {
                values[(row * 360) + column] = flat ? 1f
                    : (float)(1 - (0.3 * Math.Pow(Math.Sin(4 * latitude), 2)) - (0.2 * Math.Exp(-Math.Pow((column - 90) / 15.0, 2) - Math.Pow((row - 112) / 6.0, 2))));
            }
        }
        return new PlanetMap(values, 360, 180);
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
