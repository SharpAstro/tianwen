using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Stat;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public class StarList(ConcurrentBag<ImagedStar> stars) : IReadOnlyCollection<ImagedStar>
{
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
                _ => throw new ArgumentException($"Cannot find sample value for {kind}", nameof(kind))
            };
        }

        return aggregationMethod switch
        {
            AggregationMethod.Median => Median(starSamples),
            AggregationMethod.Average => Average(starSamples),
            _ => throw new ArgumentException($"Averaging method {aggregationMethod} is not supported", nameof(aggregationMethod))
        };
    }

    public int Count => stars.Count;

    public IEnumerator<ImagedStar> GetEnumerator() => stars.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public ImagedStar[] ToArray() => [.. stars];
}
