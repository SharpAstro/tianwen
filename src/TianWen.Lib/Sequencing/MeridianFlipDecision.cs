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
    /// <param name="alreadyOnCorrectSide">Is the mount's current pier side the destination side for the
    /// current pointing? When <c>true</c> the rig is already configured for where it points, so no flip is
    /// needed even though HA is past the meridian — the case where we slewed straight to a target that had
    /// already crossed (joined an in-progress <c>AcrossMeridian</c> observation, or re-acquired post-flip).
    /// This is what stops the flip from re-firing forever on a mount whose pier side never changes
    /// (e.g. SkyWatcher reporting Normal throughout a west-of-meridian track).</param>
    /// <param name="hasFlipped">Have we already performed (or detected) a flip for the current target?
    /// A GEM flips at most once per target; this is the belt-and-suspenders backstop that prevents a
    /// re-trigger even if the side check is inconclusive. Reset by the caller on each new slew.</param>
    /// <param name="config">Active session configuration.</param>
    public static FlipAction DecideFlipAction(
        double hourAngleHours,
        bool pierSideChanged,
        bool alreadyOnCorrectSide,
        bool hasFlipped,
        SessionConfiguration config)
    {
        if (hasFlipped)
        {
            // Already flipped (or detected a flip) for this target — never flip twice. We are past the
            // meridian on the new side; keep imaging. The pre-meridian obstruction zone cannot recur for
            // this target, so there is nothing to wait for.
            return FlipAction.Continue;
        }

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
            // Past the meridian: only command a flip if the mount is actually on the wrong pier side for
            // where it points. If it already sits on the destination side (slewed straight to a target that
            // had already crossed), there is nothing to flip — continue imaging.
            HourAngleZone.InFlipWindow => alreadyOnCorrectSide ? FlipAction.Continue : FlipAction.CommandFlip,
            HourAngleZone.PastFlipWindow => alreadyOnCorrectSide ? FlipAction.Continue : FlipAction.CommandFlip,
            _ => throw new ArgumentOutOfRangeException(nameof(hourAngleHours)),
        };
    }
}
