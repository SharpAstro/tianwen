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
    private StarQuadList? _quads;
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
    public async Task<StarQuadList> FindQuadsAsync(CancellationToken cancellationToken = default)
    {
        var quads = Interlocked.CompareExchange(ref _quads, null, null);
        if (quads is not null)
        {
            return quads;
        }

        using var @lock = await _lock.AcquireLockAsync(cancellationToken);

        return _quads ??= new StarQuadList(_stars);
    }

    public async Task<StarReferenceTable?> FindFitAsync(SortedStarList other, int minimumCount = 6, float quadTolerance = 0.008f)
        => StarReferenceTable.FindFit(await FindQuadsAsync(), await other.FindQuadsAsync(), minimumCount, quadTolerance);

    public async Task<Matrix3x2?> FindOffsetAndRotationAsync(SortedStarList other, int minimumCount = 6, float quadTolerance = 0.008f, float solutionTolerance = 1e-3f)
    {
        var starRefTable = await FindFitAsync(other, minimumCount, quadTolerance);
        return starRefTable is { } ? await starRefTable.FindOffsetAndRotationAsync(solutionTolerance) : null;
    }

    public async Task<(Matrix3x2? Solution, float QuadTolerance)> FindOffsetAndRotationWithRetryAsync(SortedStarList other, int minimumCount = 6, float solutionTolerance = 1e-3f)
    {
        var tries = 0;
        var stepSize = 0.0001f;
        for (var quadTolerance = stepSize; quadTolerance < 1.0f; quadTolerance += stepSize)
        {
            var starRefTable = await FindFitAsync(other, minimumCount, quadTolerance);
            if (starRefTable is { } && await starRefTable.FindOffsetAndRotationAsync(solutionTolerance) is { } solution)
            {
                return (solution, quadTolerance);
            }

            if ((++tries) % 10 == 0)
            {
                stepSize *= 2;
            }
        }

        return (null, float.NaN);
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