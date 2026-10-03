using System;
using Shouldly;
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
