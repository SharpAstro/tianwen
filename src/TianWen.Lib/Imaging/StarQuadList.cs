using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace TianWen.Lib.Imaging;

public sealed class StarQuadList : IReadOnlyList<StarQuad>
{
    private readonly List<StarQuad> _quads;

    public StarQuadList(IEnumerable<StarQuad> quads)
    {
        _quads = [.. quads];
        _quads.Sort();
    }

    /// <summary>
    /// Builds the quads of a star field from detected stars; only the centroids are read.
    /// </summary>
    /// <remarks>
    /// Delegates to the position overload rather than carrying its own copy of the quad
    /// construction: the two callers differ only in where the coordinates come from (detected stars
    /// here, projected catalog positions for the plate solver's scale recovery), and a second copy
    /// of this geometry is exactly the kind of duplication that lets one side drift. The buffer is
    /// pooled, so the stacking path -- which reaches this once per frame per K via
    /// <c>SortedStarList.FindQuadsAsync</c>, never per pixel -- allocates nothing extra.
    /// </remarks>
    public StarQuadList(Span<ImagedStar> stars)
    {
        var rented = ArrayPool<Vector2>.Shared.Rent(stars.Length);
        try
        {
            for (var i = 0; i < stars.Length; i++)
            {
                rented[i] = new Vector2(stars[i].XCentroid, stars[i].YCentroid);
            }
            _quads = Build(rented.AsSpan(0, stars.Length));
        }
        finally
        {
            ArrayPool<Vector2>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Builds the quads of a star field from bare positions.
    /// </summary>
    /// <remarks>
    /// <paramref name="stars"/> MUST be sorted by X. The three-nearest-neighbour search is
    /// bounded by an INDEX window rather than a spatial one, so an unsorted input has it looking in
    /// the wrong part of the frame and the descriptors come out of unrelated stars -- silently, in
    /// that the quads are well-formed and simply do not correspond to anything.
    /// </remarks>
    public StarQuadList(ReadOnlySpan<Vector2> stars)
    {
        _quads = Build(stars);
    }

    private static List<StarQuad> Build(ReadOnlySpan<Vector2> stars)
    {
        var tolerance = (int)MathF.Round(0.5f * MathF.Sqrt(stars.Length));
        var quads = new List<StarQuad>(stars.Length);

        int j_distance1 = 0, j_distance2 = 0, j_distance3 = 0;

        for (int i = 0; i < stars.Length; i++)
        {
            float distance1 = float.MaxValue, distance2 = float.MaxValue, distance3 = float.MaxValue;

            int Sstart = Math.Max(0, i - (stars.Length / tolerance));
            int Send = Math.Min(stars.Length - 1, i + (stars.Length / tolerance));

            for (int j = Sstart; j <= Send; j++)
            {
                // not the first star
                if (j != i)
                {
                    float distY = (stars[j].Y - stars[i].Y) * (stars[j].Y - stars[i].Y);
                    if (distY < distance3) // pre-check to increase processing speed by a small amount
                    {
                        float distance = (stars[j].X - stars[i].X) * (stars[j].X - stars[i].X) + distY;
                        if (distance > 1) // not an identical star
                        {
                            if (distance < distance1)
                            {
                                distance3 = distance2;
                                j_distance3 = j_distance2;
                                distance2 = distance1;
                                j_distance2 = j_distance1;
                                distance1 = distance;
                                j_distance1 = j;
                            }
                            else if (distance < distance2)
                            {
                                distance3 = distance2;
                                j_distance3 = j_distance2;
                                distance2 = distance;
                                j_distance2 = j;
                            }
                            else if (distance < distance3)
                            {
                                distance3 = distance;
                                j_distance3 = j;
                            }
                        }
                    }
                }
            }

            float x1 = stars[i].X, y1 = stars[i].Y;
            float x2 = stars[j_distance1].X, y2 = stars[j_distance1].Y;
            float x3 = stars[j_distance2].X, y3 = stars[j_distance2].Y;
            float x4 = stars[j_distance3].X, y4 = stars[j_distance3].Y;

            float xt = (x1 + x2 + x3 + x4) * 0.25f;
            float yt = (y1 + y2 + y3 + y4) * 0.25f;

            bool identical_quad = false;
            for (int k = 0; k < quads.Count; k++)
            {
                if (MathF.Abs(xt - quads[k].X) < 1 && MathF.Abs(yt - quads[k].Y) < 1)
                {
                    identical_quad = true;
                    break;
                }
            }

            if (!identical_quad)
            {
                Span<float> dists = [
                    MathF.Sqrt(distance1),
                    MathF.Sqrt(distance2),
                    MathF.Sqrt(distance3),
                    MathF.Sqrt((x2 - x3) * (x2 - x3) + (y2 - y3) * (y2 - y3)),
                    MathF.Sqrt((x2 - x4) * (x2 - x4) + (y2 - y4) * (y2 - y4)),
                    MathF.Sqrt((x3 - x4) * (x3 - x4) + (y3 - y4) * (y3 - y4))
                ];

                dists.Sort();

                var largest = dists[^1];
                quads.Add(new StarQuad(
                    largest,
                    dists[^2] / largest,
                    dists[^3] / largest,
                    dists[^4] / largest,
                    dists[^5] / largest,
                    dists[^6] / largest,
                    xt,
                    yt
                ));
            }
        }

        // order by Dist1
        quads.Sort();
        return quads;
    }

    public StarQuad this[int index] => _quads[index];

    public int Count =>  _quads.Count;

    public IEnumerator<StarQuad> GetEnumerator() => _quads.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}