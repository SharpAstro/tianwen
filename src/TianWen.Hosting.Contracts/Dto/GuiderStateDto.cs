using System;
using System.Collections.Immutable;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

public sealed class GuiderStateDto
{
    public string? State { get; init; }
    public double? TotalRMS { get; init; }
    public double? RaRMS { get; init; }
    public double? DecRMS { get; init; }
    public double? PeakRa { get; init; }
    public double? PeakDec { get; init; }
    public required double GuideExposureSeconds { get; init; }
    public required ImmutableArray<GuideStepDto> RecentSteps { get; init; }

    /// <summary>
    /// Guide star position in guide-frame pixels, and its SNR. Null when nothing is being tracked.
    /// <para>
    /// These are what turn the guide preview into a guider view: without the position the crosshair has
    /// nowhere to go, and the SNR is the number that says whether a drifting graph means seeing or a
    /// star about to be lost. Deliberately NOT <c>required</c> -- a nullable wire member that is also
    /// required cannot round-trip, because the writer omits it when null.
    /// </para>
    /// </summary>
    public double? GuideStarX { get; init; }

    /// <inheritdoc cref="GuideStarX"/>
    public double? GuideStarY { get; init; }

    /// <inheritdoc cref="GuideStarX"/>
    public double? GuideStarSNR { get; init; }

    /// <summary>
    /// Change token for the guide-camera preview, so a client polling <c>/preview/guider</c> can decide
    /// from a state poll it was making anyway whether to refetch. Mirrors <c>CameraStateDto</c>'s frame
    /// number serving the per-OTA previews.
    /// </summary>
    public required int GuideFrameNumber { get; init; }

    /// <summary>Projects the guider slice. <see cref="ISessionTelemetry"/> for the same reason as
    /// <see cref="SessionStateDto.FromSession"/>.</summary>
    public static GuiderStateDto FromSession(ISessionTelemetry session)
    {
        var stats = session.LastGuideStats;
        var steps = ImmutableArray.CreateBuilder<GuideStepDto>(session.GuideSamples.Length);
        foreach (var s in session.GuideSamples)
        {
            steps.Add(new GuideStepDto
            {
                Timestamp = s.Timestamp,
                RaError = JsonNumber.OrNull(s.RaError),
                DecError = JsonNumber.OrNull(s.DecError),
                RaCorrectionMs = JsonNumber.OrNull(s.RaCorrectionMs),
                DecCorrectionMs = JsonNumber.OrNull(s.DecCorrectionMs),
                IsDither = s.IsDither,
                IsSettling = s.IsSettling,
            });
        }

        return new GuiderStateDto
        {
            State = session.GuiderState,
            // Null with no stats, and null for a figure no sample has been folded into yet (NaN): not known
            // is never 0 on the native wire.
            TotalRMS = JsonNumber.OrNull(stats?.TotalRMS ?? double.NaN),
            RaRMS = JsonNumber.OrNull(stats?.RaRMS ?? double.NaN),
            DecRMS = JsonNumber.OrNull(stats?.DecRMS ?? double.NaN),
            PeakRa = JsonNumber.OrNull(stats?.PeakRa ?? double.NaN),
            PeakDec = JsonNumber.OrNull(stats?.PeakDec ?? double.NaN),
            GuideExposureSeconds = session.GuideExposure.TotalSeconds,
            RecentSteps = steps.MoveToImmutable(),
            // A centroid on a frame with no star can come back non-finite, which is not known, so null: one NaN
            // reaching the writer would be a bodiless 500 for the WHOLE state response, not just this field.
            GuideStarX = session.GuideStarPosition is { } p ? JsonNumber.OrNull(p.X) : null,
            GuideStarY = session.GuideStarPosition is { } q ? JsonNumber.OrNull(q.Y) : null,
            GuideStarSNR = session.GuideStarSNR is { } snr ? JsonNumber.OrNull(snr) : null,
            GuideFrameNumber = session.LastGuideFrameNumber,
        };
    }
}

public sealed class GuideStepDto
{
    public required DateTimeOffset Timestamp { get; init; }
    public double? RaError { get; init; }
    public double? DecError { get; init; }
    public double? RaCorrectionMs { get; init; }
    public double? DecCorrectionMs { get; init; }
    public required bool IsDither { get; init; }
    public required bool IsSettling { get; init; }
}
