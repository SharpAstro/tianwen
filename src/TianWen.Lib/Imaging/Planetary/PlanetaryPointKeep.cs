using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Geometry;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Each alignment point keeping its own best frames (<see cref="PlanetaryStackOptions.PointKeep"/>, #1350; Strata's Warp+): every
/// candidate frame scored at every point by its patch's gain in a trous band 2 against the stacked reference
/// (<see cref="PlanetaryPointQuality.BandGain"/>, the best local estimator #1071 measured), each point keeping its best share of the
/// candidates, and a frame folded with a weight a pixel that is the share of the points about it (a tent reaching one point spacing, so
/// between neighbours only: reaching the mesh's 48 px it was a vote of a dozen points' sets) that kept it. Where no point reaches, the sky,
/// every candidate counts.
/// </summary>
internal sealed class PlanetaryPointKeep
{
    /// <summary>The patch a point is scored over, its band and the central square the band is read in: #1071's measurement's.</summary>
    internal const int Patch = 32;
    internal const int Band = 2;
    internal const int Inner = 16;

    private readonly ImmutableArray<PixelPoint> _points;
    private readonly ImmutableArray<float[]> _references;
    private readonly double _fraction;
    private readonly Dictionary<int, int> _row = new Dictionary<int, int>();
    private readonly double[] _scores;
    private bool[]? _kept;

    // Each point's tent over the output, normalised by the sum of every point's there: a box and its values.
    private readonly (int X0, int Y0, int W, int H, float[] Values)[] _tents;
    private readonly float[,] _outside;
    private readonly float[,] _map;

    internal PlanetaryPointKeep(ImmutableArray<PixelPoint> points, ImmutableArray<float[]> references, ImmutableArray<int> candidates, double fraction,
        int width, int height, float reach)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(references.Length, points.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fraction, 1);
        _points = points;
        _references = references;
        _fraction = fraction;
        for (var i = 0; i < candidates.Length; i++)
        {
            _row[candidates[i]] = i;
        }
        _scores = new double[candidates.Length * points.Length];
        Array.Fill(_scores, double.NaN);

        // A tent of radius `reach` about each point; where any reaches, each point's share of their sum; where none does, the outside.
        var sum = new float[height, width];
        var raw = new (int X0, int Y0, int W, int H, float[] Values)[points.Length];
        var r = Math.Max(1, (int)MathF.Ceiling(reach));
        for (var p = 0; p < points.Length; p++)
        {
            var (x0, y0) = (Math.Max(0, points[p].X - r), Math.Max(0, points[p].Y - r));
            var (x1, y1) = (Math.Min(width - 1, points[p].X + r), Math.Min(height - 1, points[p].Y + r));
            var (w, h) = (Math.Max(0, x1 - x0 + 1), Math.Max(0, y1 - y0 + 1));
            var values = new float[w * h];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var (dx, dy) = (x0 + x - points[p].X, y0 + y - points[p].Y);
                    var t = 1f - (MathF.Sqrt((dx * dx) + (dy * dy)) / reach);
                    if (t > 0)
                    {
                        values[(y * w) + x] = t;
                        sum[y0 + y, x0 + x] += t;
                    }
                }
            }
            raw[p] = (x0, y0, w, h, values);
        }
        _outside = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                _outside[y, x] = sum[y, x] > 0 ? 0f : 1f;
            }
        }
        for (var p = 0; p < raw.Length; p++)
        {
            var (x0, y0, w, h, values) = raw[p];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var s = sum[y0 + y, x0 + x];
                    values[(y * w) + x] = s > 0 ? values[(y * w) + x] / s : 0f;
                }
            }
        }
        _tents = raw;
        _map = new float[height, width];
    }

    /// <summary>The points, in the matcher's order.</summary>
    internal ImmutableArray<PixelPoint> Points => _points;

    /// <summary>How many frames every point chose from: those the global keep selected.</summary>
    internal int Candidates => _row.Count;

    /// <summary>How many frames each point keeps of the candidates.</summary>
    internal int KeptEach => Math.Max(1, (int)Math.Round(_fraction * _row.Count));

    /// <summary>Each point's reference patch, cut from the stacked reference (the output's grid) about the point.</summary>
    internal static ImmutableArray<float[]> ReferencePatches(Image reference, ImmutableArray<PixelPoint> points)
    {
        var luma = Luma(reference);
        var builder = ImmutableArray.CreateBuilder<float[]>(points.Length);
        foreach (var point in points)
        {
            var patch = new float[Patch * Patch];
            PlanetaryPointQuality.Cut(luma, reference.Width, reference.Height, point.X, point.Y, Patch, patch);
            builder.Add(patch);
        }
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// A frame's score at every point: its patch about the point, read through <paramref name="mesh"/> onto the output's grid (and relit
    /// as the fold relights it), its gain in band 2 against the point's reference patch. In the points' order; NaN where it reads none.
    /// </summary>
    internal double[] Score(Image frame, DisplacementMesh mesh)
    {
        var luma = Luma(frame);
        var scores = new double[_points.Length];
        var patch = new float[Patch * Patch];
        for (var p = 0; p < _points.Length; p++)
        {
            CutThroughMesh(luma, frame.Width, frame.Height, mesh, _points[p], patch);
            scores[p] = PlanetaryPointQuality.BandGain(patch, _references[p], Patch, Band, Inner);
        }
        return scores;
    }

    /// <summary>Records frame <paramref name="index"/>'s scores (<see cref="Score"/>).</summary>
    internal void Record(int index, double[] scores)
    {
        var row = _row[index];
        scores.AsSpan().CopyTo(_scores.AsSpan(row * _points.Length, _points.Length));
    }

    /// <summary>Each point keeps its best <see cref="KeptEach"/> candidates by score; a score that is not a number is never kept.</summary>
    internal void Decide()
    {
        var (frames, points) = (_row.Count, _points.Length);
        var kept = new bool[frames * points];
        var column = new double[frames];
        var keep = Math.Min(KeptEach, frames);
        for (var p = 0; p < points; p++)
        {
            var n = 0;
            for (var f = 0; f < frames; f++)
            {
                if (!double.IsNaN(_scores[(f * points) + p]))
                {
                    column[n++] = _scores[(f * points) + p];
                }
            }
            if (n == 0)
            {
                continue;
            }
            Array.Sort(column, 0, n);
            var threshold = column[Math.Max(0, n - keep)];
            for (var f = 0; f < frames; f++)
            {
                var s = _scores[(f * points) + p];
                kept[(f * points) + p] = !double.IsNaN(s) && s >= threshold;
            }
        }
        _kept = kept;
    }

    /// <summary>Whether any point kept frame <paramref name="index"/>, or the sky alone takes it (no point reaches every pixel).</summary>
    internal bool Folds(int index)
    {
        var kept = _kept ?? throw new InvalidOperationException("The points have not decided yet.");
        var row = _row[index];
        for (var p = 0; p < _points.Length; p++)
        {
            if (kept[(row * _points.Length) + p])
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>How many candidates any point kept.</summary>
    internal int FramesKept()
    {
        var count = 0;
        foreach (var index in _row.Keys)
        {
            count += Folds(index) ? 1 : 0;
        }
        return count;
    }

    /// <summary>
    /// Frame <paramref name="index"/>'s weight a pixel of the output: the share of the points about it that kept the frame, and one
    /// where no point reaches. One map, refilled for every frame: it holds until the next call.
    /// </summary>
    internal float[,] WeightFor(int index)
    {
        var kept = _kept ?? throw new InvalidOperationException("The points have not decided yet.");
        var row = _row[index];
        Array.Copy(_outside, _map, _outside.Length);
        for (var p = 0; p < _points.Length; p++)
        {
            if (!kept[(row * _points.Length) + p])
            {
                continue;
            }
            var (x0, y0, w, h, values) = _tents[p];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    _map[y0 + y, x0 + x] += values[(y * w) + x];
                }
            }
        }
        return _map;
    }

    // A patch about `point` on the output's grid, each sample read from the frame where the mesh maps it, relit as the fold relights it.
    private static void CutThroughMesh(ReadOnlySpan<float> luma, int width, int height, DisplacementMesh mesh, PixelPoint point, Span<float> into)
    {
        var (x0, y0) = (point.X - (Patch / 2), point.Y - (Patch / 2));
        for (var j = 0; j < Patch; j++)
        {
            for (var i = 0; i < Patch; i++)
            {
                var (x, y) = (x0 + i, y0 + j);
                var (ox, oy) = mesh.Sample(x, y);
                into[(j * Patch) + i] = Bilinear(luma, width, height, x + ox, y + oy) * mesh.RelightAt(x, y);
            }
        }
    }

    private static float Bilinear(ReadOnlySpan<float> plane, int width, int height, float x, float y)
    {
        var (ix, iy) = ((int)MathF.Floor(x), (int)MathF.Floor(y));
        var (tx, ty) = (x - ix, y - iy);
        var top = (At(plane, width, height, ix, iy) * (1 - tx)) + (At(plane, width, height, ix + 1, iy) * tx);
        var bottom = (At(plane, width, height, ix, iy + 1) * (1 - tx)) + (At(plane, width, height, ix + 1, iy + 1) * tx);
        return (top * (1 - ty)) + (bottom * ty);
    }

    private static float At(ReadOnlySpan<float> plane, int width, int height, int x, int y)
        => x >= 0 && y >= 0 && x < width && y < height ? plane[(y * width) + x] : 0f;

    // The frame's channels averaged, row-major: what a point is scored on.
    private static float[] Luma(Image image)
    {
        var (w, h, channels) = (image.Width, image.Height, image.ChannelCount);
        var luma = new float[w * h];
        var inv = 1f / channels;
        for (var c = 0; c < channels; c++)
        {
            var plane = image.GetChannelSpan(c);
            for (var i = 0; i < luma.Length; i++)
            {
                luma[i] += plane[i] * inv;
            }
        }
        return luma;
    }
}
