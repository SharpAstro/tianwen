using System;
using System.Collections.Immutable;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

public sealed class GuiderStateDto
{
    public string? State { get; init; }
    public required double TotalRMS { get; init; }
    public required double RaRMS { get; init; }
    public required double DecRMS { get; init; }
    public required double PeakRa { get; init; }
    public required double PeakDec { get; init; }
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
                RaError = JsonNumber.ForWire(s.RaError),
                DecError = JsonNumber.ForWire(s.DecError),
                RaCorrectionMs = JsonNumber.ForWire(s.RaCorrectionMs),
                DecCorrectionMs = JsonNumber.ForWire(s.DecCorrectionMs),
                IsDither = s.IsDither,
                IsSettling = s.IsSettling,
            });
        }

        return new GuiderStateDto
        {
            State = session.GuiderState,
            // A stats object can exist before any sample has been folded in, so the figures can be
            // NaN even when `stats` is non-null -- the ?? only guards the null case.
            TotalRMS = JsonNumber.ForWire(stats?.TotalRMS ?? 0),
            RaRMS = JsonNumber.ForWire(stats?.RaRMS ?? 0),
            DecRMS = JsonNumber.ForWire(stats?.DecRMS ?? 0),
            PeakRa = JsonNumber.ForWire(stats?.PeakRa ?? 0),
            PeakDec = JsonNumber.ForWire(stats?.PeakDec ?? 0),
            GuideExposureSeconds = session.GuideExposure.TotalSeconds,
            RecentSteps = steps.MoveToImmutable(),
            // Through ForWire like every other double here: a centroid on a frame with no star can come
            // back non-finite, and one NaN reaching the writer is a bodiless 500 for the WHOLE state
            // response, not just this field.
            GuideStarX = session.GuideStarPosition is { } p ? JsonNumber.ForWire(p.X) : null,
            GuideStarY = session.GuideStarPosition is { } q ? JsonNumber.ForWire(q.Y) : null,
            GuideStarSNR = session.GuideStarSNR is { } snr ? JsonNumber.ForWire(snr) : null,
            GuideFrameNumber = session.LastGuideFrameNumber,
        };
    }
}

public sealed class GuideStepDto
{
    public required DateTimeOffset Timestamp { get; init; }
    public required double RaError { get; init; }
    public required double DecError { get; init; }
    public required double RaCorrectionMs { get; init; }
    public required double DecCorrectionMs { get; init; }
    public required bool IsDither { get; init; }
    public required bool IsSettling { get; init; }
}
