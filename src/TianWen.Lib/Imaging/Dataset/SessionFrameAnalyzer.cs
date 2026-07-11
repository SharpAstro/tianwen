using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Per-frame quality measurement + session-relative gating for the dataset builder.
/// Measurement follows the exact recipe the stacking pipeline uses for registration
/// (calibrate → debayer → <see cref="Image.FindStarsAsync"/> → median PSF metrics), and
/// the gate decision is the existing pure <see cref="FrameQualityFilter"/> (MAD-based,
/// relative to the session's own median — absolute thresholds don't transfer across
/// focal lengths). One path, one implementation.
/// </summary>
public static class SessionFrameAnalyzer
{
    /// <summary>A light with its measured PSF metrics and detected stars. The star list is
    /// retained so the registration stage never re-runs detection on gated frames.</summary>
    public sealed record AnalyzedFrame(FrameInfo Frame, FrameMetrics Metrics, StarList Stars);

    /// <summary>Gate outcome over one session: kept frames in input order + per-reject reasons.</summary>
    public sealed record GateResult(
        ImmutableArray<AnalyzedFrame> Kept,
        ImmutableArray<(AnalyzedFrame Frame, FrameRejectReason Reason)> Rejected,
        bool KeepFloorTriggered);

    /// <summary>
    /// Loads one light, applies calibration when masters are available, debayers, and
    /// measures star metrics. Returns the metrics plus the (debayered-frame) star list.
    /// All intermediate images are transient (FITS-loaded, no camera buffers to recycle).
    /// </summary>
    public static async Task<AnalyzedFrame> MeasureAsync(
        FrameInfo frame,
        Calibrator? calibrator = null,
        DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG,
        float snrMin = 5f,
        int minStars = 2000,
        CancellationToken cancellationToken = default)
    {
        var raw = await frame.LoadFullAsync(cancellationToken);
        var calibrated = calibrator?.Apply(raw) ?? raw;
        var debayered = await calibrated.DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
        var stars = await debayered.FindStarsAsync(channel: 0, snrMin: snrMin, minStars: minStars, cancellationToken: cancellationToken);
        var metrics = new FrameMetrics(
            MedianHfd: stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median),
            MedianFwhm: stars.MapReduceStarProperty(SampleKind.FWHM, AggregationMethod.Median),
            MedianEllipticity: stars.MapReduceStarProperty(SampleKind.Ellipticity, AggregationMethod.Median),
            StarCount: stars.Count);
        return new AnalyzedFrame(frame, metrics, stars);
    }

    /// <summary>
    /// Applies the session-relative quality gate. <paramref name="sigma"/> is the MAD
    /// threshold in standard-deviation-equivalent units (the stacker's
    /// <c>--quality-reject-sigma</c> knob); frames with zero detected stars are always
    /// rejected regardless of the filter (they would poison the session medians and can
    /// never register anyway).
    /// </summary>
    public static GateResult ApplyGate(IReadOnlyList<AnalyzedFrame> frames, float sigma)
    {
        // Zero-star frames are hard-rejected up front: FrameQualityFilter's left-tail
        // star-count check would usually catch them, but its keep-floor could reprieve
        // them in a badly mixed session, and a 0-star frame is unusable downstream.
        var measurable = new List<AnalyzedFrame>(frames.Count);
        var rejected = ImmutableArray.CreateBuilder<(AnalyzedFrame, FrameRejectReason)>();
        foreach (var frame in frames)
        {
            if (frame.Metrics.StarCount == 0)
            {
                rejected.Add((frame, FrameRejectReason.StarCountTooLow));
            }
            else
            {
                measurable.Add(frame);
            }
        }

        Span<FrameMetrics> metrics = measurable.Count < 512 ? stackalloc FrameMetrics[measurable.Count] : new FrameMetrics[measurable.Count];
        for (var i = 0; i < measurable.Count; i++)
        {
            metrics[i] = measurable[i].Metrics;
        }
        var result = FrameQualityFilter.Filter(metrics, sigma);

        var kept = ImmutableArray.CreateBuilder<AnalyzedFrame>(result.KeptCount);
        for (var i = 0; i < measurable.Count; i++)
        {
            if (result.Reasons[i] == FrameRejectReason.Kept)
            {
                kept.Add(measurable[i]);
            }
            else
            {
                rejected.Add((measurable[i], result.Reasons[i]));
            }
        }
        return new GateResult(kept.ToImmutable(), rejected.ToImmutable(), result.FloorTriggered);
    }
}
