using System;
using System.Collections.Immutable;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Hosting.Dto;

public sealed class GuiderStateDto
{
    public required string? State { get; init; }
    public required double TotalRMS { get; init; }
    public required double RaRMS { get; init; }
    public required double DecRMS { get; init; }
    public required double PeakRa { get; init; }
    public required double PeakDec { get; init; }
    public required double GuideExposureSeconds { get; init; }
    public required ImmutableArray<GuideStepDto> RecentSteps { get; init; }

    public static GuiderStateDto FromSession(ISession session)
    {
        var stats = session.LastGuideStats;
        var steps = ImmutableArray.CreateBuilder<GuideStepDto>(session.GuideSamples.Length);
        foreach (var s in session.GuideSamples)
        {
            steps.Add(new GuideStepDto
            {
                Timestamp = s.Timestamp,
                RaError = s.RaError,
                DecError = s.DecError,
                RaCorrectionMs = s.RaCorrectionMs,
                DecCorrectionMs = s.DecCorrectionMs,
                IsDither = s.IsDither,
                IsSettling = s.IsSettling,
            });
        }

        return new GuiderStateDto
        {
            State = session.GuiderState,
            TotalRMS = stats?.TotalRMS ?? 0,
            RaRMS = stats?.RaRMS ?? 0,
            DecRMS = stats?.DecRMS ?? 0,
            PeakRa = stats?.PeakRa ?? 0,
            PeakDec = stats?.PeakDec ?? 0,
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
