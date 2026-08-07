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

    /// <summary>
    /// Frames this slot is expected to yield, <b>per OTA</b> (each OTA works the same plan in parallel),
    /// or 0 when there is no plan -- which is what a bare target queued without one looks like.
    /// <para>
    /// An <b>estimate</b>, derived from <see cref="Duration"/> via <see cref="FrameCountEstimate"/>;
    /// present it as approximate. It used to be the sum of <see cref="FilterExposure.Count"/> over the
    /// plan, which answers a different question -- see the remarks on <see cref="FrameCountEstimate"/>
    /// for why the imaging loop makes that sum wrong (it reported 1 for any rig with no filter wheel).
    /// </para>
    /// <para>
    /// Progress displays and the wire projection both need this number, and a second formula elsewhere
    /// is a second answer waiting to disagree -- which is exactly what had happened.
    /// </para>
    /// </summary>
    public int PlannedFrameCount => FrameCountEstimate.ForPlan(Duration, FilterPlan);
}
