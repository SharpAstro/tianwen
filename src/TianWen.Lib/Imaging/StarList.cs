using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TianWen.Lib.Stat;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public class StarList(ConcurrentBag<ImagedStar> stars, BitMatrix? starMask = null) : IReadOnlyCollection<ImagedStar>
{
    public static StarList Empty { get; } = new StarList(new ConcurrentBag<ImagedStar>());

    /// <summary>
    /// Bit mask of pixels occupied by detected stars, or <c>null</c> if not available.
    /// Built during <see cref="Image.FindStarsAsync"/> for deduplication and reusable
    /// for star-aware background estimation.
    /// </summary>
    public BitMatrix? StarMask => starMask;

    /// <summary>
    /// This list with every centroid translated by (<paramref name="dx"/>, <paramref name="dy"/>).
    /// </summary>
    /// <remarks>
    /// For converting between pixel grids -- the mono debayer detection runs on samples the mosaic
    /// half a pixel off the grid it indexes, so a mosaic detection shifts back into mosaic
    /// coordinates. Only the centroids move: HFD, FWHM, ellipticity and flux are grid-independent,
    /// and <see cref="StarMask"/> is deliberately NOT translated, since it indexes the pixels the
    /// measurement actually ran on and a shifted mask would no longer correspond to any image.
    /// </remarks>
    public StarList ShiftedBy(float dx, float dy)
    {
        if (dx == 0f && dy == 0f)
        {
            return this;
        }

        var shifted = new ConcurrentBag<ImagedStar>();
        foreach (var star in stars)
        {
            shifted.Add(star with { XCentroid = star.XCentroid + dx, YCentroid = star.YCentroid + dy });
        }
        return new StarList(shifted, starMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public float MapReduceStarProperty(SampleKind kind, AggregationMethod aggregationMethod)
    {
        var count = stars.Count;
        Span<float> starSamples = count < 256 ? stackalloc float[count] : new float[count];

        var i = 0;
        foreach (var star in stars)
        {
            starSamples[i++] = kind switch
            {
                SampleKind.HFD => star.HFD,
                SampleKind.FWHM => star.StarFWHM,
                SampleKind.Ellipticity => star.Ellipticity,
                _ => throw new ArgumentException($"Cannot find sample value for {kind}", nameof(kind))
            };
        }

        return aggregationMethod switch
        {
            AggregationMethod.Median => MedianFast(starSamples),
            AggregationMethod.Average => Average(starSamples),
            _ => throw new ArgumentException($"Averaging method {aggregationMethod} is not supported", nameof(aggregationMethod))
        };
    }

    public int Count => stars.Count;

    public IEnumerator<ImagedStar> GetEnumerator() => stars.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public ImagedStar[] ToArray() => [.. stars];
}
