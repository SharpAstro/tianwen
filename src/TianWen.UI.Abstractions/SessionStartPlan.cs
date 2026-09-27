using System;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What a session start sends this computer's node (P6 of docs/plans/hardware-in-the-server.md, #936): the schedule, built
/// here where the plan is (the planner's pinned proposals, their altitude-optimised windows, the session tab's per-filter
/// plan), and the configuration the session tab holds, with the planner's site and the first OTA's setpoint. The node runs
/// it (its connects, cooling, focus, guiding, imaging, flats, finalise, and the backlash mirror into the profile at the end),
/// so a window that dies or wedges takes none of the night with it.
/// </summary>
public readonly record struct SessionStartPlan(ImmutableArray<ScheduledObservation> Schedule, SessionConfiguration Configuration)
{
    /// <summary>
    /// Builds the plan into <paramref name="session"/>'s schedule, as the session tab shows it; null with the reason, in words
    /// a user can act on, when there is no plan to send.
    /// </summary>
    public static SessionStartPlan? TryBuild(PlannerState planner, SessionTabState session, Profile profile, ITimeProvider timeProvider,
        ILogger logger, out string? problem)
    {
        var profileData = profile.Data ?? ProfileData.Empty;
        if (TransformFactory.FromProfile(profile, timeProvider, out _) is not { } transform)
        {
            problem = "Cannot determine site location from profile";
            return null;
        }

        var (filters, design) = AppSignalHandler.GetFirstOtaFilterConfig(profileData);
        var subExposure = SessionContent.EffectiveDefaultSubExposure(session);
        logger.LogInformation(
            "BuildSchedule: effective default sub-exposure={SubExposure} (config={Config}, f-ratio default={FRatioSec}s)",
            subExposure, session.Configuration.DefaultSubExposure, SessionContent.DefaultExposureSeconds(session));
        PlannerActions.BuildSchedule(planner, session, transform,
            defaultGain: null, defaultOffset: null,
            defaultSubExposure: subExposure,
            defaultObservationTime: TimeSpan.FromMinutes(60),
            availableFilters: filters,
            opticalDesign: design);

        if (session.Schedule is not { Count: > 0 } schedule)
        {
            problem = "Failed to build schedule from proposals";
            return null;
        }

        // The site the plan was made at, and the first OTA's setpoint.
        var setpointTempC = session.CameraSettings is { Count: > 0 }
            ? session.CameraSettings[0].SetpointTempC
            : session.Configuration.SetpointCCDTemperature.TempC;
        var configuration = session.Configuration with
        {
            SiteLatitude = planner.SiteLatitude,
            SiteLongitude = planner.SiteLongitude,
            SetpointCCDTemperature = new SetpointTemp(setpointTempC, SetpointTempKind.Normal),
        };

        problem = null;
        return new SessionStartPlan([.. schedule], configuration);
    }

    /// <summary>
    /// The configuration a flat run started from the Live Session starts from, the session tab's as a session's is, so on-demand
    /// flats cool to the setpoint the lights are taken at; the flat knobs of the request are laid over it on the node.
    /// </summary>
    public static SessionConfiguration ForFlats(SessionTabState session, ProfileData profile)
    {
        var setpointTempC = session.CameraSettings is { Count: > 0 }
            ? session.CameraSettings[0].SetpointTempC
            : session.Configuration.SetpointCCDTemperature.TempC;
        return session.Configuration with
        {
            // NaN falls back to the mount's own site on the node, as the CLI's flats do.
            SiteLatitude = profile.SiteLatitude ?? double.NaN,
            SiteLongitude = profile.SiteLongitude ?? double.NaN,
            SetpointCCDTemperature = new SetpointTemp(setpointTempC, SetpointTempKind.Normal),
        };
    }
}
