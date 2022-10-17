using System;
using System.Collections.Generic;
using Astap.Lib.Imaging;
using static Astap.Lib.StatisticsHelper;

namespace Astap.Lib.Astrometry.Focus;

public interface IImageAnalyser
{
    (double? median, FocusSolution? solution, int? minPos, int? maxPos) SampleStarsAtFocusPosition(
        Image image,
        int currentPos,
        MetricSampleMap samples,
        float snr_min = 20f,
        int max_stars = 500,
        int max_retries = 2)
    {
        var stars = FindStars(image, snr_min, max_stars, max_retries);
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

        if (!double.IsNaN(median))
        {
            // add the sample
            samples.Samples(currentPos).Add(median);

            if (samples.TryGetBestFocusSolution(AggregationMethod.Average, out var solution, out var minPos, out var maxPos))
            {
                return (median, solution.Value, minPos, maxPos);
            }
            else
            {
                return (median, null, null, null);
            }
        }
        else
        {
            return default;
        }
    }

    IReadOnlyList<ImagedStar> FindStars(Image image, float snr_min = 20f, int max_stars = 500, int max_retries = 2);
}
