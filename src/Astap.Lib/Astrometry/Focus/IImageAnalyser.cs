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
        double snr_min = 20,
        int max_stars = 500,
        int max_retries = 2)
    {
        var stars = FindStars(image, snr_min, max_stars, max_retries);
        var count = stars.Count;
        Span<double> starSamples = count < 100 ? stackalloc double[count] : new double[count];

        for (var i = 0; i < count; i++)
        {
            starSamples[i] = samples.Kind switch
            {
                SampleKind.HFD => stars[i].HFD,
                SampleKind.FWHM => stars[i].StarFWHM,
                _ => throw new ArgumentException($"Cannot find sample value for {samples.Kind}", nameof(samples))
            };
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

    IReadOnlyList<ImagedStar> FindStars(Image image, double snr_min = 20, int max_stars = 500, int max_retries = 2);
}
