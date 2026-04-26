using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Where the current hour angle sits relative to the configured flip / obstruction zones.
/// All states are computed from <c>HA</c> and the three time fields on
/// <see cref="SessionConfiguration"/>; no device I/O.
/// </summary>
public enum HourAngleZone
{
    /// <summary>HA is east of the obstruction zone (or zone disabled). Imaging is fine.</summary>
    EastOfMeridian,
    /// <summary>HA is inside the configured no-imaging zone immediately before meridian. Tracking should be paused.</summary>
    InObstructionZone,
    /// <summary>HA is past meridian and inside the configured flip window. A flip may be commanded.</summary>
    InFlipWindow,
    /// <summary>HA is past <c>MeridianFlipLatestMinutesAfter</c>. A flip is overdue.</summary>
    PastFlipWindow,
}

/// <summary>Outcome of <see cref="MeridianFlipDecision.DecideFlipAction"/>.</summary>
public enum FlipAction
{
    /// <summary>Continue imaging as normal — HA is east of the zone.</summary>
    Continue,
    /// <summary>HA has entered the obstruction zone — pause exposures and tracking until clear.</summary>
    WaitForObstructionClear,
    /// <summary>Pier side is unchanged but HA reached the flip window. Command a flip ourselves.</summary>
    CommandFlip,
    /// <summary>Pier side already changed (firmware auto-flip, handbox, prior <c>:MNe</c>/<c>:MNw</c>).
    /// Skip the re-slew and just plate-solve recenter + restart guider.</summary>
    AlreadyFlipped,
}

/// <summary>
/// Pure decision functions for meridian-flip handling. Inputs are scalars and the
/// <see cref="SessionConfiguration"/> record so the unit tests don't need devices, time, or async.
/// </summary>
public static class MeridianFlipDecision
{
    /// <summary>
    /// Classify the current hour angle (in hours, signed: negative = east of meridian, positive = west)
    /// against the configured obstruction + flip zones.
    /// </summary>
    /// <remarks>
    /// Zone layout (HA in minutes):
    /// <code>
    /// EastOfMeridian  | InObstructionZone | meridian | InFlipWindow | PastFlipWindow
    /// HA &lt;= -ObsZone | -ObsZone &lt; HA &lt; 0 |          | EarliestAfter &lt;= HA &lt;= LatestAfter | HA &gt; LatestAfter
    /// </code>
    /// The narrow gap <c>0 &lt; HA &lt; EarliestAfter</c> is treated as <see cref="HourAngleZone.InObstructionZone"/>:
    /// the rig is past the mechanical risk but not yet at the earliest sanctioned flip — keep waiting,
    /// don't start new exposures.
    /// </remarks>
    public static HourAngleZone ClassifyHourAngle(double hourAngleHours, SessionConfiguration config)
    {
        var haMinutes = hourAngleHours * 60.0;

        if (haMinutes <= -config.MeridianFlipObstructionZoneMinutesBefore)
        {
            return HourAngleZone.EastOfMeridian;
        }

        if (haMinutes < config.MeridianFlipEarliestMinutesAfter)
        {
            return HourAngleZone.InObstructionZone;
        }

        if (haMinutes <= config.MeridianFlipLatestMinutesAfter)
        {
            return HourAngleZone.InFlipWindow;
        }

        return HourAngleZone.PastFlipWindow;
    }

    /// <summary>
    /// Decide what the imaging loop should do this tick given the observed mount state.
    /// </summary>
    /// <param name="hourAngleHours">Current HA, signed hours.</param>
    /// <param name="pierSideChanged">Has the pier side changed since slew time? (i.e. <c>!IsOnSamePierSide</c>)</param>
    /// <param name="config">Active session configuration.</param>
    public static FlipAction DecideFlipAction(double hourAngleHours, bool pierSideChanged, SessionConfiguration config)
    {
        if (pierSideChanged)
        {
            // Mount flipped without us — firmware auto-flip past limit, handbox press, or
            // someone called SideOfPier setter behind our back. Skip the re-slew.
            return FlipAction.AlreadyFlipped;
        }

        var zone = ClassifyHourAngle(hourAngleHours, config);
        return zone switch
        {
            HourAngleZone.EastOfMeridian => FlipAction.Continue,
            HourAngleZone.InObstructionZone => FlipAction.WaitForObstructionClear,
            HourAngleZone.InFlipWindow => FlipAction.CommandFlip,
            HourAngleZone.PastFlipWindow => FlipAction.CommandFlip,
            _ => throw new ArgumentOutOfRangeException(nameof(hourAngleHours)),
        };
    }
}
