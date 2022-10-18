using System;
using System.Collections.Generic;
using Astap.Lib.Imaging;
using static Astap.Lib.StatisticsHelper;

namespace Astap.Lib.Astrometry.Focus;

public interface IImageAnalyser
{
    public (float? median, FocusSolution? solution, int? minPos, int? maxPos, int starCount) SampleStarsAtFocusPosition(
        Image image,
        int currentPos,
        MetricSampleMap samples,
        float snrMin = 20f,
        int maxStars = 500,
        int maxStarIterations = 2,
        int maxFocusIterations = 20)
    {
        var stars = FindStars(image, snrMin, maxStars, maxStarIterations);
        var count = stars.Count;
        Span<float> starSamples = count < 200 ? stackalloc float[count] : new float[count];

        switch (samples.Kind)
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
                throw new ArgumentException($"Cannot find sample value for {samples.Kind}", nameof(samples));
        }

        var median = Median(starSamples);

        if (!float.IsNaN(median))
        {
            // add the sample
            samples.Samples(currentPos).Add(median);

            if (samples.TryGetBestFocusSolution(AggregationMethod.Average, out var solution, out var minPos, out var maxPos, maxIterations: maxFocusIterations))
            {
                return (median, solution.Value, minPos, maxPos, count);
            }
            else
            {
                return (median, null, null, null, count);
            }
        }
        else
        {
            return default;
        }
    }

    IReadOnlyList<ImagedStar> FindStars(Image image, float snrMin = 20f, int maxStars = 500, int maxIterations = 2);
}
