using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Pure-geometry helpers used by <see cref="StackingPipeline"/> to lay out
/// the integration canvas from a set of per-frame reference-space affine
/// transforms. No I/O, no <c>Image</c> access -- everything operates on
/// (transform, source-extent) pairs and returns rectangles. Lives next to
/// the pipeline because it has no other callers, but is fully testable in
/// isolation.
/// </summary>
internal static class CanvasGeometry
{
    /// <summary>
    /// Computes the axis-aligned union bounding box of every transformed
    /// source frame in reference space, plus a translation that shifts the
    /// box's top-left to canvas origin (0, 0). The reference frame itself
    /// is included in the union (corners at <c>(0, 0)</c> -- <c>(refW, refH)</c>),
    /// so the canvas is always at least as big as the reference.
    /// </summary>
    /// <param name="transforms">Per-frame reference-space affine matrices
    /// (source-grid -> reference-grid). Order doesn't matter -- output is
    /// purely a union.</param>
    /// <param name="refW">Reference frame width in pixels.</param>
    /// <param name="refH">Reference frame height in pixels.</param>
    public static (Matrix3x2 CanvasShift, int OriginX, int OriginY, int Width, int Height)
        ComputeUnionCanvas(IReadOnlyList<Matrix3x2> transforms, int refW, int refH)
    {
        float minX = 0f, minY = 0f;
        float maxX = refW, maxY = refH;
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            Vector2.Zero,
            new Vector2(refW, 0f),
            new Vector2(0f, refH),
            new Vector2(refW, refH),
        };
        for (var f = 0; f < transforms.Count; f++)
        {
            var mt = transforms[f];
            for (var i = 0; i < 4; i++)
            {
                var p = Vector2.Transform(corners[i], mt);
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        }
        var outOriginX = (int)MathF.Floor(minX);
        var outOriginY = (int)MathF.Floor(minY);
        var outWidth = (int)MathF.Ceiling(maxX) - outOriginX;
        var outHeight = (int)MathF.Ceiling(maxY) - outOriginY;
        var canvasShift = Matrix3x2.CreateTranslation(-outOriginX, -outOriginY);
        return (canvasShift, outOriginX, outOriginY, outWidth, outHeight);
    }

    /// <summary>
    /// For each transform, computes the canvas-space AABB ("footprint") of
    /// the warped source rectangle. Also computes the convex intersection
    /// of all warped quads and returns its AABB as <c>StatsRect</c> -- the
    /// region where every frame contributes, useful for stretch statistics.
    /// </summary>
    /// <param name="transforms">Per-frame reference-space affine matrices.
    /// Must already be left-multiplied or composed with <paramref name="canvasShift"/>
    /// by the caller -- no, wait, the caller passes the original ref-space
    /// transforms and we compose with <paramref name="canvasShift"/> here
    /// to mirror the production warp path.</param>
    /// <param name="canvasShift">Reference-to-canvas translation
    /// (from <see cref="ComputeUnionCanvas"/>).</param>
    public static (List<Rectangle> Footprints, Rectangle StatsRect)
        ComputeFootprintsAndStatsRect(
            IReadOnlyList<Matrix3x2> transforms,
            Matrix3x2 canvasShift,
            int srcW, int srcH,
            int canvasW, int canvasH)
    {
        var intersectionPoly = new List<Vector2>
        {
            new(0f,       0f),
            new(canvasW,  0f),
            new(canvasW,  canvasH),
            new(0f,       canvasH),
        };
        // Heap-allocate quad buffer once outside the loop (CA2014).
        var quad = new Vector2[4];
        var footprints = new List<Rectangle>(transforms.Count);
        for (var f = 0; f < transforms.Count; f++)
        {
            var t = transforms[f] * canvasShift;
            quad[0] = Vector2.Transform(new Vector2(0f,   0f),   t);
            quad[1] = Vector2.Transform(new Vector2(srcW, 0f),   t);
            quad[2] = Vector2.Transform(new Vector2(srcW, srcH), t);
            quad[3] = Vector2.Transform(new Vector2(0f,   srcH), t);
            float fxMin = quad[0].X, fxMax = quad[0].X;
            float fyMin = quad[0].Y, fyMax = quad[0].Y;
            for (var i = 1; i < 4; i++)
            {
                if (quad[i].X < fxMin) fxMin = quad[i].X;
                if (quad[i].X > fxMax) fxMax = quad[i].X;
                if (quad[i].Y < fyMin) fyMin = quad[i].Y;
                if (quad[i].Y > fyMax) fyMax = quad[i].Y;
            }
            var ffX = Math.Max(0, (int)MathF.Floor(fxMin));
            var ffY = Math.Max(0, (int)MathF.Floor(fyMin));
            var ffR = Math.Min(canvasW, (int)MathF.Ceiling(fxMax));
            var ffB = Math.Min(canvasH, (int)MathF.Ceiling(fyMax));
            footprints.Add(new Rectangle(ffX, ffY, Math.Max(0, ffR - ffX), Math.Max(0, ffB - ffY)));

            EnsureCwInCanvas(quad);
            intersectionPoly = ClipConvex(intersectionPoly, quad);
            if (intersectionPoly.Count == 0) break;
        }
        // If clipping bailed early, pad the remaining footprints with the
        // full canvas rect so the caller still gets one Rectangle per frame.
        while (footprints.Count < transforms.Count)
        {
            footprints.Add(new Rectangle(0, 0, canvasW, canvasH));
        }

        Rectangle statsRect;
        if (intersectionPoly.Count == 0)
        {
            statsRect = Rectangle.Empty;
        }
        else
        {
            float xMin = float.PositiveInfinity, yMin = float.PositiveInfinity;
            float xMax = float.NegativeInfinity, yMax = float.NegativeInfinity;
            foreach (var v in intersectionPoly)
            {
                if (v.X < xMin) xMin = v.X;
                if (v.X > xMax) xMax = v.X;
                if (v.Y < yMin) yMin = v.Y;
                if (v.Y > yMax) yMax = v.Y;
            }
            var rx = (int)MathF.Ceiling(xMin);
            var ry = (int)MathF.Ceiling(yMin);
            var rw = (int)MathF.Floor(xMax) - rx;
            var rh = (int)MathF.Floor(yMax) - ry;
            statsRect = new Rectangle(rx, ry, Math.Max(0, rw), Math.Max(0, rh));
        }
        return (footprints, statsRect);
    }

