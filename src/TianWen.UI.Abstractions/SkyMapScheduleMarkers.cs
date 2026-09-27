using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The sky map's markers for a schedule, the one being imaged flagged (P5b part 8 of docs/plans/hardware-in-the-server.md).
/// The schedule is the view on show's: this computer's committed plan, or a rig's own. The active one is matched through
/// <see cref="PlannerActions.IsSameObject"/>: by catalogue index for a planet, the Moon or a comet, whose coordinates are
/// ephemeris values of an instant and so differ between the plan and the run, and by the whole target otherwise, since
/// mosaic panels share an index and a match on it would light every panel.
/// </summary>
public static class SkyMapScheduleMarkers
{
    public static ImmutableArray<(double RA, double Dec, string Name, bool IsActive)> Build(
        IReadOnlyList<ScheduledObservation> schedule, Target? active)
    {
        var markers = ImmutableArray.CreateBuilder<(double RA, double Dec, string Name, bool IsActive)>(schedule.Count);
        foreach (var observation in schedule)
        {
            var target = observation.Target;
            if (double.IsNaN(target.RA) || double.IsNaN(target.Dec))
            {
                continue;
            }
            markers.Add((target.RA, target.Dec, target.Name, active is { } a && PlannerActions.IsSameObject(a, target)));
        }
        return markers.ToImmutable();
    }
}
