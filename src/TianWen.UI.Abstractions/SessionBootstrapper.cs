using System;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Builds the observation schedule from the planner's pinned proposals and launches
    /// <see cref="ISession.RunAsync"/> as a tracked background task, wiring the session's
    /// scout / guider / phase events into the notification feed and mirroring backlash
    /// estimates back into the profile at session end. Extracted from the
    /// StartSessionSignal handler so the signal lambda routes only (see CLAUDE.md
    /// "Signal Handler Pattern"). Container-free: the caller resolves
    /// <see cref="ISessionFactory"/>. Preconditions (session not already running, profile
    /// present, proposals pinned) are the caller's responsibility.
    /// </summary>
    public static class SessionBootstrapper
    {
        public static async Task BuildAndStartAsync(
            ISessionFactory factory,
            GuiAppState appState,
            PlannerState plannerState,
            SessionTabState sessionState,
            LiveSessionState liveSessionState,
            Profile profile,
            BackgroundTaskTracker tracker,
            IExternal external,
            ITimeProvider timeProvider,
            ILogger logger,
            CancellationToken parentToken)
        {
            try
            {
                // Switch to live session tab immediately so user sees progress
                // Set IsRunning immediately to prevent double-start from rapid clicks
                liveSessionState.IsRunning = true;
                liveSessionState.Phase = SessionPhase.NotStarted;
                liveSessionState.ShowAbortConfirm = false;
                liveSessionState.ExposureLogScrollOffset = 0;
                liveSessionState.FocusHistoryScrollOffset = 0;
                appState.ActiveTab = GuiTab.LiveSession;
                appState.StatusMessage = "Building schedule\u2026";
                appState.NeedsRedraw = true;

                var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
                liveSessionState.SessionCts = sessionCts;

                // Build schedule from proposals using planner's window allocation
                var profileData = profile.Data ?? ProfileData.Empty;
                var transform = TransformFactory.FromProfile(profile, timeProvider, out var transformError);
                if (transform is null)
                {
                    appState.AppendNotification(timeProvider.GetUtcNow(),
                        NotificationSeverity.Warning, "Cannot determine site location from profile");
                    liveSessionState.IsRunning = false;
                    return;
                }

                var (filters, design) = AppSignalHandler.GetFirstOtaFilterConfig(profileData);

                var subExposure = SessionContent.EffectiveDefaultSubExposure(sessionState);
                logger.LogInformation(
                    "BuildSchedule: effective default sub-exposure={SubExposure} (config={Config}, f-ratio default={FRatioSec}s)",
                    subExposure, sessionState.Configuration.DefaultSubExposure, SessionContent.DefaultExposureSeconds(sessionState));
                PlannerActions.BuildSchedule(plannerState, sessionState, transform,
                    defaultGain: null, defaultOffset: null,
                    defaultSubExposure: subExposure,
                    defaultObservationTime: TimeSpan.FromMinutes(60),
                    availableFilters: filters,
                    opticalDesign: design);

                if (sessionState.Schedule is not { Count: > 0 } schedule)
                {
                    appState.AppendNotification(timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, "Failed to build schedule from proposals");
                    liveSessionState.IsRunning = false;
                    return;
                }

                logger.LogDebug("StartSession: schedule built with {Count} observations, initialising factory", schedule.Count);
                appState.StatusMessage = "Initialising session...";
                appState.NeedsRedraw = true;
                await factory.InitializeAsync(sessionCts.Token);

                // Create session from pre-built schedule with proper time windows
                var observations = new ScheduledObservation[schedule.Count];
                for (var i = 0; i < schedule.Count; i++)
                {
                    observations[i] = schedule[i];
                }
                // Inject site coordinates and per-OTA setpoint into session configuration
                var setpointTempC = sessionState.CameraSettings is { Count: > 0 }
                    ? sessionState.CameraSettings[0].SetpointTempC
                    : sessionState.Configuration.SetpointCCDTemperature.TempC;
                var config = sessionState.Configuration with
                {
                    SiteLatitude = plannerState.SiteLatitude,
                    SiteLongitude = plannerState.SiteLongitude,
                    SetpointCCDTemperature = new SetpointTemp(setpointTempC, SetpointTempKind.Normal)
                };
                logger.LogDebug("StartSession: site lat={Lat}, lon={Lon}, setpoint={Setpoint}°C",
                    config.SiteLatitude, config.SiteLongitude, setpointTempC);

                var session = factory.Create(
                    profile.ProfileId,
                    config,
                    observations.AsSpan());
                logger.LogDebug("StartSession: session created with {OtaCount} OTAs, launching RunAsync",
                    session.Setup.Telescopes.Length);

                liveSessionState.ActiveSession = session;
                // SiteTimeZone is no longer copied here -- LiveSessionState reads it
                // through to the single app-wide GuiAppState.SiteTimeZone.
                appState.AppendNotification(timeProvider.GetUtcNow(),
                    NotificationSeverity.Info, "Session started");
                appState.NeedsRedraw = true;

                // Surface FOV-obstruction scout decisions in the notification feed.
                // The scout is a 30-90s opaque pause between centering and guider start;
                // without this the user sees nothing until the next phase ticks.
                // Healthy outcomes are silent (the common case shouldn't spam toasts).
                session.ScoutCompleted += (_, e) =>
                {
                    var (msg, severity) = (e.Classification, e.Outcome) switch
                    {
                        (ScoutClassification.Healthy, _) => (null, NotificationSeverity.Info),
                        (ScoutClassification.Transparency, _) =>
                            ($"Scout on {e.Target.Name}: low transparency \u2014 proceeding (recovery loop will engage if it persists).",
                             NotificationSeverity.Info),
                        (ScoutClassification.Obstruction, ScoutOutcome.Proceed) =>
                            ($"Scout on {e.Target.Name}: obstruction cleared during wait \u2014 imaging now.",
                             NotificationSeverity.Info),
                        (ScoutClassification.Obstruction, ScoutOutcome.Advance) =>
                            ($"Scout on {e.Target.Name}: FOV obstructed (~{string.Join("/", e.StarCountsPerOTA)} stars vs baseline)"
                             + (e.EstimatedClearIn is { } c
                                 ? $", clears in {c.TotalMinutes:F0} min \u2014 advancing to next target."
                                 : " with no usable clear time \u2014 advancing to next target."),
                             NotificationSeverity.Warning),
                        _ => (null, NotificationSeverity.Info)
                    };
                    if (msg is not null)
                    {
                        appState.AppendNotification(timeProvider.GetUtcNow(), severity, msg);
                        appState.NeedsRedraw = true;
                    }
                };

                // Surface guider star-loss / recovery transitions in the notification feed.
                // Mapping lives in GuiderActions; silent for ordinary state churn.
                session.GuiderStateChanged += (_, e) =>
                {
                    if (GuiderActions.NotificationForGuiderTransition(e.OldState, e.NewState) is { } n)
                    {
                        appState.AppendNotification(timeProvider.GetUtcNow(), n.Severity, n.Message);
                        appState.NeedsRedraw = true;
                    }
                };

                // Surface each session phase transition in the notification feed.
                // Terminal phases (Complete/Aborted/Failed) are emitted in the RunAsync
                // finally block below, so they are skipped here to avoid duplicates.
                // Extracted to a named local so it can be unsubscribed in finally —
                // prevents a dangling delegate from keeping a stale session alive
                // if the session reference is ever captured here in future refactors.
                void OnPhaseChanged(object? _, SessionPhaseChangedEventArgs e)
                {
                    var phaseMsg = e.NewPhase switch
                    {
                        SessionPhase.Initialising => "Initialising session…",
                        SessionPhase.WaitingForDark => "Waiting for astronomical dark…",
                        SessionPhase.Cooling => "Cooling cameras to setpoint…",
                        SessionPhase.RoughFocus => "Initial rough focus…",
                        SessionPhase.AutoFocus => "Auto-focusing…",
                        SessionPhase.CalibratingGuider => "Calibrating guider…",
                        SessionPhase.Observing => "Observation loop started",
                        SessionPhase.Finalising => "Finalising session…",
                        _ => null
                    };
                    if (phaseMsg is not null)
                    {
                        appState.AppendNotification(timeProvider.GetUtcNow(), NotificationSeverity.Info, phaseMsg);
                        appState.NeedsRedraw = true;
                    }
                }
                session.PhaseChanged += OnPhaseChanged;

                // RunAsync includes Finalise — run as tracked background task so:
                // 1. UI stays responsive (signal handler returns immediately)
                // 2. DrainAsync at shutdown waits for Finalise to complete
                tracker.Run(async () =>
                {
                    try
                    {
                        await session.RunAsync(sessionCts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Session run failed");
                    }
                    finally
                    {
                        session.PhaseChanged -= OnPhaseChanged;
                        liveSessionState.IsRunning = false;
                        liveSessionState.NeedsRedraw = true;

                        // Mirror per-focuser backlash EWMAs back into the active profile's
                        // focuser URIs so the next session bootstraps from last night's value.
                        // The Session sidecar (BacklashHistory) keeps the same data with sample
                        // count + timestamp; the URI mirror is so drivers can read it on connect
                        // without going through the Session.
                        try
                        {
                            if (appState.ActiveProfile is { } activeProfile)
                            {
                                var updated = await EquipmentActions.SaveBacklashEstimatesIfChangedAsync(
                                    session, activeProfile, external, CancellationToken.None);
                                if (!ReferenceEquals(updated, activeProfile))
                                {
                                    appState.ActiveProfile = updated;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to mirror backlash estimates into profile at session end");
                        }

                        var (phaseMsg, phaseSeverity) = liveSessionState.Phase switch
                        {
                            SessionPhase.Complete => ("Session complete", NotificationSeverity.Info),
                            SessionPhase.Aborted => ("Session aborted", NotificationSeverity.Warning),
                            // Session.FailureReason carries the user-facing "which device / what to
                            // check" text; without it fall back to the bare phase.
                            SessionPhase.Failed => (session.FailureReason is { } why ? $"Session failed: {why}" : "Session failed", NotificationSeverity.Error),
                            _ => (null, NotificationSeverity.Info)
                        };
                        if (phaseMsg is not null)
                        {
                            appState.AppendNotification(timeProvider.GetUtcNow(), phaseSeverity, phaseMsg);
                        }
                        else
                        {
                            appState.StatusMessage = null;
                        }
                        appState.NeedsRedraw = true;
                    }
                }, "Session run");
            }
            catch (OperationCanceledException)
            {
                appState.AppendNotification(timeProvider.GetUtcNow(),
                    NotificationSeverity.Warning, "Session cancelled");
                liveSessionState.IsRunning = false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start session");
                appState.AppendNotification(timeProvider.GetUtcNow(),
                    NotificationSeverity.Error, $"Session failed: {ex.Message}");
                liveSessionState.IsRunning = false;
            }
        }
    }
}
