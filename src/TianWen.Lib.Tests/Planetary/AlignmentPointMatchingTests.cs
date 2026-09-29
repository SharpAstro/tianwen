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
