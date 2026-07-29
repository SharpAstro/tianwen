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
    /// Frames the whole plan asks for, <b>per OTA</b> (each OTA works the same plan in parallel), or 0 when
    /// there is no plan -- which is what a bare target queued without one looks like.
    /// <para>
    /// The one place this sum is computed. Progress displays and the wire projection both need it, and a
    /// second loop over <see cref="FilterPlan"/> elsewhere is a second answer waiting to disagree.
    /// </para>
    /// </summary>
    public int PlannedFrameCount
    {
        get
        {
            if (FilterPlan.IsDefaultOrEmpty)
            {
                return 0;
            }

            var total = 0;
            foreach (var entry in FilterPlan)
            {
                total += entry.Count;
            }

            return total;
        }
    }
}
