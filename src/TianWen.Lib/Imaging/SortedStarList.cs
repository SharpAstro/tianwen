using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging;

public class SortedStarList(StarList stars) : IReadOnlyList<ImagedStar>, IDisposable
{
    sealed class XCentroidComparer : IComparer<ImagedStar>
    {
        public int Compare(ImagedStar x, ImagedStar y) => x.XCentroid.CompareTo(y.XCentroid);
    }

    static readonly XCentroidComparer xCentroidComparer = new XCentroidComparer();

    private readonly ImagedStar[] _stars = SortStarList(stars, xCentroidComparer);
    private List<StarQuad>? _quads;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static ImagedStar[] SortStarList(StarList stars, IComparer<ImagedStar> comparer)
    {
        var sortedStars = stars.ToArray();
        Array.Sort(sortedStars, comparer);
        return sortedStars;
    }


    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    /// <remarks>From unit_star_align.pas:find_quads</remarks>
    public async Task<IReadOnlyList<StarQuad>> FindQuadsAsync(CancellationToken cancellationToken = default)
    {
        var quads = Interlocked.CompareExchange(ref _quads, null, null);
        if (quads is not null)
        {
            return quads;
        }

        using var @lock = await _lock.AcquireLockAsync(cancellationToken);

        if (quads is not null)
        {
            return quads;
        }

        return _quads = DoFindQuads();
    }

    private List<StarQuad> DoFindQuads()
    {
        var tolerance = (int)MathF.Round(0.5f * MathF.Sqrt(_stars.Length));
        var quadStarDistances = new List<StarQuad>(_stars.Length);

        int j_distance1 = 0, j_distance2 = 0, j_distance3 = 0;

        for (int i = 0; i < _stars.Length; i++)
        {
            float distance1 = float.MaxValue, distance2 = float.MaxValue, distance3 = float.MaxValue;

            int Sstart = Math.Max(0, i - (_stars.Length / tolerance));
            int Send = Math.Min(_stars.Length - 1, i + (_stars.Length / tolerance));

            for (int j = Sstart; j <= Send; j++)
            {
                // not the first star
                if (j != i)
                {
                    float distY = (_stars[j].YCentroid - _stars[i].YCentroid) * (_stars[j].YCentroid - _stars[i].YCentroid);
                    if (distY < distance3) // pre-check to increase processing speed by a small amount
                    {
                        float distance = (_stars[j].XCentroid - _stars[i].XCentroid) * (_stars[j].XCentroid - _stars[i].XCentroid) + distY;
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

            float x1 = _stars[i].XCentroid, y1 = _stars[i].YCentroid;
            float x2 = _stars[j_distance1].XCentroid, y2 = _stars[j_distance1].YCentroid;
            float x3 = _stars[j_distance2].XCentroid, y3 = _stars[j_distance2].YCentroid;
            float x4 = _stars[j_distance3].XCentroid, y4 = _stars[j_distance3].YCentroid;

            float xt = (x1 + x2 + x3 + x4) * 0.25f;
            float yt = (y1 + y2 + y3 + y4) * 0.25f;

            bool identical_quad = false;
            for (int k = 0; k < quadStarDistances.Count; k++)
            {
                if (MathF.Abs(xt - quadStarDistances[k].X) < 1 && MathF.Abs(yt - quadStarDistances[k].Y) < 1)
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

                var largest = dists[5];
                quadStarDistances.Add(new StarQuad(
                    largest,
                    dists[4] / largest,
                    dists[3] / largest,
                    dists[2] / largest,
                    dists[1] / largest,
                    dists[0] / largest,
                    xt,
                    yt
                ));
            }
        }

        return quadStarDistances;
    }

    public async Task<StarReferenceTable?> FindFitAsync(SortedStarList other, int minimumCount = 6, float quadTolerance = 0.008f)
        => StarReferenceTable.FindFit(await FindQuadsAsync(), await other.FindQuadsAsync(), minimumCount, quadTolerance);

    public async Task<Matrix3x2?> FindOffsetAndRotationAsync(SortedStarList other, int minimumCount = 6, float quadTolerance = 0.008f)
    {
        var starRefTable = await FindFitAsync(other, minimumCount, quadTolerance);
        return starRefTable is { } ? await starRefTable.FindOffsetAndRotationAsync() : null;
    }

    public int Count => _stars.Length;

    public ImagedStar this[int index] => _stars[index];

    public IEnumerator<ImagedStar> GetEnumerator() => _stars.GetEnumerable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _stars.GetEnumerator();

    public void Dispose()
    {
        _lock.Dispose();
        
        GC.SuppressFinalize(this);
    }

    public static implicit operator SortedStarList(StarList stars) => new SortedStarList(stars);
}