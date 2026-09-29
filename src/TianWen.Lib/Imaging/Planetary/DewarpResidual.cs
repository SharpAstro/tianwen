using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>One way of taking the points' warp, and what it left of a synthetic capture's true warp, px RMS a axis.</summary>
/// <param name="PoolFrames">The Gaussian the points' warp was pooled over, in frames (0 for each frame's own).</param>
/// <param name="MedianGeometry">Whether the points' median warp was taken out (the stack on the median geometry).</param>
/// <param name="ResidualX">The true warp less the points' reading at the points, across.</param>
/// <param name="ResidualY">Down.</param>
/// <param name="MeshResidualX">The true warp less what the stack applies, the points' mesh, over the disk the points cover, across.</param>
/// <param name="MeshResidualY">Down.</param>
/// <param name="TruthResidualX">The points' reading against the truth's own geometry, whichever geometry the stack is on: on the
/// reference geometry the reference frame's own warp stays in the stack, and this counts it. Across.</param>
/// <param name="TruthResidualY">Down.</param>
public readonly record struct DewarpReading(double PoolFrames, bool MedianGeometry, double ResidualX, double ResidualY, double MeshResidualX, double MeshResidualY,
    double TruthResidualX, double TruthResidualY);

/// <summary>What the points were measured against: the true warp a stack on each geometry would be left with undewarped.</summary>
/// <param name="Points">The alignment points.</param>
/// <param name="Frames">The frames compared.</param>
/// <param name="ReferenceIndex">The stacker's reference frame.</param>
/// <param name="UndewarpedX">The true warp at the points, on the reference geometry (each frame's less the reference's), across.</param>
/// <param name="UndewarpedY">Down.</param>
/// <param name="UndewarpedMedianX">On the median geometry (each frame's own), across.</param>
/// <param name="UndewarpedMedianY">Down.</param>
/// <param name="MeshPlaces">The places over the disk the mesh is compared at, every <see cref="DewarpResidual.MeshStep"/> px across the points.</param>
/// <param name="MeshUndewarpedX">The true warp there, on the reference geometry, across.</param>
/// <param name="MeshUndewarpedY">Down.</param>
/// <param name="MeshUndewarpedMedianX">On the median geometry, across.</param>
/// <param name="MeshUndewarpedMedianY">Down.</param>
/// <param name="Readings">Each way of taking the points' warp.</param>
public sealed record DewarpReport(int Points, int Frames, int ReferenceIndex, double UndewarpedX, double UndewarpedY, double UndewarpedMedianX, double UndewarpedMedianY,
    int MeshPlaces, double MeshUndewarpedX, double MeshUndewarpedY, double MeshUndewarpedMedianX, double MeshUndewarpedMedianY, ImmutableArray<DewarpReading> Readings);

/// <summary>
/// The dewarp's residual against a synthetic capture's true warp (docs/plans/planetary-restoration.md, R5 part 2): every frame's
/// points read as the stacker reads them (<see cref="LuckyImagingStacker"/>, the same reference, points and matcher), each point's
/// warp compared with the warp <see cref="PlanetaryDegrade"/> applied there. On the reference geometry a frame must be moved by
/// its warp less the reference frame's; on the median geometry by its own. Each frame's mean over its points is the global
/// aligner's, and is taken out of both. The points' reading is compared at the points, and what the stack applies, the
/// displacement mesh the points are blended into (<see cref="PlanetaryStackOptions.MeshNodeSpacing"/>,
/// <see cref="PlanetaryStackOptions.MeshInfluence"/>), over the disk they cover.
/// </summary>
public static class DewarpResidual
{
    /// <summary>The spacing, px, of the places the mesh is compared at.</summary>
    public const int MeshStep = 4;

