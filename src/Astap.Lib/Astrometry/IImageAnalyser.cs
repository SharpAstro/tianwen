using System.Collections.Generic;
using Astap.Lib.Imaging;
using static Astap.Lib.StatisticsHelper;

namespace Astap.Lib.Astrometry;

public interface IImageAnalyser
{
    (double? median, FocusSolution? solution, int? minPos, int? maxPos) SampleStarsAtFocusPosition(Image image, int currentPos, HFDSamples samples, double snr_min = 20, int max_stars = 500, int max_retries = 2)
    {
        var stars = FindStars(image, snr_min, max_stars, max_retries);
        var hfds = new double[stars.Count];

        for (var i = 0; i < stars.Count; i++)
        {
            hfds[i] = stars[i].HFD;
        }

        var sampleMedianHFD = Median(hfds);

        if (!double.IsNaN(sampleMedianHFD))
        {
            // add the sample
            samples.HFDValues(currentPos).Add(sampleMedianHFD);

            if (samples.TryGetBestFocusSolution(AggregationMethod.Average, out var solution, out var minPos, out var maxPos))
            {
                return (sampleMedianHFD, solution.Value, minPos, maxPos);
            }
            else
            {
                return (sampleMedianHFD, null, null, null);
            }
        }
        else
        {
            return default;
        }
    }

    IReadOnlyList<ImagedStar> FindStars(Image image, double snr_min = 20, int max_stars = 500, int max_retries = 2);
}
