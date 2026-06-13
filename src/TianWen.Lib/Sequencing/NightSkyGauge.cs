using System;

namespace TianWen.Lib.Sequencing
{
    /// <summary>
    /// Tonight's sky calibration measured at the zenith rough-focus frame. Because the zenith is
    /// (for any real setup) never obstructed and sits at ~air mass 1, the ratio of detected stars to
    /// catalog-predicted stars there is a clean, obstruction-free read of transparency x the rig's
    /// detection capability. It anchors three things: the first-scout obstruction oracle (expected
    /// star count = catalog x <see cref="Efficiency"/>), the whole-sky cloud gate (a crushed
    /// <see cref="Efficiency"/> means cloud, since zenith can't be blocked), and the transparency
    /// readout (<see cref="EffectiveLimitMag"/>).
    /// </summary>
    /// <param name="DetectedAtZenith">Stars actually detected in the zenith rough-focus frame.</param>
    /// <param name="CatalogPredictedAtZenith">Catalog stars expected in the field down to
    /// <paramref name="TheoreticalLimitMag"/> (clear-sky model limit).</param>
    /// <param name="TheoreticalLimitMag">Clear-sky limiting magnitude from the detectability model for the
    /// zenith exposure/optics.</param>
    /// <param name="EffectiveLimitMag">Limiting magnitude actually reached tonight (the magnitude at which
    /// the catalog cumulative count matches <paramref name="DetectedAtZenith"/>); the gap to
    /// <paramref name="TheoreticalLimitMag"/> is the extinction in magnitudes.</param>
    /// <param name="Efficiency">detected / predicted, clamped to [0,1]; ~1 = transparent, low = hazy/cloud.</param>
    /// <param name="Valid">True when the prediction was large enough to trust the ratio.</param>
    public readonly record struct NightSkyGauge(
        int DetectedAtZenith,
        int CatalogPredictedAtZenith,
        double TheoreticalLimitMag,
        double EffectiveLimitMag,
        double Efficiency,
        bool Valid)
    {
        /// <summary>An absent / unusable gauge (no rough-focus frame, or too few predicted stars to trust).</summary>
        public static NightSkyGauge None => new(0, 0, double.NaN, double.NaN, 1.0, false);

        /// <summary>
        /// Builds a gauge from a zenith detection count, the field's cumulative magnitude histogram
        /// (<see cref="Astrometry.Catalogs.CatalogStarCounter.CountStarsByMagnitude"/> output: <c>magBins[b]</c>
        /// = stars with V &lt;= <c>(b+1)*0.5</c>), and the clear-sky theoretical limiting magnitude.
        /// </summary>
        /// <param name="detectedAtZenith">Detected star count in the zenith frame.</param>
        /// <param name="cumulativeMagBins">Cumulative catalog counts per 0.5-mag bin for the zenith field.</param>
        /// <param name="theoreticalLimitMag">Clear-sky model limiting magnitude for the zenith exposure.</param>
        /// <param name="minPredictedToTrust">Minimum predicted count for the gauge to be <see cref="Valid"/>.</param>
        public static NightSkyGauge FromCounts(
            int detectedAtZenith, ReadOnlySpan<int> cumulativeMagBins, double theoreticalLimitMag, int minPredictedToTrust)
        {
            var predicted = CumulativeAtMag(cumulativeMagBins, theoreticalLimitMag);
            if (predicted < minPredictedToTrust || cumulativeMagBins.Length == 0)
            {
                return None;
            }

            var efficiency = Math.Clamp((double)detectedAtZenith / Math.Max(predicted, 1), 0.0, 1.0);
            var effectiveLimit = InvertToMag(cumulativeMagBins, detectedAtZenith);
            return new NightSkyGauge(detectedAtZenith, predicted, theoreticalLimitMag, effectiveLimit, efficiency, Valid: true);
        }

        /// <summary>Cumulative catalog count at the given magnitude (0.5-mag bin resolution).</summary>
        private static int CumulativeAtMag(ReadOnlySpan<int> cumulativeMagBins, double mag)
        {
            if (cumulativeMagBins.Length == 0)
            {
                return 0;
            }
            var bin = Math.Clamp((int)(mag / 0.5) - 1, 0, cumulativeMagBins.Length - 1);
            return cumulativeMagBins[bin];
        }

        /// <summary>Faintest magnitude whose cumulative catalog count first reaches <paramref name="target"/>.</summary>
        private static double InvertToMag(ReadOnlySpan<int> cumulativeMagBins, int target)
        {
            for (var b = 0; b < cumulativeMagBins.Length; b++)
            {
                if (cumulativeMagBins[b] >= target)
                {
                    return (b + 1) * 0.5;
                }
            }
            // Detected more than the catalog predicts even at the faintest bin: cap at the catalog's edge.
            return cumulativeMagBins.Length * 0.5;
        }
    }
}
