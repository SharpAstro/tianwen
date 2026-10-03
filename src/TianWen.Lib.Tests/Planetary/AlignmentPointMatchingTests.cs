using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;
using Xunit;
using static TianWen.Lib.Tests.PlanetaryMetricsTests;

namespace TianWen.Lib.Tests;

/// <summary>
/// How well an alignment point's patch is placed on a single 8-bit planetary frame (docs/plans/planetary-restoration.md, R5): a
/// banded, limb-darkened disk at the level and noise of 2022-09-03's twin (47 ADU over the sky, 1.25 ADU a pixel, rounded),
/// moved by a known sub-pixel shift, its 16 px patches registered by phase correlation, whitened and not.
/// </summary>
public class AlignmentPointMatchingTests
{
    private const double DiskAdu = 47;
    private const double NoiseAdu = 1.25;
    private const int Patch = 16;

    [Fact]
    public void OnANoisy8BitFrameAPlainCorrelationPlacesAPatchCloserThanAWhitenedOne()
    {
        var sharp = Blur(Banded(), 1.5);
        var (dx, dy) = (0.6, -0.4);
        var moved = PlanetaryMetrics.Shift(sharp, Size, Size, dx, dy);
        var random = new Random(3);
        double whitened = 0, plain = 0;
        var count = 0;
        foreach (var (cx, cy) in new[] { (64, 64), (52, 60), (76, 66), (60, 76), (68, 50) })
        {
            for (var draw = 0; draw < 40; draw++)
            {
                var reference = Tile(Frame(sharp, random), cx, cy);
                var moving = Tile(Frame(moved, random), cx, cy);
                var w = PhaseCorrelation.Estimate(PhaseCorrelation.PrepareReferenceSpectrum(reference, Patch, Patch, applyWindow: true, whiten: true),
                    moving, Patch, Patch, new System.Numerics.Complex[Patch * Patch], applyWindow: true, whiten: true);
                var p = PhaseCorrelation.Estimate(PhaseCorrelation.PrepareReferenceSpectrum(reference, Patch, Patch, applyWindow: true, whiten: false),
                    moving, Patch, Patch, new System.Numerics.Complex[Patch * Patch], applyWindow: true, whiten: false);
                whitened += ((w.Dx - dx) * (w.Dx - dx)) + ((w.Dy - dy) * (w.Dy - dy));
                plain += ((p.Dx - dx) * (p.Dx - dx)) + ((p.Dy - dy) * (p.Dy - dy));
                count += 2;
            }
        }
        var (whitenedRms, plainRms) = (Math.Sqrt(whitened / count), Math.Sqrt(plain / count));
        TestContext.Current.TestOutputHelper?.WriteLine($"a 16 px patch placed to {whitenedRms:0.000} px RMS a axis whitened, {plainRms:0.000} px plain");
        plainRms.ShouldBeLessThan(whitenedRms);
    }

