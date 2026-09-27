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
            // Completed on EVERY way out: the early returns and both catches below (through the outer
            // finally), and the tracked run's own finally, so a RigShutdown awaiting the session never
            // waits on a start that failed, and never walks past a Finalise still warming the cameras.
            var ended = liveSessionState.BeginSession();
            var handedToRun = false;
            try
            {
                // Switch to live session tab immediately so user sees progress
                // Set IsRunning immediately to prevent double-start from rapid clicks
                liveSessionState.IsRunning = true;
                liveSessionState.Phase = SessionPhase.NotStarted;
                liveSessionState.ShowAbortConfirm = false;
                // Exposure-log view resets itself: the new session's empty log re-establishes the
                // scroll controller's tail pin (the fits-again rule), so no offset reset here.
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
                var started = SessionNotes.ForRunStart(flatRun: false);
                appState.AppendNotification(timeProvider.GetUtcNow(), started.Severity, started.Message);
                appState.NeedsRedraw = true;

                // The run's notes in the feed, in the words a node's feed uses for the same run (SessionNotes, the ONE
                // mapping). A scout is a 30-90 s opaque pause between centring and guiding, a lost guide star is worth
                // knowing, and each phase says where the night is; healthy outcomes and ordinary churn stay quiet.
                void Note(SessionNote? note)
                {
                    if (note is { } n)
                    {
                        appState.AppendNotification(timeProvider.GetUtcNow(), n.Severity, n.Message);
                        appState.NeedsRedraw = true;
                    }
                }
                session.ScoutCompleted += (_, e) => Note(SessionNotes.ForScout(e));
                session.GuiderStateChanged += (_, e) => Note(SessionNotes.ForGuiderTransition(e.OldState, e.NewState));

                // The terminal phases are noted once the run (its Finalise included) has ended, in the finally below.
                // A named local, so it can be unsubscribed there and never keep a stale session alive.
                void OnPhaseChanged(object? _, SessionPhaseChangedEventArgs e) => Note(SessionNotes.ForPhase(e.NewPhase));
                session.PhaseChanged += OnPhaseChanged;

                // The run's prompts on the live view, as a flat run's are: the ONE wiring. A full session subscribed
                // nothing, so the manual panel its end-of-session flats ask for was declined unseen (P5b part 5).
                var prompts = LiveSessionPrompts.ShowOn(session, liveSessionState, appState);

                // RunAsync includes Finalise: run as tracked background task so:
                // 1. UI stays responsive (signal handler returns immediately)
                // 2. DrainAsync at shutdown waits for Finalise to complete
                handedToRun = true;
                tracker.Run(async () =>
                {
                    try
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
                            prompts.Dispose();
                            // A prompt left open by a cancel mid-wait was already declined by the run; drop the overlay.
                            liveSessionState.PendingPrompt = null;
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

                            // Session.FailureReason carries the user-facing "which device, what to check" text.
                            if (SessionNotes.ForRunEnd(liveSessionState.Phase, session.FailureReason) is { } ended)
                            {
                                appState.AppendNotification(timeProvider.GetUtcNow(), ended.Severity, ended.Message);
                            }
                            else
                            {
                                appState.StatusMessage = null;
                            }
                            appState.NeedsRedraw = true;
                        }
                    }
                    finally
                    {
                        // Last of all, so a shutdown waiting on it sees the run ENDED: Finalise, and the
                        // bookkeeping above, included.
                        ended.TrySetResult();
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
            finally
            {
                if (!handedToRun)
                {
                    ended.TrySetResult();
                }
            }
        }
    }
}
