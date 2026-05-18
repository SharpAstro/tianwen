using System;
using System.Collections;
using System.Collections.Concurrent;
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

    sealed class FluxDescComparer : IComparer<ImagedStar>
    {
        // Note the reversed argument order -- Flux descending = brightest first.
        public int Compare(ImagedStar x, ImagedStar y) => y.Flux.CompareTo(x.Flux);
    }

    static readonly XCentroidComparer xCentroidComparer = new XCentroidComparer();
    static readonly FluxDescComparer fluxDescComparer = new FluxDescComparer();

    // Sentinel for "use every star" so the cache key is a plain int.
    private const int UnlimitedKey = 0;

    private readonly ImagedStar[] _stars = SortStarList(stars, xCentroidComparer);

    // Per-K quad cache. maxStars=null path uses UnlimitedKey. The semaphore
    // serialises building so two concurrent callers with the same K don't both
    // pay the kNN scan; once cached, subsequent calls hit the dictionary lock-free.
    private readonly ConcurrentDictionary<int, StarQuadList> _quadsByK = new();
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static ImagedStar[] SortStarList(StarList stars, IComparer<ImagedStar> comparer)
    {
        var sortedStars = stars.ToArray();
        Array.Sort(sortedStars, comparer);
        return sortedStars;
    }


    /// <summary>
    /// Builds (and memoises) a <see cref="StarQuadList"/> from this frame's stars.
    /// </summary>
    /// <param name="maxStars">
    /// Optional cap on the number of stars used to construct quads. When set, the
    /// brightest <paramref name="maxStars"/> by <see cref="ImagedStar.Flux"/> are
    /// picked, then re-sorted by <see cref="ImagedStar.XCentroid"/> for the kNN
    /// scan. Bright stars are reproducible across detection-threshold variation
    /// between frames, so top-K quad signatures stay stable across the group --
    /// the all-stars path can produce different quads on the same physical star
    /// when faint neighbours appear/disappear, killing the qt=0.020 raw-pair count
    /// even on co-pointing frames.
    /// </param>
    /// <remarks>From unit_star_align.pas:find_quads. Top-K selection is the same
    /// trick astrometry.net / ASTAP use to stabilise the quad fingerprint.</remarks>
    public async Task<StarQuadList> FindQuadsAsync(int? maxStars = null, CancellationToken cancellationToken = default)
    {
        var key = maxStars ?? UnlimitedKey;
        if (_quadsByK.TryGetValue(key, out var cached))
        {
            return cached;
        }

        using var @lock = await _lock.AcquireLockAsync(cancellationToken);

        // Re-check under the lock: another caller may have built the same key
        // while we were waiting on the semaphore.
        if (_quadsByK.TryGetValue(key, out cached))
        {
            return cached;
        }

        ImagedStar[] source;
        if (maxStars is int k && _stars.Length > k)
        {
            // Top-K by Flux desc, then re-sort by X so the StarQuadList ctor's
            // spatial-strip scan works on the subset.
            source = new ImagedStar[_stars.Length];
            Array.Copy(_stars, source, _stars.Length);
            Array.Sort(source, fluxDescComparer);
            var subset = new ImagedStar[k];
            Array.Copy(source, subset, k);
            Array.Sort(subset, xCentroidComparer);
            source = subset;
        }
        else
        {
            source = _stars;
        }

        var built = new StarQuadList(source);
        _quadsByK[key] = built;
        return built;
    }

    public async Task<StarReferenceTable?> FindFitAsync(SortedStarList other, int minimumCount = 6, float quadTolerance = 0.008f, int? maxStars = null)
        => StarReferenceTable.FindFit(await FindQuadsAsync(maxStars), await other.FindQuadsAsync(maxStars), minimumCount, quadTolerance);

    public async Task<Matrix3x2?> FindOffsetAndRotationAsync(SortedStarList other, int minimumCount = 6, float quadTolerance = 0.008f, float solutionTolerance = 1e-3f, int? maxStars = null)
    {
        var starRefTable = await FindFitAsync(other, minimumCount, quadTolerance, maxStars);
        return starRefTable is { } ? await starRefTable.FindOffsetAndRotationAsync(solutionTolerance) : null;
    }

    /// <summary>Same as <see cref="FindOffsetAndRotationAsync"/> but also returns the
    /// registration RMS in source-frame pixels (APP / PixInsight surface this per
    /// frame). RMS is <c>NaN</c> when no match is found.</summary>
    public async Task<(Matrix3x2? Solution, float RmsResidualPx)> FindOffsetAndRotationWithRmsAsync(
        SortedStarList other, int minimumCount = 6, float quadTolerance = 0.008f, float solutionTolerance = 1e-3f, int? maxStars = null)
    {
        var starRefTable = await FindFitAsync(other, minimumCount, quadTolerance, maxStars);
        if (starRefTable is null) return (null, float.NaN);
        var solution = await starRefTable.FindOffsetAndRotationAsync(solutionTolerance);
        return solution is { } s ? (s, starRefTable.ComputeRmsResidualPx(s)) : (null, float.NaN);
    }

    public async Task<(Matrix3x2? Solution, float QuadTolerance)> FindOffsetAndRotationWithRetryAsync(SortedStarList other, int minimumCount = 6, float solutionTolerance = 1e-3f, int? maxStars = null)
    {
        var tries = 0;
        var stepSize = 0.0001f;
        for (var quadTolerance = stepSize; quadTolerance < 1.0f; quadTolerance += stepSize)
        {
            var starRefTable = await FindFitAsync(other, minimumCount, quadTolerance, maxStars);
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