    [Fact]
    public void APlainCorrelationShrinksTheShiftItReadsAndShiftingTheWindowTradesTheShrinkForNoise()
    {
        // #1081: both patches are Hann-windowed where the point is, so the window's own correlation, which peaks at no shift, pulls
        // a reading toward zero where the texture under it is smooth. On a banded disk with no noise a 16 px patch reads half a
        // rigid shift. Cutting the moving patch again where the shift read so far puts it (window shifting) undoes the shrink as
        // the bias falls with the residual, but on an 8-bit frame it lifts the noise more: the error against the truth grows.
        var sharp = Blur(Banded(), 1.5);
        var shifts = new[] { -0.8, -0.5, -0.3, -0.15, 0.15, 0.3, 0.5, 0.8 };
        var places = new[] { (64, 64), (52, 60), (76, 66), (60, 76), (68, 50) };
        (double Slope, double Error) Read(int passes, int draws, Random? random)
        {
            double rt = 0, tt = 0, error = 0;
            var count = 0;
            foreach (var shift in shifts)
            {
                var moved = PlanetaryMetrics.Shift(sharp, Size, Size, shift, shift / 2);
                foreach (var (cx, cy) in places)
                {
                    for (var draw = 0; draw < draws; draw++)
                    {
                        var referenceFrame = random is null ? sharp : Frame(sharp, random);
                        var movingFrame = random is null ? moved : Frame(moved, random);
                        var spectrum = PhaseCorrelation.PrepareReferenceSpectrum(Tile(referenceFrame, cx, cy), Patch, Patch, applyWindow: true, whiten: false);
                        double ex = 0, ey = 0;
                        for (var pass = 0; pass < passes; pass++)
                        {
                            var back = pass == 0 ? movingFrame : PlanetaryMetrics.Shift(movingFrame, Size, Size, -ex, -ey);
                            var p = PhaseCorrelation.Estimate(spectrum, Tile(back, cx, cy), Patch, Patch, new System.Numerics.Complex[Patch * Patch], applyWindow: true, whiten: false);
                            (ex, ey) = (ex + p.Dx, ey + p.Dy);
                        }
                        rt += (ex * shift) + (ey * shift / 2);
                        tt += (shift * shift) + (shift * shift / 4);
                        error += ((ex - shift) * (ex - shift)) + ((ey - (shift / 2)) * (ey - (shift / 2)));
                        count += 2;
                    }
                }
            }
            return (rt / tt, Math.Sqrt(error / count));
        }

        var clean = Read(passes: 1, draws: 1, random: null);
        var once = Read(passes: 1, draws: 8, new Random(3));
        var shifted = Read(passes: 3, draws: 8, new Random(3));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"no noise: slope {clean.Slope:0.000}; 8-bit, one pass: slope {once.Slope:0.000}, error {once.Error:0.000} px; three passes: slope {shifted.Slope:0.000}, error {shifted.Error:0.000} px");
        clean.Slope.ShouldBeInRange(0.4, 0.7, "the windows' pull toward no shift, with no noise at all");
        shifted.Slope.ShouldBeGreaterThan(once.Slope + 0.1, "window shifting undoes the shrink");
        shifted.Error.ShouldBeGreaterThan(once.Error, "but the noise it lifts costs more than the bias it removes");
    }

    [Fact]
    public void ASquareDifferenceReadsTheShiftTheWindowsShrinkAndNeitherEstimatorBeatsTheBound()
    {
        // #1082: Loefdahl's square difference, unwindowed against a reference three pixels larger each side, has no window to pull
        // its reading toward no shift: on the clean banded disk it reads a rigid shift nearly whole where the plain correlation reads
        // about half. The maximum-likelihood weight on the plain cross-spectrum is a constant wherever the reference holds signal, so
        // the weighted correlation reads as the plain one does. On 8-bit frames against a noiseless reference, as a stack of many
        // frames nearly is, neither reads more truly, unbiased, than the Cramer-Rao bound allows; and the noise a frame's patch is read
        // to carry is the noise put in.
        var sharp = Blur(Banded(), 1.5);
        var places = new[] { (64, 64), (52, 60), (76, 66), (60, 76), (68, 50) };
        const int Margin = AlignmentPointMatcher.SquareDifferenceMargin;
        var big = Patch + (2 * Margin);
        var scratch = new double[(Patch * Patch) + (((2 * Margin) + 1) * ((2 * Margin) + 1))];
        var reference = Scaled(sharp);
        var image = ToImage(reference);
        var points = ImmutableArray.CreateRange(places.Select(p => new PixelPoint(p.Item1, p.Item2)));
        var matcher = AlignmentPointMatcher.FromReference(image, points, Patch, whiten: false, PlanetaryPointEstimator.WeightedCorrelation);

        (double Slope, double Error) Read(Func<float[], int, int, (double Dx, double Dy)> estimate, Random? random, int draws)
        {
            double rt = 0, tt = 0, residual = 0;
            var readings = new List<(double Rx, double Ry, double Tx, double Ty)>();
            foreach (var shift in new[] { -0.8, -0.5, -0.3, -0.15, 0.15, 0.3, 0.5, 0.8 })
            {
                var moved = Scaled(PlanetaryMetrics.Shift(sharp, Size, Size, shift, shift / 2));
                foreach (var (cx, cy) in places)
                {
                    for (var draw = 0; draw < draws; draw++)
                    {
                        var (dx, dy) = estimate(random is null ? moved : Noisy(moved, random), cx, cy);
                        readings.Add((dx, dy, shift, shift / 2));
                        rt += (dx * shift) + (dy * shift / 2);
                        tt += (shift * shift) + (shift * shift / 4);
                    }
                }
            }
            var slope = rt / tt;
            foreach (var (rx, ry, tx, ty) in readings)
            {
                residual += Math.Pow(rx - (slope * tx), 2) + Math.Pow(ry - (slope * ty), 2);
            }
            return (slope, Math.Sqrt(residual / (2 * readings.Count)));
        }
        (double, double) Plain(float[] frame, int cx, int cy)
        {
            var p = PhaseCorrelation.Estimate(PhaseCorrelation.PrepareReferenceSpectrum(Tile(reference, cx, cy), Patch, Patch, applyWindow: true, whiten: false),
                Tile(frame, cx, cy), Patch, Patch, new Complex[Patch * Patch], applyWindow: true, whiten: false);
            return (p.Dx, p.Dy);
        }
        (double, double) Weighted(float[] frame, int cx, int cy)
        {
            var spectrum = PhaseCorrelation.PrepareReferenceSpectrum(Tile(reference, cx, cy), Patch, Patch, applyWindow: true, whiten: false);
            var floor = PhaseCorrelation.SpectralFloor(spectrum, Patch, Patch);
            var signal = spectrum.Select(c => Math.Max(0, (c.Magnitude * c.Magnitude) - floor)).ToArray();
            var p = PhaseCorrelation.EstimateWeighted(spectrum, signal, floor, Tile(frame, cx, cy), Patch, Patch, new Complex[Patch * Patch]);
            return (p.Dx, p.Dy);
        }
        (double, double) Difference(float[] frame, int cx, int cy) => SquareDifferenceShift.Estimate(TileOf(reference, cx, cy, big), Tile(frame, cx, cy), Patch, Margin, scratch);

        var (plainClean, differenceClean) = (Read(Plain, null, 1), Read(Difference, null, 1));
        var (plain, weighted, difference) = (Read(Plain, new Random(3), 8), Read(Weighted, new Random(3), 8), Read(Difference, new Random(3), 8));
        // A pixel's noise in a frame: its Gaussian and the rounding to whole ADU.
        var variance = (NoiseAdu * NoiseAdu) + (1.0 / 12);
        double boundSquared = 0, noiseRead = 0;
        var noisyFrame = ToImage(Noisy(reference, new Random(5)));
        for (var i = 0; i < places.Length; i++)
        {
            var (bx, by) = matcher.Bound(i, variance);
            boundSquared += (bx + by) / 2;
            noiseRead += matcher.PatchNoise(noisyFrame, 0, 0, i);
        }
        var bound = Math.Sqrt(boundSquared / places.Length);
        noiseRead /= places.Length;
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"clean slope: plain {plainClean.Slope:0.000}, square difference {differenceClean.Slope:0.000}; 8-bit, unbiased error: plain {plain.Error / plain.Slope:0.000} (slope {plain.Slope:0.000}), weighted {weighted.Error / weighted.Slope:0.000} (slope {weighted.Slope:0.000}), square difference {difference.Error / difference.Slope:0.000} (slope {difference.Slope:0.000}); the bound {bound:0.000} px; noise read {noiseRead:0.000} against {variance:0.000} put in"));

        differenceClean.Slope.ShouldBeGreaterThan(0.8, "no window pulls the square difference's reading toward no shift");
        plainClean.Slope.ShouldBeLessThan(0.7, "the plain correlation's two windows do");
        weighted.Slope.ShouldBe(plain.Slope, 0.05 * plain.Slope, "against a noiseless reference the weight is a constant where it holds signal");
        (plain.Error / plain.Slope).ShouldBeGreaterThan(bound);
        (difference.Error / difference.Slope).ShouldBeGreaterThan(bound);
        noiseRead.ShouldBe(variance, 0.25 * variance, "a patch's spectral floor, its median over ln 2, is the noise in it");
    }

    // The plane at the twin's level over a sky of zero, noiseless and unrounded: a reference stacked from many frames.
    private static float[] Scaled(float[] plane)
    {
        var scaled = new float[plane.Length];
        for (var i = 0; i < plane.Length; i++)
        {
            scaled[i] = (float)(plane[i] * DiskAdu);
        }
        return scaled;
    }

    // A frame of a scaled plane: its noise added and rounded to whole ADU.
    private static float[] Noisy(float[] scaled, Random random)
    {
        var frame = new float[scaled.Length];
        for (var i = 0; i < scaled.Length; i++)
        {
            frame[i] = (float)Math.Round(scaled[i] + (NoiseAdu * PhaseScreen.Gaussian(random)));
        }
        return frame;
    }

    private static Image ToImage(float[] plane)
    {
        var data = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                data[y, x] = plane[(y * Size) + x];
            }
        }
        return new Image([data], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }

    private static float[] TileOf(float[] frame, int cx, int cy, int size)
    {
        var tile = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                tile[(y * size) + x] = frame[((cy - (size / 2) + y) * Size) + cx - (size / 2) + x];
            }
        }
        return tile;
    }

    // The plane at the twin's level over a sky of zero, with its noise, rounded to whole ADU.
    private static float[] Frame(float[] plane, Random random)
    {
        var frame = new float[plane.Length];
        for (var i = 0; i < plane.Length; i++)
        {
            frame[i] = (float)Math.Round((plane[i] * DiskAdu) + (NoiseAdu * PhaseScreen.Gaussian(random)));
        }
        return frame;
    }

    private static float[] Tile(float[] frame, int cx, int cy)
    {
        var tile = new float[Patch * Patch];
        for (var y = 0; y < Patch; y++)
        {
            for (var x = 0; x < Patch; x++)
            {
                tile[(y * Patch) + x] = frame[((cy - (Patch / 2) + y) * Size) + cx - (Patch / 2) + x];
            }
        }
        return tile;
    }
}
