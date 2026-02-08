using System;
using System.Collections;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging;

public sealed class StarQuadList : IReadOnlyList<StarQuad>
{
    private readonly List<StarQuad> _quads;

    public StarQuadList(IEnumerable<StarQuad> quads)
    {
        _quads = [.. quads];
        _quads.Sort();
    }

    public StarQuadList(Span<ImagedStar> stars)
    {
        var tolerance = (int)MathF.Round(0.5f * MathF.Sqrt(stars.Length));
        _quads = new List<StarQuad>(stars.Length);

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
                    float distY = (stars[j].YCentroid - stars[i].YCentroid) * (stars[j].YCentroid - stars[i].YCentroid);
                    if (distY < distance3) // pre-check to increase processing speed by a small amount
                    {
                        float distance = (stars[j].XCentroid - stars[i].XCentroid) * (stars[j].XCentroid - stars[i].XCentroid) + distY;
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

            float x1 = stars[i].XCentroid, y1 = stars[i].YCentroid;
            float x2 = stars[j_distance1].XCentroid, y2 = stars[j_distance1].YCentroid;
            float x3 = stars[j_distance2].XCentroid, y3 = stars[j_distance2].YCentroid;
            float x4 = stars[j_distance3].XCentroid, y4 = stars[j_distance3].YCentroid;

            float xt = (x1 + x2 + x3 + x4) * 0.25f;
            float yt = (y1 + y2 + y3 + y4) * 0.25f;

            bool identical_quad = false;
            for (int k = 0; k < _quads.Count; k++)
            {
                if (MathF.Abs(xt - _quads[k].X) < 1 && MathF.Abs(yt - _quads[k].Y) < 1)
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
                _quads.Add(new StarQuad(
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
        _quads.Sort();
    }

    public StarQuad this[int index] => _quads[index];

    public int Count =>  _quads.Count;

    public IEnumerator<StarQuad> GetEnumerator() => _quads.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}