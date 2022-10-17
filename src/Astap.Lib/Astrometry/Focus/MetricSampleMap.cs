using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using static Astap.Lib.StatisticsHelper;

namespace Astap.Lib.Astrometry.Focus;

public class MetricSampleMap
{
    private readonly ConcurrentDictionary<int, ConcurrentBag<float>> _samples = new();

    public MetricSampleMap(SampleKind kind)
    {
        Kind = kind;
    }

    public (FocusSolution? solution, int? minPos, int? maxPos) SampleStarsAtFocusPosition(float sample, int currentPos)
    {
        if (!double.IsNaN(sample) && sample > 0.0)
        {
            // add the sample
            Samples(currentPos).Add(sample);

            if (TryGetBestFocusSolution(AggregationMethod.Average, out var solution, out var minPos, out var maxPos))
            {
                return (solution.Value, minPos, maxPos);
            }
            else
            {
                return default;
            }
        }
        else
        {
            return default;
        }
    }

    public SampleKind Kind { get; }

    internal ConcurrentBag<float> Samples(int focusPos) => _samples.GetOrAdd(focusPos, pFocusPos => new ConcurrentBag<float>());

    public bool TryGetBestFocusSolution(AggregationMethod method, [NotNullWhen(true)] out FocusSolution? solution, out int min, out int max)
    {
        var keys = _samples.Keys.ToArray();
        Array.Sort(keys);

        var data = new float[keys.Length, 2];
        if (keys.Length > 2)
        {
            min = keys[0];
            max = keys[^1];
        }
        else
        {
            min = -1;
            max = -1;

            solution = null;
            return false;
        }

        for (int i = 0; i < keys.Length; i++)
        {
            var focusPos = keys[i];

            var aggregated = Aggregate(focusPos, method);
            if (!aggregated.HasValue)
            {
                solution = null;
                return false;
            }

            data[i, 0] = focusPos;
            data[i, 1] = aggregated.Value;
        }

        solution = Hyperbola.FindBestHyperbolaFit(data);
        return true;
    }

    public float? Aggregate(int focusPos, AggregationMethod method)
    {
        if (_samples.TryGetValue(focusPos, out var samples))
        {
            switch (method)
            {
                case AggregationMethod.Median:
                    var median = Median(samples.ToArray());
                    return float.IsNaN(median) ? null as float? : median;

                case AggregationMethod.Best:
                    return !samples.IsEmpty ? samples.Min() : default;

                case AggregationMethod.Average:
                    return !samples.IsEmpty ? samples.Average() : default;

                default:
                    return default;
            }
        }
        else
        {
            return default;
        }
    }
}
