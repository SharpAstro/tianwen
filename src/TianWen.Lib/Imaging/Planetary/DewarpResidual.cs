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
    int MeshPlaces, double MeshUndewarpedX, double MeshUndewarpedY, double MeshUndewarpedMedianX, double MeshUndewarpedMedianY, ImmutableArray<DewarpReading> Readings)
{
    /// <summary>How each point reads the warp and what three interpolations of the readings leave, when a warp model was given.</summary>
    public DewarpInterpolation? Interpolation { get; init; }
}

/// <summary>
/// The warp a synthetic capture was drawn with, for a kriging that knows it (#1081): each axis's RMS, px, and the distance at which
/// its correlation falls to 1/e, px (<c>planetary-degrade --warp-rms --warp-length</c>, whose correlation is exp(-d^2 / L^2)).
/// </summary>
public readonly record struct WarpModel(double RmsPx, double LengthPx);

/// <summary>
/// How well each point reads the warp, and what three ways of carrying the readings to the disk leave of the true warp (#1081): on
/// the median geometry, each frame's own reading, every fitted thing fitted on the even frames and scored on the odd ones.
/// </summary>
/// <param name="WindowSigmaPx">The patch's window (Hann on both patches, so Hann squared) as a Gaussian's sigma, px: the scale a point
/// averages the warp over.</param>
/// <param name="SlopeAtPoint">The reading regressed on the true warp at the point (both axes).</param>
/// <param name="ErrorAtPointX">The reading less that slope times the truth there, RMS, across.</param>
/// <param name="ErrorAtPointY">Down.</param>
/// <param name="SlopeInWindow">The reading regressed on the true warp averaged over the patch's window: below one, the estimator
/// shrinks the shift it sees (Loefdahl 2010).</param>
/// <param name="ErrorInWindowX">The point error proper: the reading less that slope times the window's truth, RMS, across.</param>
/// <param name="ErrorInWindowY">Down.</param>
/// <param name="UndewarpedX">The true warp at the places, undewarped, across.</param>
/// <param name="UndewarpedY">Down.</param>
/// <param name="BlendX">Left by the stack's blend, its mesh at the options' spacing and reach, across.</param>
/// <param name="BlendY">Down.</param>
/// <param name="ScaledBlendX">Left by the blend times one gain an axis, fitted on the even frames: the blend's shape with its shrink
/// undone, across.</param>
/// <param name="ScaledBlendY">Down.</param>
/// <param name="GainX">The scaled blend's gain on the blend as the options build it, across: the blend's own gain times this is the
/// <see cref="PlanetaryStackOptions.MeshGain"/> that applies it.</param>
/// <param name="GainY">Down.</param>
/// <param name="KrigedX">Left by a kriging with the model's covariance seen through the window, the slope and the point error, the
/// errors independent, across.</param>
/// <param name="KrigedY">Down.</param>
/// <param name="KrigedOverlapX">The same kriging with two points' errors correlated as their windows overlap (patches closer than
/// their size share pixels), across.</param>
/// <param name="KrigedOverlapY">Down.</param>
/// <param name="CeilingX">Left by the best linear weights of the readings within one and a half correlation lengths of a place, fitted
/// against the truth: no linear interpolation of these readings does better. Across.</param>
/// <param name="CeilingY">Down.</param>
public sealed record DewarpInterpolation(double WindowSigmaPx, double SlopeAtPoint, double ErrorAtPointX, double ErrorAtPointY,
    double SlopeInWindow, double ErrorInWindowX, double ErrorInWindowY, double UndewarpedX, double UndewarpedY,
    double BlendX, double BlendY, double ScaledBlendX, double ScaledBlendY, double GainX, double GainY, double KrigedX, double KrigedY, double KrigedOverlapX, double KrigedOverlapY,
    double CeilingX, double CeilingY);

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
        => await MeasureAsync(stream, truth, options, pools, model: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// <see cref="MeasureAsync(IPlanetaryFrameStream, ImmutableArray{SyntheticWarp}, PlanetaryStackOptions, IReadOnlyList{double}, CancellationToken)"/>,
    /// and with <paramref name="model"/>, how each point reads the warp and what the blend, a kriging and the best linear weights
    /// leave (<see cref="DewarpReport.Interpolation"/>, #1081).
    /// </summary>
    public static async Task<DewarpReport> MeasureAsync(IPlanetaryFrameStream stream, ImmutableArray<SyntheticWarp> truth, PlanetaryStackOptions options,
        IReadOnlyList<double> pools, WarpModel? model, CancellationToken cancellationToken)
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
        var readings = ImmutableArray.CreateBuilder<DewarpReading>(pools.Count * 2);
        foreach (var pool in pools)
        {
            foreach (var median in new[] { false, true })
            {
                var (rx, ry) = median
                    ? Rms(frames, n, (f, p) => tracks.Warp(f, p, pool, medianGeometry: true), (f, p) => (trueX[(f * n) + p], trueY[(f * n) + p]))
                    : Rms(frames, n, (f, p) => tracks.Warp(f, p, pool, medianGeometry: false), (f, p) => (trueX[(f * n) + p] - refX[p], trueY[(f * n) + p] - refY[p]));
                // The mesh each frame is warped by, sampled at the places, less the frame's global shift: its local warp. A mesh costs
                // its nodes times its points (15 million terms a frame at 4 px nodes over 500 points), and each frame's is its own, so
                // the frames are built at once; the residual is summed afterwards in frame order.
                var applied = new (double X, double Y)[frames * m];
                ParallelFor.Run(frames, f => SampleMesh(tracks, f, pool, median, n, width, height, options, places, applied));
                var (mx, my) = Rms(frames, m, (f, q) => applied[(f * m) + q], (f, q) => PlaceTarget(f, q, median));
                var (tx, ty) = Rms(frames, n, (f, p) => tracks.Warp(f, p, pool, median), (f, p) => (trueX[(f * n) + p], trueY[(f * n) + p]));
                readings.Add(new DewarpReading(pool, median, rx, ry, mx, my, tx, ty));
            }
        }
        return new DewarpReport(n, frames, referenceIndex, undewarpedX, undewarpedY, undewarpedMedianX, undewarpedMedianY,
            m, meshUndewarpedX, meshUndewarpedY, meshUndewarpedMedianX, meshUndewarpedMedianY, readings.MoveToImmutable())
        {
            Interpolation = model is { } warpModel
                ? Interpolate(tracks, truth, reference, points, places, placeX, placeY, options, warpModel, width, height)
                : null,
        };
    }

    // How each point reads the warp, and what the blend, a kriging and the best linear weights leave of it at the places (#1081).
    private static DewarpInterpolation Interpolate(AlignmentPointTracks tracks, ImmutableArray<SyntheticWarp> truth, SyntheticWarp reference,
        AlignmentPointShift[] points, List<(float X, float Y)> places, double[] placeX, double[] placeY, PlanetaryStackOptions options,
        WarpModel model, int width, int height)
    {
        var (frames, n, m) = (tracks.Frames, points.Length, places.Count);
        // Each frame's readings on the median geometry, and the truth at each point, at the point and over the patch's window: Hann
        // on the frame's patch and on the reference's, so a point weighs the warp by the window squared.
        var (rx, ry) = (new double[frames * n], new double[frames * n]);
        var (tx, ty, wx, wy) = (new double[frames * n], new double[frames * n], new double[frames * n], new double[frames * n]);
        var size = options.AlignmentPatchSize;
        var weight = new double[size];
        double sumW = 0, sumW2 = 0;
        for (var i = 0; i < size; i++)
        {
            var hann = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (size - 1)));
            weight[i] = hann * hann;
            sumW += weight[i];
        }
        for (var i = 0; i < size; i++)
        {
            sumW2 += weight[i] * (i - ((size - 1) / 2.0)) * (i - ((size - 1) / 2.0));
        }
        var windowSigma = Math.Sqrt(sumW2 / sumW);
        ParallelFor.Run(frames, f =>
        {
            for (var p = 0; p < n; p++)
            {
                var k = (f * n) + p;
                (rx[k], ry[k]) = tracks.Warp(f, p, 0, medianGeometry: true);
                var (cx, cy) = (points[p].X - reference.OriginX, points[p].Y - reference.OriginY);
                (tx[k], ty[k]) = truth[f].AtWindow(cx, cy);
                double sx = 0, sy = 0;
                for (var j = 0; j < size; j++)
                {
                    for (var i = 0; i < size; i++)
                    {
                        var (dx, dy) = truth[f].AtWindow(cx + i - (size / 2), cy + j - (size / 2));
                        var w = weight[i] * weight[j];
                        sx += w * dx;
                        sy += w * dy;
                    }
                }
                (wx[k], wy[k]) = (sx / (sumW * sumW), sy / (sumW * sumW));
            }
        });
        // The global aligner takes each frame's mean warp out, so every comparison is of values less their frame's mean.
        Center(rx, frames, n);
        Center(ry, frames, n);
        Center(tx, frames, n);
        Center(ty, frames, n);
        Center(wx, frames, n);
        Center(wy, frames, n);
        var (slopeAtPoint, errorAtPointX, errorAtPointY) = Regress(rx, ry, tx, ty);
        var (slopeInWindow, errorInWindowX, errorInWindowY) = Regress(rx, ry, wx, wy);

        // The truth at the places, less each frame's mean over them, and the odd frames everything is scored on.
        var (px, py) = ((double[])placeX.Clone(), (double[])placeY.Clone());
        Center(px, frames, m);
        Center(py, frames, m);
        var odd = Enumerable.Range(0, frames).Where(f => f % 2 == 1).ToArray();
        (double X, double Y) Score(Func<int, int, (double X, double Y)> estimate)
            => Rms(odd.Length, m, (o, q) => estimate(odd[o], q), (o, q) => (px[(odd[o] * m) + q], py[(odd[o] * m) + q]));
        var (undewarpedX, undewarpedY) = Score((_, _) => (0, 0));

        // The stack's blend, as the mesh it builds.
        var blend = new (double X, double Y)[frames * m];
        ParallelFor.Run(odd.Length, o => SampleMesh(tracks, odd[o], 0, true, n, width, height, options, places, blend));
        var (blendX, blendY) = Score((f, q) => blend[(f * m) + q]);
        // The blend scaled by one gain an axis, fitted on the even frames, which needs their blend too.
        var even = Enumerable.Range(0, frames).Where(f => f % 2 == 0).ToArray();
        ParallelFor.Run(even.Length, e => SampleMesh(tracks, even[e], 0, true, n, width, height, options, places, blend));
        double bxt = 0, bxx = 0, byt = 0, byy = 0;
        foreach (var f in even)
        {
            for (var q = 0; q < m; q++)
            {
                var (bx, by) = blend[(f * m) + q];
                (bxt, bxx) = (bxt + (bx * px[(f * m) + q]), bxx + (bx * bx));
                (byt, byy) = (byt + (by * py[(f * m) + q]), byy + (by * by));
            }
        }
        var (gainX, gainY) = (bxx > 0 ? bxt / bxx : 0, byy > 0 ? byt / byy : 0);
        var (scaledBlendX, scaledBlendY) = Score((f, q) => (gainX * blend[(f * m) + q].X, gainY * blend[(f * m) + q].Y));

        // A simple kriging: the model's Gaussian covariance, sigma^2 exp(-d^2 / L^2), seen through the window (a Gaussian of
        // windowSigma) once between a place and a reading and twice between two readings, each reading the slope times its window's
        // warp plus the point error. Its weights are the same for every frame.
        var ell2 = model.LengthPx * model.LengthPx / 2;
        var s2 = windowSigma * windowSigma;
        var variance = model.RmsPx * model.RmsPx;
        var nugget = ((errorInWindowX * errorInWindowX) + (errorInWindowY * errorInWindowY)) / 2;
        var cov = new double[n * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                var d2 = Square(points[i].X - points[j].X) + Square(points[i].Y - points[j].Y);
                cov[(i * n) + j] = (slopeInWindow * slopeInWindow * variance * ell2 / (ell2 + (2 * s2)) * Math.Exp(-d2 / (2 * (ell2 + (2 * s2)))))
                    + (i == j ? nugget : 0);
            }
        }
        var cross = new double[n * m];
        for (var q = 0; q < m; q++)
        {
            for (var i = 0; i < n; i++)
            {
                var d2 = Square(places[q].X - points[i].X) + Square(places[q].Y - points[i].Y);
                cross[(i * m) + q] = slopeInWindow * variance * ell2 / (ell2 + s2) * Math.Exp(-d2 / (2 * (ell2 + s2)));
            }
        }
        // The same with the errors correlated as the windows overlap: two patches closer than their size read some of the same
        // pixels, their noise the same noise, weighted by the window squared on each, so the correlation is that weight's own
        // correlation at their offset, an axis at a time.
        var overlap = (double[])cov.Clone();
        double selfOverlap = 0;
        for (var i = 0; i < size; i++)
        {
            selfOverlap += weight[i] * weight[i];
        }
        double Overlap(double d)
        {
            var shift = (int)Math.Round(Math.Abs(d));
            if (shift >= size)
            {
                return 0;
            }
            double s = 0;
            for (var i = 0; i + shift < size; i++)
            {
                s += weight[i] * weight[i + shift];
            }
            return s / selfOverlap;
        }
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                if (i != j)
                {
                    overlap[(i * n) + j] += nugget * Overlap(points[i].X - points[j].X) * Overlap(points[i].Y - points[j].Y);
                }
            }
        }
        (double X, double Y) Apply(double[] solution, int f, int q)
        {
            double sx = 0, sy = 0;
            for (var i = 0; i < n; i++)
            {
                var w = solution[(i * m) + q];
                sx += w * rx[(f * n) + i];
                sy += w * ry[(f * n) + i];
            }
            return (sx, sy);
        }
        // cov^-1 cross, a column per place: place q's weight on reading i is solved[i * m + q].
        var solved = SolveSymmetric(cov, n, cross, m) ?? throw new InvalidOperationException("the kriging's covariance is not positive definite");
        var (krigedX, krigedY) = Score((f, q) => Apply(solved, f, q));
        var solvedOverlap = SolveSymmetric(overlap, n, cross, m) ?? throw new InvalidOperationException("the overlapping kriging's covariance is not positive definite");
        var (krigedOverlapX, krigedOverlapY) = Score((f, q) => Apply(solvedOverlap, f, q));

        // The ceiling: at each place, the least-squares weights of the readings within one and a half correlation lengths, fitted
        // against the truth there on the even frames, each axis on its own.
        var reach2 = Square(1.5 * model.LengthPx);
        var ceiling = new (double X, double Y)[frames * m];
        ParallelFor.Run(m, q =>
        {
            var near = Enumerable.Range(0, n).Where(i => Square(places[q].X - points[i].X) + Square(places[q].Y - points[i].Y) <= reach2).ToArray();
            if (near.Length == 0)
            {
                return;
            }
            var k = near.Length;
            foreach (var axisIsX in new[] { true, false })
            {
                var (r, t) = axisIsX ? (rx, px) : (ry, py);
                var normal = new double[k * k];
                var rhs = new double[k];
                foreach (var f in even)
                {
                    for (var a = 0; a < k; a++)
                    {
                        var ra = r[(f * n) + near[a]];
                        rhs[a] += ra * t[(f * m) + q];
                        for (var b = 0; b < k; b++)
                        {
                            normal[(a * k) + b] += ra * r[(f * n) + near[b]];
                        }
                    }
                }
                double trace = 0;
                for (var a = 0; a < k; a++)
                {
                    trace += normal[(a * k) + a];
                }
                for (var a = 0; a < k; a++)
                {
                    normal[(a * k) + a] += 1e-9 * trace / k;
                }
                if (SolveSymmetric(normal, k, rhs, 1) is not { } beta)
                {
                    continue;
                }
                foreach (var f in odd)
                {
                    double s = 0;
                    for (var a = 0; a < k; a++)
                    {
                        s += beta[a] * r[(f * n) + near[a]];
                    }
                    ceiling[(f * m) + q] = axisIsX ? (s, ceiling[(f * m) + q].Y) : (ceiling[(f * m) + q].X, s);
                }
            }
        });
        var (ceilingX, ceilingY) = Score((f, q) => ceiling[(f * m) + q]);

        return new DewarpInterpolation(windowSigma, slopeAtPoint, errorAtPointX, errorAtPointY, slopeInWindow, errorInWindowX, errorInWindowY,
            undewarpedX, undewarpedY, blendX, blendY, scaledBlendX, scaledBlendY, gainX, gainY, krigedX, krigedY, krigedOverlapX, krigedOverlapY, ceilingX, ceilingY);
    }

    private static double Square(double v) => v * v;

    // Frame `f`'s mesh, built from its points as the stack builds it, sampled at the places less the frame's global shift, into its
    // row of `applied`.
    private static void SampleMesh(AlignmentPointTracks tracks, int f, double pool, bool median, int n, int width, int height,
        PlanetaryStackOptions options, List<(float X, float Y)> places, (double X, double Y)[] applied)
    {
        var shifts = new AlignmentPointShift[n];
        var (gx, gy) = tracks.GlobalShift(f);
        tracks.Points(f, pool, median, shifts);
        var mesh = DisplacementMesh.Build(width, height, (float)gx, (float)gy, shifts, options.MeshNodeSpacing, options.MeshInfluence, residualGain: options.MeshGain);
        var m = places.Count;
        for (var q = 0; q < m; q++)
        {
            var (ox, oy) = mesh.Sample(places[q].X, places[q].Y);
            applied[(f * m) + q] = (ox - gx, oy - gy);
        }
    }

    // Each frame's values (a row of `n`) less their mean.
    private static void Center(double[] values, int frames, int n)
    {
        for (var f = 0; f < frames; f++)
        {
            var row = values.AsSpan(f * n, n);
            double mean = 0;
            foreach (var v in row)
            {
                mean += v;
            }
            mean /= n;
            for (var i = 0; i < n; i++)
            {
                row[i] -= mean;
            }
        }
    }

    // The reading regressed on a truth over both axes through the origin, and the RMS left a axis.
    private static (double Slope, double ErrorX, double ErrorY) Regress(double[] rx, double[] ry, double[] tx, double[] ty)
    {
        double rt = 0, tt = 0;
        for (var i = 0; i < rx.Length; i++)
        {
            rt += (rx[i] * tx[i]) + (ry[i] * ty[i]);
            tt += (tx[i] * tx[i]) + (ty[i] * ty[i]);
        }
        var slope = tt > 0 ? rt / tt : 0;
        double ex = 0, ey = 0;
        for (var i = 0; i < rx.Length; i++)
        {
            ex += Square(rx[i] - (slope * tx[i]));
            ey += Square(ry[i] - (slope * ty[i]));
        }
        return (slope, Math.Sqrt(ex / rx.Length), Math.Sqrt(ey / ry.Length));
    }

    // Solves the symmetric positive definite `a` (n by n, row-major) against `columns` right-hand sides (b, n by columns, row-major)
    // by Cholesky; null when `a` is not positive definite. `a` is overwritten.
    private static double[]? SolveSymmetric(double[] a, int n, double[] b, int columns)
    {
        for (var j = 0; j < n; j++)
        {
            var d = a[(j * n) + j];
            for (var k = 0; k < j; k++)
            {
                d -= a[(j * n) + k] * a[(j * n) + k];
            }
            if (!(d > 0))
            {
                return null;
            }
            var l = Math.Sqrt(d);
            a[(j * n) + j] = l;
            for (var i = j + 1; i < n; i++)
            {
                var s = a[(i * n) + j];
                for (var k = 0; k < j; k++)
                {
                    s -= a[(i * n) + k] * a[(j * n) + k];
                }
                a[(i * n) + j] = s / l;
            }
        }
        var x = (double[])b.Clone();
        for (var c = 0; c < columns; c++)
        {
            for (var i = 0; i < n; i++)
            {
                var s = x[(i * columns) + c];
                for (var k = 0; k < i; k++)
                {
                    s -= a[(i * n) + k] * x[(k * columns) + c];
                }
                x[(i * columns) + c] = s / a[(i * n) + i];
            }
            for (var i = n - 1; i >= 0; i--)
            {
                var s = x[(i * columns) + c];
                for (var k = i + 1; k < n; k++)
                {
                    s -= a[(k * n) + i] * x[(k * columns) + c];
                }
                x[(i * columns) + c] = s / a[(i * n) + i];
            }
        }
        return x;
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
