using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Sequencing.PolarAlignment
{
    /// <summary>
    /// Ranks <see cref="ICaptureSource"/> candidates for the polar-alignment
    /// routine when more than one is available. The "best" combo is the one
    /// that yields the shortest practical exposure to a plate-solveable frame.
    ///
    /// Score = (1 / fRatio) * clamp(pixelScale, 1, 5)
    ///
    /// Smaller f-ratio = wider apparent field, more reference stars per second.
    /// Pixel scale clamped to the 1-5 arcsec/pixel band that plate solvers
    /// digest fastest and most reliably; outside that band the solve runs slow
    /// and is more likely to fail near the pole. Tie-break prefers main camera
    /// (no PHD2 RPC + Save Images dependency).
    /// </summary>
    internal static class CaptureSourceRanker
    {
        /// <summary>
        /// Lower bound of the "fast" pixel-scale band, in arcsec/pixel. Below 1"/px
        /// the FOV is too narrow for reliable wide-field polar pole solves.
        /// </summary>
        public const double PixelScaleMinArcsec = 1.0;

        /// <summary>
        /// Upper bound of the "fast" pixel-scale band, in arcsec/pixel. Above 5"/px
        /// stars are under-sampled and centroid uncertainty dominates the solve.
        /// </summary>
        public const double PixelScaleMaxArcsec = 5.0;

        /// <summary>
        /// Compute the score for a single capture source. Higher is better.
        /// Pure function — same inputs always produce the same score.
        /// </summary>
        public static double Score(ICaptureSource source)
        {
            if (source.FRatio <= 0 || double.IsNaN(source.FRatio) || double.IsInfinity(source.FRatio))
            {
                return 0;
            }

            double clampedPxScale = Math.Clamp(source.PixelScaleArcsecPerPx, PixelScaleMinArcsec, PixelScaleMaxArcsec);
            return (1.0 / source.FRatio) * clampedPxScale;
        }

        /// <summary>
        /// Rank candidates from best to worst. <see cref="IsMainCamera"/> is the
        /// tie-breaker for sources with equal numerical score: main camera wins.
        /// </summary>
        public static ImmutableArray<RankedSource> Rank(
            IEnumerable<ICaptureSource> candidates,
            Func<ICaptureSource, bool> isMainCamera)
        {
            return [..
                candidates
                    .Select(c => new RankedSource(c, Score(c), isMainCamera(c)))
                    .OrderByDescending(r => r.Score)
                    .ThenByDescending(r => r.IsMainCamera)
            ];
        }
    }

    /// <summary>Capture source with its computed ranking score.</summary>
    /// <param name="Source">The candidate.</param>
    /// <param name="Score">Numeric score (see <see cref="CaptureSourceRanker.Score"/>).</param>
    /// <param name="IsMainCamera">Tie-break flag: true if this is a main imaging camera
    /// (rather than a guide camera). Drives the tie-break in <see cref="CaptureSourceRanker.Rank"/>.</param>
    internal readonly record struct RankedSource(ICaptureSource Source, double Score, bool IsMainCamera);
}
