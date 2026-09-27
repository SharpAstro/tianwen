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
    /// Builds an <see cref="ISession"/> from the active profile and launches
    /// <see cref="ISession.RunFlatsOnlyAsync"/> as a tracked background task for the Flats live-session
    /// mode. The counterpart to <see cref="SessionBootstrapper"/> for on-demand flats: no schedule, no
    /// planner proposals -- just a self-contained connect -> cool -> capture -> finalise cycle. Sets
    /// <see cref="LiveSessionState.ActiveSession"/> (so the per-frame <c>PollSession</c> mirrors the phase,
    /// current-activity, and captured frames into the preview) WITHOUT setting
    /// <see cref="LiveSessionState.IsRunning"/> -- the tab keeps the preview layout + mode pill, and the run
    /// is tracked via <see cref="LiveSessionState.FlatsCts"/>. Container-free: the caller resolves
    /// <see cref="ISessionFactory"/> and re-validates preconditions.
    /// </summary>
    public static class FlatsBootstrapper
    {
        public static async Task BuildAndStartAsync(
            ISessionFactory factory,
            GuiAppState appState,
            SessionTabState sessionState,
            LiveSessionState liveSessionState,
            Profile profile,
            FlatIlluminationChoice source,
            int flatsPerFilter,
            double siteLatitude,
            double siteLongitude,
            BackgroundTaskTracker tracker,
            ITimeProvider timeProvider,
            ILogger logger,
            CancellationToken parentToken)
        {
            // Collapse the UI choice back into the session's (source, period) pair. Calibrator ignores
            // the period; sky maps dusk/dawn through.
            var (flatSource, period) = source switch
            {
                FlatIlluminationChoice.SkyDusk => (FlatIlluminationSource.TwilightSky, TwilightPeriod.Dusk),
                FlatIlluminationChoice.SkyDawn => (FlatIlluminationSource.TwilightSky, TwilightPeriod.Dawn),
                _ => (FlatIlluminationSource.Calibrator, TwilightPeriod.Dawn),
            };

            var flatsCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);

            // Completed on EVERY way out (see SessionBootstrapper): the early return and both catches
            // through the outer finally, and the tracked run's own finally once the run has ended.
            var ended = liveSessionState.BeginFlatRun();
            var handedToRun = false;
            try
            {
                liveSessionState.FlatsCts = flatsCts;
                liveSessionState.Mode = LiveSessionMode.Flats;
                liveSessionState.FlatStatusMessage = "Starting flat run…";
                liveSessionState.NeedsRedraw = true;
                appState.ActiveTab = GuiTab.LiveSession;
                appState.StatusMessage = "Initialising flat run…";
                appState.NeedsRedraw = true;

                // Populate the device hub so Create can resolve profile URIs (mirrors the CLI path).
                await factory.InitializeAsync(flatsCts.Token);

                // Cool to the same setpoint the imaging tab uses, so on-demand flats match light-frame
                // temperature (a no-op on cameras without a cooler).
                var setpointTempC = sessionState.CameraSettings is { Count: > 0 }
                    ? sessionState.CameraSettings[0].SetpointTempC
                    : sessionState.Configuration.SetpointCCDTemperature.TempC;
                var config = sessionState.Configuration with
                {
                    SiteLatitude = siteLatitude,
                    SiteLongitude = siteLongitude,
                    FlatSource = flatSource,
                    FlatsPerFilter = flatsPerFilter,
                    SetpointCCDTemperature = new SetpointTemp(setpointTempC, SetpointTempKind.Normal),
                };

                ISession session;
                try
                {
                    session = factory.Create(profile.ProfileId, config, ReadOnlySpan<ScheduledObservation>.Empty);
                }
                catch (ArgumentException ex)
                {
                    logger.LogError(ex, "Flat run: could not build session from profile");
                    appState.AppendNotification(timeProvider.GetUtcNow(),
                        NotificationSeverity.Error, $"Flats failed to start: {ex.Message}");
                    liveSessionState.FlatsCts = null;
                    liveSessionState.NeedsRedraw = true;
                    flatsCts.Dispose();
                    return;
                }

                // ActiveSession WITHOUT IsRunning: PollSession mirrors phase / current-activity / captured
                // frames into the preview every frame, but the tab stays in the preview layout (mode pill
                // visible), and the flat run is gated on FlatsCts rather than IsRunning.
                liveSessionState.ActiveSession = session;

                void OnPhaseChanged(object? _, SessionPhaseChangedEventArgs e)
                {
                    if (SessionNotes.FlatStatusForPhase(e.NewPhase) is { } msg)
                    {
                        liveSessionState.FlatStatusMessage = msg;
                        liveSessionState.NeedsRedraw = true;
                    }
                }
                session.PhaseChanged += OnPhaseChanged;

                // Surface session-driven user prompts (e.g. "switch on the manual panel"). Fires on the
                // session's background thread; a reference assignment + NeedsRedraw is all that crosses over.
                void OnPromptRequested(object? _, SessionPromptEventArgs e)
                {
                    // Nobody can see an overlay once the display is gone: answer as an unattended caller
                    // would, rather than hold the run for a frame that will never be drawn.
                    if (liveSessionState.AnswerPromptsUnattended)
                    {
                        e.Respond(e.DefaultIfUnanswerable);
                        return;
                    }
                    liveSessionState.PendingPrompt = e;
                    liveSessionState.NeedsRedraw = true;
                    appState.ActiveTab = GuiTab.LiveSession;
                    appState.NeedsRedraw = true;

                    // Off the bar once it settles by any route: the session withdraws it when the run is
                    // cancelled while it waits, and a prompt it no longer waits on must not stay up.
                    _ = e.Settled.ContinueWith(_ =>
                    {
                        if (liveSessionState.TryClearPendingPrompt(e))
                        {
                            liveSessionState.NeedsRedraw = true;
                        }
                    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                }
                session.PromptRequested += OnPromptRequested;

                var started = SessionNotes.ForRunStart(flatRun: true);
                appState.AppendNotification(timeProvider.GetUtcNow(), started.Severity, started.Message);
                appState.NeedsRedraw = true;

                handedToRun = true;
                tracker.Run(async () =>
                {
                    try
                    {
                        try
                        {
                            await session.RunFlatsOnlyAsync(period, flatsCts.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogError(ex, "Flat run failed");
                        }
                        finally
                        {
                            session.PhaseChanged -= OnPhaseChanged;
                            session.PromptRequested -= OnPromptRequested;
                            // Clear any prompt left open by a cancel mid-wait (the session's WaitAsync already
                            // resolved it to "decline"; this just drops the stale overlay).
                            liveSessionState.PendingPrompt = null;

                            var ended = SessionNotes.ForFlatRunEnd(liveSessionState.Phase, session.FailureReason);
                            liveSessionState.FlatStatusMessage = ended.Message;
                            appState.AppendNotification(timeProvider.GetUtcNow(), ended.Severity, ended.Message);

                            // Detach the session so the tab returns to normal preview polling. Leaving Mode ==
                            // Flats keeps the terminal status + Cancel(->Preview) visible until the user acts.
                            liveSessionState.ActiveSession = null;
                            liveSessionState.FlatsCts = null;
                            try { await session.DisposeAsync(); }
                            catch (Exception ex) { logger.LogWarning(ex, "Flat run: session dispose failed"); }
                            flatsCts.Dispose();
                            liveSessionState.NeedsRedraw = true;
                            appState.NeedsRedraw = true;
                        }
                    }
                    finally
                    {
                        // Last of all, so a shutdown waiting on it sees the run ENDED, its finalise and
                        // the session's dispose included.
                        ended.TrySetResult();
                    }
                }, "Flat run");
            }
            catch (OperationCanceledException)
            {
                appState.AppendNotification(timeProvider.GetUtcNow(),
                    NotificationSeverity.Warning, "Flat run cancelled");
                liveSessionState.FlatsCts = null;
                liveSessionState.ActiveSession = null;
                flatsCts.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start flat run");
                appState.AppendNotification(timeProvider.GetUtcNow(),
                    NotificationSeverity.Error, $"Flats failed to start: {ex.Message}");
                liveSessionState.FlatsCts = null;
                liveSessionState.ActiveSession = null;
                flatsCts.Dispose();
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
