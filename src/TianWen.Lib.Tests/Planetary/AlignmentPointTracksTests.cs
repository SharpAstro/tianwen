using System;
using Shouldly;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The alignment points' tracks over a capture (docs/plans/planetary-restoration.md, R5 part 2): a frame's points pooled over the
/// frames either side, and put on the median geometry, from warps the test knows.
/// </summary>
public class AlignmentPointTracksTests
{
    private const int Frames = 400;
    private static readonly AlignmentPointShift[] Points = [new AlignmentPointShift(10, 10, 0, 0), new AlignmentPointShift(30, 10, 0, 0)];

    [Fact]
    public void AFramesOwnPointsAreItsWarpOverItsShift()
    {
        // No pooling, no median: a point's residual is its warp over the frame's exact global shift, what the matcher wrote,
        // since its patch is cut there. The shift's fraction is the mesh's, never a residual's (it once was: R5a).
        var global = new (double Dx, double Dy)[Frames];
        var (warpX, warpY) = (new float[Frames * 2], new float[Frames * 2]);
        global[7] = (3.3, -1.8);
        (warpX[14], warpY[14], warpX[15], warpY[15]) = (0.25f, -0.5f, -0.1f, 0.4f);
        var tracks = AlignmentPointTracks.FromWarps(global, warpX, warpY, Points);
        var points = new AlignmentPointShift[2];
        tracks.Points(7, sigmaFrames: 0, medianGeometry: false, points);
        points[0].ResidualX.ShouldBe(0.25f, 1e-5f);
        points[0].ResidualY.ShouldBe(-0.5f, 1e-5f);
        points[1].ResidualX.ShouldBe(-0.1f, 1e-5f);
        points[1].ResidualY.ShouldBe(0.4f, 1e-5f);
        (points[0].X, points[0].Y).ShouldBe((10f, 10f));
    }

    [Fact]
    public void PoolingKeepsASlowWarpAndTakesOutAFramesOwnNoise()
    {
        // A warp that turns over in 60 frames, each frame's match 0.35 px off it: pooled over 3 frames either side, the reading
        // lands closer to the warp than any one frame's did.
        var random = new Random(9);
        var global = new (double Dx, double Dy)[Frames];
        var (truth, warpX, warpY) = (new double[Frames], new float[Frames * 2], new float[Frames * 2]);
        for (var f = 0; f < Frames; f++)
        {
            truth[f] = 0.6 * Math.Sin(2 * Math.PI * f / 60);
            for (var p = 0; p < 2; p++)
            {
                warpX[(f * 2) + p] = (float)(truth[f] + (0.35 * PhaseScreen.Gaussian(random)));
            }
        }
        var tracks = AlignmentPointTracks.FromWarps(global, warpX, warpY, Points);
        var points = new AlignmentPointShift[2];
        double own = 0, pooled = 0;
        var count = 0;
        for (var f = 20; f < Frames - 20; f++)
        {
            tracks.Points(f, sigmaFrames: 0, medianGeometry: false, points);
            own += (points[0].ResidualX - truth[f]) * (points[0].ResidualX - truth[f]);
            tracks.Points(f, sigmaFrames: 3, medianGeometry: false, points);
            pooled += (points[0].ResidualX - truth[f]) * (points[0].ResidualX - truth[f]);
            count++;
        }
        var (ownRms, pooledRms) = (Math.Sqrt(own / count), Math.Sqrt(pooled / count));
        TestContext.Current.TestOutputHelper?.WriteLine($"a frame's own match {ownRms:0.000} px off the warp, pooled over 3 frames {pooledRms:0.000}");
        pooledRms.ShouldBeLessThan(0.5 * ownRms);
    }

    [Fact]
    public void TheMedianGeometryTakesOutTheReferencesOwnWarp()
    {
        // Every frame reads each point displaced by the reference's own warp there (0.4 and -0.3 px) plus its own, which averages
        // out: on the median geometry, the reference's is gone.
        var random = new Random(4);
        var global = new (double Dx, double Dy)[Frames];
        var (warpX, warpY) = (new float[Frames * 2], new float[Frames * 2]);
        for (var f = 0; f < Frames; f++)
        {
            warpX[f * 2] = (float)(0.4 + (0.3 * PhaseScreen.Gaussian(random)));
            warpX[(f * 2) + 1] = (float)(-0.3 + (0.3 * PhaseScreen.Gaussian(random)));
        }
        var tracks = AlignmentPointTracks.FromWarps(global, warpX, warpY, Points);
        var points = new AlignmentPointShift[2];
        double first = 0, second = 0;
        for (var f = 0; f < Frames; f++)
        {
            tracks.Points(f, sigmaFrames: 0, medianGeometry: true, points);
            first += points[0].ResidualX;
            second += points[1].ResidualX;
        }
        (first / Frames).ShouldBe(0, 0.05);
        (second / Frames).ShouldBe(0, 0.05);
    }
}
