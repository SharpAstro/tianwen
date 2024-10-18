using Astap.Lib.Imaging;
using Astap.Lib.Stat;
using System;
using System.Collections.Generic;
using static Astap.Lib.Stat.StatisticsHelper;

namespace Astap.Lib.Astrometry.Focus;

public interface IImageAnalyser
{
    (FocusSolution? solution, int? minPos, int? maxPos) SampleStarsAtFocusPosition(
        MetricSampleMap samples,
        int currentPos,
        float sample,
        int starCount,
        int maxFocusIterations = 20
    )
    {
        if (!float.IsNaN(sample) && sample > 0)
        {
            // add the sample
            samples.Samples(currentPos).Add(sample);

            if (samples.TryGetBestFocusSolution(out var solution, out var minPos, out var maxPos, maxIterations: maxFocusIterations))
            {
                return (solution.Value, minPos, maxPos);
            }
        }

        return default;
    }

    float MapReduceStarProperty(IReadOnlyList<ImagedStar> stars, SampleKind kind, AggregationMethod aggregationMethod)
    {
        var count = stars.Count;
        Span<float> starSamples = count < 200 ? stackalloc float[count] : new float[count];

        switch (kind)
        {
            case SampleKind.HFD:
                for (var i = 0; i < count; i++)
                {
                    starSamples[i] = stars[i].HFD;
                }
                break;

            case SampleKind.FWHM:
                for (var i = 0; i < count; i++)
                {
                    starSamples[i] = stars[i].StarFWHM;
                }
                break;

            default:
                throw new ArgumentException($"Cannot find sample value for {kind}", nameof(stars));
        }

        return aggregationMethod switch
        {
            AggregationMethod.Median => Median(starSamples),
            AggregationMethod.Average => Average(starSamples),
            _ => throw new ArgumentException($"Averaging method {aggregationMethod} is not supported", nameof(aggregationMethod))
        };
    }

    IReadOnlyList<ImagedStar> FindStars(Image image, float snrMin = 20f, int maxStars = 500, int maxIterations = 2);
}
