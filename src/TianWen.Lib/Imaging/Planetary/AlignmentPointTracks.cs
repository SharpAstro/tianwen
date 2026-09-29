using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Every alignment point's local warp over every frame of a capture, read once in capture order: the displacement the matcher
/// finds at the point, less the frame's own global shift (docs/plans/planetary-restoration.md, R5 part 2). From it, a frame's
/// points can be taken <b>pooled</b> over the frames either side, since a warp stays coherent for a few frames while each
/// frame's match is as uncertain as the warp is large, and on the <b>median geometry</b>, each point's median warp over the
/// frames taken out, so the stack lands where a feature lies on average rather than where the reference frame's own warp put it
/// (the user's centroid idea).
/// </summary>
internal sealed class AlignmentPointTracks
{
    private readonly (double Dx, double Dy)[] _global;
    private readonly float[] _warpX;
    private readonly float[] _warpY;
    private readonly float[] _medianX;
    private readonly float[] _medianY;
    private readonly AlignmentPointShift[] _points;

    private AlignmentPointTracks((double Dx, double Dy)[] global, float[] warpX, float[] warpY, AlignmentPointShift[] points)
    {
        (_global, _warpX, _warpY, _points) = (global, warpX, warpY, points);
        var n = points.Length;
        (_medianX, _medianY) = (new float[n], new float[n]);
        var column = new float[global.Length];
        for (var p = 0; p < n; p++)
        {
            for (var f = 0; f < global.Length; f++)
            {
                column[f] = warpX[(f * n) + p];
            }
            _medianX[p] = StatisticsHelper.NthSmallest(column, column.Length / 2);
            for (var f = 0; f < global.Length; f++)
            {
                column[f] = warpY[(f * n) + p];
            }
            _medianY[p] = StatisticsHelper.NthSmallest(column, column.Length / 2);
        }
    }

    /// <summary>
    /// Tracks from warps already read: frame f's global shift and each point's warp over it, frame-major (point p of frame f at
    /// <c>f * points.Length + p</c>), for a test that knows them.
    /// </summary>
    internal static AlignmentPointTracks FromWarps((double Dx, double Dy)[] global, float[] warpX, float[] warpY, AlignmentPointShift[] points)
    {
        return new AlignmentPointTracks(global, warpX, warpY, points);
    }

    /// <summary>The frames tracked.</summary>
    public int Frames => _global.Length;

    /// <summary>Reads every frame of <paramref name="stream"/>: its global shift by <paramref name="aligner"/> and its points' warp by <paramref name="matcher"/>.</summary>
    public static async Task<AlignmentPointTracks> MeasureAsync(IPlanetaryFrameStream stream, GlobalAligner aligner, AlignmentPointMatcher matcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var (frames, n) = (stream.FrameCount, matcher.AlignmentPoints.Length);
        var global = new (double Dx, double Dy)[frames];
        var (warpX, warpY) = (new float[frames * n], new float[frames * n]);
        var shifts = new AlignmentPointShift[n];
        for (var f = 0; f < frames; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await stream.LoadAsync(f, cancellationToken).ConfigureAwait(false);
            try
            {
                var shift = aligner.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                global[f] = (shift.Dx, shift.Dy);
                matcher.Match(frame, (float)shift.Dx, (float)shift.Dy, shifts);
                // A point's residual is over the rounded global shift; its warp is over the frame's own, unrounded.
                var (fracX, fracY) = (shift.Dx - Math.Round(shift.Dx), shift.Dy - Math.Round(shift.Dy));
                for (var p = 0; p < n; p++)
                {
                    warpX[(f * n) + p] = (float)(shifts[p].ResidualX - fracX);
                    warpY[(f * n) + p] = (float)(shifts[p].ResidualY - fracY);
                }
            }
            finally
            {
                frame.Release();
            }
        }
        var points = new AlignmentPointShift[n];
        for (var p = 0; p < n; p++)
        {
            points[p] = new AlignmentPointShift(matcher.AlignmentPoints[p].X, matcher.AlignmentPoints[p].Y, 0, 0);
        }
        return new AlignmentPointTracks(global, warpX, warpY, points);
    }

    /// <summary>Frame <paramref name="frame"/>'s global shift, as the aligner read it.</summary>
    public (double Dx, double Dy) GlobalShift(int frame) => _global[frame];

    /// <summary>
    /// Frame <paramref name="frame"/>'s points as residuals over its rounded global shift, as <see cref="DisplacementMesh.Build"/>
    /// takes them: each point's warp averaged over the frames within three <paramref name="sigmaFrames"/> of it, Gaussian-weighted
    /// (the frame's own when zero), less the point's median warp over every frame when <paramref name="medianGeometry"/>.
    /// </summary>
    public void Points(int frame, double sigmaFrames, bool medianGeometry, Span<AlignmentPointShift> destination)
    {
        var n = _points.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, n);
        var reach = sigmaFrames > 0 ? (int)Math.Ceiling(3 * sigmaFrames) : 0;
        var (first, last) = (Math.Max(0, frame - reach), Math.Min(_global.Length - 1, frame + reach));
        var (fracX, fracY) = (_global[frame].Dx - Math.Round(_global[frame].Dx), _global[frame].Dy - Math.Round(_global[frame].Dy));
        for (var p = 0; p < n; p++)
        {
            double sx = 0, sy = 0, weights = 0;
            for (var g = first; g <= last; g++)
            {
                var w = sigmaFrames > 0 ? Math.Exp(-0.5 * (g - frame) * (g - frame) / (sigmaFrames * sigmaFrames)) : 1;
                sx += w * _warpX[(g * n) + p];
                sy += w * _warpY[(g * n) + p];
                weights += w;
            }
            var (wx, wy) = (sx / weights, sy / weights);
            if (medianGeometry)
            {
                (wx, wy) = (wx - _medianX[p], wy - _medianY[p]);
            }
            destination[p] = _points[p] with { ResidualX = (float)(wx + fracX), ResidualY = (float)(wy + fracY) };
        }
    }
}
