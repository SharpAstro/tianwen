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
