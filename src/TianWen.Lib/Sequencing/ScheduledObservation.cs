using System;
using System.Collections.Immutable;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record ScheduledObservation(
    Target Target,
    DateTimeOffset Start,
    TimeSpan Duration,
    bool AcrossMeridian,
    ImmutableArray<FilterExposure> FilterPlan,
    int? Gain,
    int? Offset,
    ObservationPriority Priority = ObservationPriority.Normal
)
{
    /// <summary>
    /// Backward-compatible: returns the first filter entry's sub-exposure duration.
    /// </summary>
    public TimeSpan SubExposure => FilterPlan is { IsDefaultOrEmpty: false } ? FilterPlan[0].SubExposure : TimeSpan.Zero;
}