    /// <summary>
    /// Measures the points' warp over <paramref name="stream"/> against <paramref name="truth"/> (one per frame, in order), stacked
    /// as <paramref name="options"/> says, pooled over each of <paramref name="pools"/> frames and on both geometries.
    /// </summary>
    public static async Task<DewarpReport> MeasureAsync(IPlanetaryFrameStream stream, ImmutableArray<SyntheticWarp> truth, PlanetaryStackOptions options,
        IReadOnlyList<double> pools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(truth.Length, stream.FrameCount, nameof(truth));
        var (tracks, referenceIndex) = await LuckyImagingStacker.TrackAsync(stream, options, cancellationToken).ConfigureAwait(false);
        var points = tracks.AlignmentPoints.ToArray();
        var (frames, n) = (tracks.Frames, points.Length);

        // Each point's true warp in each frame, read where the point sits in the reference frame's window: the window moves with
        // the planet, so a feature keeps its window position from frame to frame, to the fraction of a pixel of the shift.
        var reference = truth[referenceIndex];
        var (trueX, trueY) = (new double[frames * n], new double[frames * n]);
        for (var f = 0; f < frames; f++)
        {
            for (var p = 0; p < n; p++)
            {
                (trueX[(f * n) + p], trueY[(f * n) + p]) = truth[f].AtWindow(points[p].X - reference.OriginX, points[p].Y - reference.OriginY);
            }
        }
        var (refX, refY) = (new double[n], new double[n]);
        for (var p = 0; p < n; p++)
        {
            (refX[p], refY[p]) = (trueX[(referenceIndex * n) + p], trueY[(referenceIndex * n) + p]);
        }

        var (undewarpedX, undewarpedY) = Rms(frames, n, (f, p) => (0, 0), (f, p) => (trueX[(f * n) + p] - refX[p], trueY[(f * n) + p] - refY[p]));
        var (undewarpedMedianX, undewarpedMedianY) = Rms(frames, n, (f, p) => (0, 0), (f, p) => (trueX[(f * n) + p], trueY[(f * n) + p]));
        // The places the mesh is compared at: a grid over the points' box, and the true warp at each, frame by frame.
        var (minX, maxX, minY, maxY) = (points.Min(p => p.X), points.Max(p => p.X), points.Min(p => p.Y), points.Max(p => p.Y));
        var places = new List<(float X, float Y)>();
        for (var y = minY; y <= maxY; y += MeshStep)
        {
            for (var x = minX; x <= maxX; x += MeshStep)
            {
                places.Add((x, y));
            }
        }
        var m = places.Count;
        var (placeX, placeY) = (new double[frames * m], new double[frames * m]);
        for (var f = 0; f < frames; f++)
        {
            for (var q = 0; q < m; q++)
            {
                (placeX[(f * m) + q], placeY[(f * m) + q]) = truth[f].AtWindow(places[q].X - reference.OriginX, places[q].Y - reference.OriginY);
            }
        }
        (double X, double Y) PlaceTarget(int f, int q, bool median) => median
            ? (placeX[(f * m) + q], placeY[(f * m) + q])
            : (placeX[(f * m) + q] - placeX[(referenceIndex * m) + q], placeY[(f * m) + q] - placeY[(referenceIndex * m) + q]);
        var (meshUndewarpedX, meshUndewarpedY) = Rms(frames, m, (f, q) => (0, 0), (f, q) => PlaceTarget(f, q, median: false));
        var (meshUndewarpedMedianX, meshUndewarpedMedianY) = Rms(frames, m, (f, q) => (0, 0), (f, q) => PlaceTarget(f, q, median: true));

        var (width, height) = (stream.Width, stream.Height);
        var shifts = new AlignmentPointShift[n];
        var readings = ImmutableArray.CreateBuilder<DewarpReading>(pools.Count * 2);
        foreach (var pool in pools)
        {
            foreach (var median in new[] { false, true })
            {
                var (rx, ry) = median
                    ? Rms(frames, n, (f, p) => tracks.Warp(f, p, pool, medianGeometry: true), (f, p) => (trueX[(f * n) + p], trueY[(f * n) + p]))
                    : Rms(frames, n, (f, p) => tracks.Warp(f, p, pool, medianGeometry: false), (f, p) => (trueX[(f * n) + p] - refX[p], trueY[(f * n) + p] - refY[p]));
                // The mesh each frame is warped by, sampled at the places, less the frame's global shift: its local warp.
                var applied = new (double X, double Y)[frames * m];
                for (var f = 0; f < frames; f++)
                {
                    var (gx, gy) = tracks.GlobalShift(f);
                    tracks.Points(f, pool, median, shifts);
                    var mesh = DisplacementMesh.Build(width, height, MathF.Round((float)gx), MathF.Round((float)gy), shifts, options.MeshNodeSpacing, options.MeshInfluence);
                    for (var q = 0; q < m; q++)
                    {
                        var (ox, oy) = mesh.Sample(places[q].X, places[q].Y);
                        applied[(f * m) + q] = (ox - gx, oy - gy);
                    }
                }
                var (mx, my) = Rms(frames, m, (f, q) => applied[(f * m) + q], (f, q) => PlaceTarget(f, q, median));
                var (tx, ty) = Rms(frames, n, (f, p) => tracks.Warp(f, p, pool, median), (f, p) => (trueX[(f * n) + p], trueY[(f * n) + p]));
                readings.Add(new DewarpReading(pool, median, rx, ry, mx, my, tx, ty));
            }
        }
        return new DewarpReport(n, frames, referenceIndex, undewarpedX, undewarpedY, undewarpedMedianX, undewarpedMedianY,
            m, meshUndewarpedX, meshUndewarpedY, meshUndewarpedMedianX, meshUndewarpedMedianY, readings.MoveToImmutable());
    }

    // The RMS a axis of the reading less the target, each frame's mean over its points taken out of both.
    private static (double X, double Y) Rms(int frames, int n, Func<int, int, (double X, double Y)> reading, Func<int, int, (double X, double Y)> target)
    {
        double sx = 0, sy = 0;
        var (dx, dy) = (new double[n], new double[n]);
        for (var f = 0; f < frames; f++)
        {
            double mx = 0, my = 0;
            for (var p = 0; p < n; p++)
            {
                var (rx, ry) = reading(f, p);
                var (tx, ty) = target(f, p);
                (dx[p], dy[p]) = (rx - tx, ry - ty);
                mx += dx[p];
                my += dy[p];
            }
            (mx, my) = (mx / n, my / n);
            for (var p = 0; p < n; p++)
            {
                sx += (dx[p] - mx) * (dx[p] - mx);
                sy += (dy[p] - my) * (dy[p] - my);
            }
        }
        var count = (double)frames * n;
        return (Math.Sqrt(sx / count), Math.Sqrt(sy / count));
    }
}