    /// <summary>
    /// Reverses <paramref name="quad"/> in place if its winding is CCW in
    /// canvas-y-down axes, so downstream <see cref="ClipConvex"/>, which
    /// expects CW-in-canvas, gets a consistently oriented quad. A
    /// 180-degree-rotated frame (post-meridian-flip) flips winding to
    /// CCW-in-canvas and needs reversing.
    /// </summary>
    private static void EnsureCwInCanvas(Span<Vector2> quad)
    {
        double area = 0;
        for (var i = 0; i < quad.Length; i++)
        {
            var a = quad[i];
            var b = quad[(i + 1) % quad.Length];
            area += (double)(b.X - a.X) * (b.Y + a.Y);
        }
        if (area > 0)
        {
            quad.Reverse();
        }
    }

    /// <summary>Sutherland-Hodgman polygon clip: clip <paramref name="subject"/>
    /// against the convex <paramref name="clip"/> polygon, both wound CW
    /// in canvas (y-down) axes. Returns the empty list when subject is
    /// fully outside clip.</summary>
    private static List<Vector2> ClipConvex(List<Vector2> subject, ReadOnlySpan<Vector2> clip)
    {
        var output = new List<Vector2>(subject);
        var input = new List<Vector2>(subject.Count + 2);
        for (var i = 0; i < clip.Length; i++)
        {
            if (output.Count == 0) return output;
            input.Clear();
            input.AddRange(output);
            output.Clear();
            var a = clip[i];
            var b = clip[(i + 1) % clip.Length];
            var edgeDx = b.X - a.X;
            var edgeDy = b.Y - a.Y;
            for (var j = 0; j < input.Count; j++)
            {
                var curr = input[j];
                var prev = input[(j - 1 + input.Count) % input.Count];
                var currSide = edgeDx * (curr.Y - a.Y) - edgeDy * (curr.X - a.X);
                var prevSide = edgeDx * (prev.Y - a.Y) - edgeDy * (prev.X - a.X);
                var currIn = currSide >= 0;
                var prevIn = prevSide >= 0;
                if (currIn)
                {
                    if (!prevIn) output.Add(IntersectSegment(prev, curr, a, b));
                    output.Add(curr);
                }
                else if (prevIn)
                {
                    output.Add(IntersectSegment(prev, curr, a, b));
                }
            }
        }
        return output;
    }

    private static Vector2 IntersectSegment(Vector2 p1, Vector2 p2, Vector2 a, Vector2 b)
    {
        var dx = p2.X - p1.X;
        var dy = p2.Y - p1.Y;
        var ex = b.X - a.X;
        var ey = b.Y - a.Y;
        var denom = dx * ey - dy * ex;
        var t = ((a.X - p1.X) * ey - (a.Y - p1.Y) * ex) / denom;
        return new Vector2(p1.X + t * dx, p1.Y + t * dy);
    }
}
