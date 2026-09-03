using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    internal async ValueTask ObservationLoopAsync(CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;
        var sessionStartTime = await GetMountUtcNowAsync(cancellationToken);
        var sessionEndTime = await SessionEndTimeAsync(sessionStartTime, cancellationToken);

        ScheduledObservation? observation;
        while ((observation = ActiveObservation) is not null
            && await GetMountUtcNowAsync(cancellationToken) < sessionEndTime
            && !cancellationToken.IsCancellationRequested
        )
        {
            // Honor the scheduler's allocated start: wait until (Start - lead) before committing
            // to the slew, so the altitude-optimised slot allocation actually happens at the
            // allocated time. Waiting BEFORE the slew (not after) avoids running the RA worm toward
            // the meridian and going stale on an early centering/refocus/guider start. Same-Start
            // and past-Start schedules (hosted API, legacy, existing tests) short-circuit instantly,
            // preserving the linear-advance behaviour.
            var startOutcome = await WaitForScheduledStartAsync(observation, sessionEndTime, cancellationToken);
            if (startOutcome == ScheduledStartOutcome.SessionEnded)
            {
                _logger.LogInformation(
                    "Scheduled start of {Observation} is beyond session end; ending observation loop.", observation);
                break;
            }

            if (!await mount.Driver.EnsureTrackingAsync(cancellationToken: cancellationToken))
            {
                _logger.LogError("Failed to enable tracking of {Mount}.", mount);
                return;
            }

            _currentActivity = $"Slewing to {observation.Target.Name}\u2026";
            _logger.LogInformation("Stop guiding to start slewing mount to target {Observation}.", observation);
            await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            // Set camera target early so centering plate-solve has correct metadata
            for (var i = 0; i < Setup.Telescopes.Length; i++)
            {
                Setup.Telescopes[i].Camera.Driver.Target = observation.Target;
            }

            double hourAngleAtSlewTime;
            var pierSideAtSlewTime = PointingState.Unknown;
            try
            {
                (var postCondition, hourAngleAtSlewTime) = await ResilientInvokeAsync(
                    mount.Driver,
                    ct => mount.Driver.BeginSlewToTargetAsync(observation.Target, Configuration.MinHeightAboveHorizon, ct),
                    ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);
                if (postCondition is SlewPostCondition.SlewNotPossible or SlewPostCondition.TargetBelowHorizonLimit)
                {
                    var maxWait = Configuration.MaxWaitForRisingTarget ?? TimeSpan.FromMinutes(15);

                    // If target is rising and will clear the horizon soon, wait for it
                    if (postCondition is SlewPostCondition.TargetBelowHorizonLimit
                        && await EstimateTimeUntilTargetRisesAsync(observation.Target, Configuration.MinHeightAboveHorizon, maxWait, cancellationToken) is { } waitTime
                        && waitTime > TimeSpan.Zero)
                    {
                        _logger.LogInformation(
                            "Target {Target} is rising, waiting {WaitMinutes:F0} min until it clears {MinAlt}°.",
                            observation.Target, waitTime.TotalMinutes, Configuration.MinHeightAboveHorizon);
                        await _timeProvider.SleepAsync(waitTime, cancellationToken);

                        // Retry slew after waiting
                        (postCondition, hourAngleAtSlewTime) = await ResilientInvokeAsync(
                            mount.Driver,
                            ct => mount.Driver.BeginSlewToTargetAsync(observation.Target, Configuration.MinHeightAboveHorizon, ct),
                            ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);
                    }

                    // Still not available: try spare targets, then advance
                    if (postCondition is SlewPostCondition.SlewNotPossible or SlewPostCondition.TargetBelowHorizonLimit)
                    {
                        if (Observations.TryGetNextSpare(_activeObservation, ref _spareIndex) is { } spare)
                        {
                            _logger.LogInformation("Primary target {Target} not available ({PostCondition}), trying spare target {SpareTarget}.",
                                observation.Target, postCondition, spare.Target);
                            observation = spare;

                            (postCondition, hourAngleAtSlewTime) = await ResilientInvokeAsync(
                                mount.Driver,
                                ct => mount.Driver.BeginSlewToTargetAsync(spare.Target, Configuration.MinHeightAboveHorizon, ct),
                                ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);
                            if (postCondition is SlewPostCondition.SlewNotPossible or SlewPostCondition.TargetBelowHorizonLimit)
                            {
                                _ = AdvanceObservation();
                                continue;
                            }
                        }
                        else
                        {
                            _ = AdvanceObservation();
                            continue;
                        }
                    }
                }
                else if (postCondition is SlewPostCondition.Slewing)
                {
                    if (!await ResilientInvokeAsync(
                            mount.Driver,
                            ct => mount.Driver.WaitForSlewCompleteAsync(PollDeviceStatesAsync, ct),
                            ResilientCallOptions.IdempotentRead, cancellationToken).ConfigureAwait(false))
                    {
                        _logger.LogError("Failed to complete slewing of mount {Mount}", mount);

                        throw new InvalidOperationException($"Failed to complete slewing of mount {mount} while slewing to {observation.Target}");
                    }

                    // Recompute hour angle now that the mount is pointing at the target
                    // (BeginSlewToTargetAsync returns the pre-slew HA, which may be on a different pier side)
                    hourAngleAtSlewTime = await ResilientInvokeAsync(
                        mount.Driver, mount.Driver.GetHourAngleAsync,
                        ResilientCallOptions.IdempotentRead, cancellationToken);

                    // Capture pier side baseline so the imaging-loop flip state machine can later
                    // detect an out-of-band flip (firmware auto-flip past limit, handbox, :MNe/:MNw).
                    pierSideAtSlewTime = await CatchAsync(mount.Driver.GetSideOfPierAsync, cancellationToken, PointingState.Unknown);

                    // Iterative plate-solve + sync + reslew centering
                    if (!await CenterOnTargetAsync(observation.Target, 0, thresholdArcmin: 1.0, maxAttempts: 3, cancellationToken))
                    {
                        _logger.LogWarning("Centering on {Target} did not converge, continuing with current pointing.", observation.Target);
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unknown post condition {postCondition} after slewing to target {observation.Target}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while slewing to {Observation}, retrying", observation);
                continue;
            }

            // FOV obstruction probe: predictive scout + altitude nudge BEFORE guider/exposure
            // commitment. Catches "behind a tree" cases that would otherwise burn through
            // auto-focus + several full-length exposures before the deterioration check trips.
            // First observation has no previous baseline → ScoutAndProbeAsync returns Healthy.
            if (await RunObstructionScoutAsync(observation, cancellationToken) is { } scoutDecision)
            {
                if (scoutDecision == ScoutOutcome.Advance)
                {
                    _ = AdvanceObservation();
                    continue;
                }
                // ScoutOutcome.Proceed → fall through to guider start
            }

            _currentActivity = $"Starting guider on {observation.Target.Name}\u2026";
            var guidingSuccess = await ResilientInvokeAsync(
                guider.Driver,
                ct => guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, ct),
                ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Cancellation requested, abort setting up guider \"{GuiderName}\" and quit observation loop.", guider.Driver);
                break;
            }
            else if (!guidingSuccess)
            {
                _logger.LogError("Skipping target {Observation} as starting guider \"{GuiderName}\" failed after trying {GuiderTries} times.", observation, guider.Driver, Configuration.GuidingTries);
                _ = AdvanceObservation();
                continue;
            }

            // Optionally refocus when switching to a new target
            if (Configuration.AlwaysRefocusOnNewTarget && !_baselineByObservation.ContainsKey(ActiveObservationIndex))
            {
                _logger.LogInformation("Refocusing for new target {Target}.", observation.Target);
                await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

                if (!await AutoFocusAllTelescopesAsync(cancellationToken))
                {
                    _logger.LogWarning("Auto-focus did not converge for all telescopes on new target, proceeding.");
                }

                await ResilientInvokeAsync(
                    guider.Driver,
                    ct => guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, ct),
                    ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);
            }

            var imageLoopStart = await GetMountUtcNowAsync(cancellationToken);
            var imageLoopResult = await ImagingLoopAsync(observation, hourAngleAtSlewTime, pierSideAtSlewTime, cancellationToken).ConfigureAwait(false);
            if (imageLoopResult is ImageLoopNextAction.AdvanceToNextObservation)
            {
                _ = AdvanceObservation();
                continue;
            }
            else if (imageLoopResult is ImageLoopNextAction.RepeatCurrentObservation)
            {
                // TODO: increase test coverage for condition recovery, add more signals (mean background, etc.)
                continue;
            }
            else if (imageLoopResult is ImageLoopNextAction.DeviceUnrecoverable)
            {
                _logger.LogError("Driver escalation tripped during {Observation} after {Runtime:c}; ending observation loop cleanly.",
                    observation, await GetMountUtcNowAsync(cancellationToken) - imageLoopStart);
                break;
            }
            else if (imageLoopResult is ImageLoopNextAction.LimitReached)
            {
                // Not an error in the rig, so it does not advance to the next target either: every
                // remaining target is reached from the same mount, and the mount is now stopped or
                // parked at its limit.
                _logger.LogError("Mount safety limit reached during {Observation} after {Runtime:c} ({Verdict}); ending observation loop cleanly.",
                    observation, await GetMountUtcNowAsync(cancellationToken) - imageLoopStart, _limitVerdict.Describe());
                // The guider is still correcting a star that no longer moves, and on a stopped mount every
                // RA correction is a real axis move. Finalise stops it too, but flats may run first.
                await CatchAsync(ct => guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), ct), cancellationToken);
                break;
            }
            else
            {
                _logger.LogError("Imaging loop for {Observation} did not complete successfully, total runtime: {TotalRuntime:c}", observation, await GetMountUtcNowAsync(cancellationToken) - imageLoopStart);
                break;
            }
        } // end observation loop
    }

    /// <summary>
    /// Imaging loop for one observation, handles exposing frames + dithering, handles meridian flip.
    /// </summary>
    /// <param name="observation">Observation to image.</param>
    /// <param name="hourAngleAtSlewTime">provide hour angle current as of start of session, used to calculate meridian flip.</param>
    /// <param name="pierSideAtSlewTime">Pier side reported by the mount at slew completion. Defaults to <see cref="PointingState.Unknown"/>
    /// which disables out-of-band-flip detection: direct test callers without a real mount can omit this.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>loop result</returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <summary>
    /// Backstop for how long the imaging loop defers to the guider recovering a lock in place
    /// before forcing a clean restart. Tunable via
    /// <see cref="SessionConfiguration.GuiderRecoveryGrace"/>; see that doc for the rationale.
    /// </summary>
    private TimeSpan GuiderRecoveryGrace =>
        Configuration.GuiderRecoveryGrace ?? SessionConfiguration.DefaultGuiderRecoveryGrace;

    /// <summary>How <see cref="ImagingLoopAsync"/> should react to the guider's reported health.
    /// See <see cref="DecideGuiderIntervention"/>.</summary>
    internal enum GuiderInterventionAction
    {
        /// <summary>Guiding -- proceed with imaging.</summary>
        Proceed,
        /// <summary>Recovering a lock in place (Calibrating/Settling) within the grace budget --
        /// defer the next frame and let the driver finish; do NOT restart (that fights it).</summary>
        DeferForRecovery,
        /// <summary>Stopped, or "recovering" past the grace budget (a stuck settle) -- the session
        /// must (re)start guiding, and reschedule the target if that fails.</summary>
        Restart,
    }

    /// <summary>
    /// Decides how <see cref="ImagingLoopAsync"/> reacts to the guider's reported state. The
    /// built-in guider drops out of "Guiding" while it recovers a lock in place -- re-acquiring a
    /// star after a loss, or recalibrating after a divergence (states "Calibrating"/"Settling") --
    /// and bounds that recovery itself. Restarting from the session during that window fights the
    /// driver: GuideAsync throws "cannot start guiding in state Calibrating", the retry backs off,
    /// and the target is rescheduled even though guiding was recovering. So defer while it recovers
    /// in place, and only (re)start once it has genuinely stopped -- or, as a backstop, if a
    /// never-completing settle drags on past <paramref name="recoveryGrace"/>.
    /// </summary>
    internal static GuiderInterventionAction DecideGuiderIntervention(
        bool isGuiding, string? guiderState, TimeSpan recoveringFor, TimeSpan recoveryGrace)
    {
        if (isGuiding)
        {
            return GuiderInterventionAction.Proceed;
        }

        // "Calibrating"/"Settling" => the driver is recovering a lock in place (see the GetStatusAsync
        // state mappings in BuiltInGuiderDriver / FakeGuider / OpenPHD2). Give it room to the backstop.
        var recoveringInPlace = guiderState is "Calibrating" or "Settling";
        return recoveringInPlace && recoveringFor < recoveryGrace
            ? GuiderInterventionAction.DeferForRecovery
            : GuiderInterventionAction.Restart;
    }

    internal async ValueTask<ImageLoopNextAction> ImagingLoopAsync(ScheduledObservation observation, double hourAngleAtSlewTime, PointingState pierSideAtSlewTime = PointingState.Unknown, CancellationToken cancellationToken = default)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;
        var scopes = Setup.Telescopes.Length;

        // Ensure arrays are initialized (tests may call ImagingLoopAsync directly)
        if (_cameraStates.Length != scopes)
        {
            _cameraStates = new CameraExposureState[scopes];
        }
        if (_lastCapturedImages.Length != scopes)
        {
            _lastCapturedImages = new Image?[scopes];
            _viewerChannels = new Imaging.Channel[]?[scopes];
        }
        if (_lastFrameMetrics.Length != scopes)
        {
            _lastFrameMetrics = new FrameMetrics[scopes];
            _frameMetricsHistory = CreateFrameMetricsHistory(scopes);
        }
        var frameNumbers = new int[scopes];

        // Per-telescope filter plan state
        // The plan is an altitude ladder: narrowband first (index 0), broadband last.
        // Ascending = true: traverse forward (0 → N-1), target is rising toward transit.
        // Ascending = false: traverse backward (N-1 → 0), target is descending after transit.
        var filterPlans = new FilterExposure[scopes][];
        var filterCursors = new int[scopes];
        var filterFrameCounters = new int[scopes];
        var filterAscending = hourAngleAtSlewTime < 0; // HA < 0 means east of meridian (rising)
        // A GEM flips at most once per target. Set after a successful (or detected) flip so the HA-window
        // decision can never re-fire for this observation; the backstop behind the destination-side gate.
        var hasFlipped = false;

        // A pending flip belongs to the target that is crossing, so the countdown starts unknown for each
        // observation and is only stamped once this loop actually reads HA below.
        MeridianFlipUtc = null;

        // The meridian flip + pre-meridian obstruction pause are a GERMAN-equatorial concern ONLY: the GEM's
        // counterweight bar would collide with the pier if it tracked past the meridian on the same side.
        // Fork/equatorial (AlignmentMode.Polar, OTA rides between the fork arms) and Alt-Az mounts track
        // straight across the meridian and never flip, so the entire detection block below is skipped for
        // them. Read once (cheap, effectively constant per session); default to GermanPolar on a read failure
        // so a transient glitch can never stop a real GEM from flipping.
        var isGermanEquatorial =
            await CatchAsync(mount.Driver.GetAlignmentAsync, cancellationToken, AlignmentMode.GermanPolar) == AlignmentMode.GermanPolar;
        var currentSubExposuresSec = new int[scopes];

        for (var i = 0; i < scopes; i++)
        {
            var camera = Setup.Telescopes[i].Camera;
            camera.Driver.Target = observation.Target;

            // Each telescope gets its own copy of the filter plan.
            // Single-position filter wheels (manual holders) get a single-entry plan
            // using the observation's first sub-exposure: they can't switch filters.
            var hasMultiFilterWheel = Setup.Telescopes[i].FilterWheel?.Driver is { Connected: true, Filters.Count: > 1 };
            filterPlans[i] = observation.FilterPlan.IsDefaultOrEmpty || !hasMultiFilterWheel
                ? [new FilterExposure(-1, observation.SubExposure)]
                : [.. observation.FilterPlan];

            // Start at beginning (ascending/rising) or end (descending/setting) of plan
            filterCursors[i] = filterAscending ? 0 : filterPlans[i].Length - 1;

            // Initialize with the starting filter entry's exposure
            currentSubExposuresSec[i] = (int)Math.Ceiling(filterPlans[i][filterCursors[i]].SubExposure.TotalSeconds);
        }

        // Tick = GCD/6, clamped to [1s, 5s]. Fast enough for responsive monitoring
        // (guiding, pier side, altitude) while keeping timer callback counts manageable.
        // GCD and LCM are kept for dithering cadence.
        var allSubExposuresSec = new HashSet<int>();
        for (var i = 0; i < scopes; i++)
        {
            foreach (var entry in filterPlans[i])
            {
                allSubExposuresSec.Add((int)Math.Ceiling(entry.SubExposure.TotalSeconds));
            }
        }

        var allExposuresArray = allSubExposuresSec.ToArray();
        var gcdSec = (int)GCD(allExposuresArray);
        var lcmSec = (int)LCM(gcdSec, allExposuresArray);
        var tickSec = Math.Clamp(gcdSec / 6, 1, 5);
        var tickDuration = TimeSpan.FromSeconds(tickSec);
        var ditherEveryNTicks = Configuration.DitherEveryNthFrame * (lcmSec / tickSec);
        var expStartTimes = new DateTimeOffset[scopes];
        var expTicks = new int[scopes];
        var tickCount = 0;

        var imageWriteQueue = new Queue<QueuedImageWrite>();
        ImageLoopNextAction? next = null;
        var maxTicks = (int)(observation.Duration.TotalSeconds / tickSec);

        _currentActivity = null; // clear; PhaseStatusText takes over for imaging
        _logger.LogInformation(
            "ImagingLoop starting for {Target}: {FilterCount} filters, direction={Direction}, tick={TickSec}s, duration={Duration}, GCD={GCD}s.",
            observation.Target, observation.FilterPlan.Length,
            filterAscending ? "ascending" : "descending",
            tickSec, observation.Duration, gcdSec);
        _logger.LogInformation(
            "Memory at ImagingLoop start: working={WorkingMB:F0}MB, managed={ManagedMB:F0}MB",
            Environment.WorkingSet / (1024.0 * 1024),
            GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024));

        using var ticker = new PeriodicTimer(tickDuration, _timeProvider.System);

        // When the guider is not guiding, this stamps when it first dropped out so we can tell a
        // brief in-place recovery (defer to it) from a stuck/stopped guider (restart). Reset
        // whenever guiding is healthy or after a successful restart. See DecideGuiderIntervention.
        long? guiderRecoveryStartedTicks = null;

        while (!cancellationToken.IsCancellationRequested
            && mount.Driver.Connected
            && await CatchAsync(mount.Driver.IsTrackingAsync, cancellationToken)
        )
        {
            tickCount++;
            Interlocked.Increment(ref _imagingLoopTicks);

            // Poll all device states (focuser temp/pos/moving, mount RA/Dec/HA/pier)
            await PollDeviceStatesAsync(cancellationToken);

            var isGuiding = await CatchAsync(guider.Driver.IsGuidingAsync, cancellationToken).ConfigureAwait(false);

            // Poll guider state, settle progress, and exposure each tick
            try
            {
                var (appState, _) = await guider.Driver.GetStatusAsync(cancellationToken);
                UpdateGuiderState(appState);
            }
            catch { /* ignore */ }

            try { _guiderSettleProgress = await guider.Driver.GetSettleProgressAsync(cancellationToken); } catch { /* ignore */ }
            try { _guideExposure = await guider.Driver.ExposureTimeAsync(cancellationToken); } catch { /* ignore */ }

            // Poll guide stats each tick for the guide graph (also during settling, guide loop still corrects)
            var isSettlingOrGuiding = isGuiding || _guiderState is "Settling";
            if (isSettlingOrGuiding)
            {
                GuideStats? guideStats = null;
                try { guideStats = await guider.Driver.GetStatsAsync(cancellationToken); } catch { /* ignore */ }
                if (guideStats is { } gs)
                {
                    UpdateGuideStats(gs);
                    // Use real per-frame errors when available, fall back to synthetic
                    var raErr = gs.LastRaErr ?? gs.RaRMS * (new Random(tickCount).NextDouble() * 2 - 1);
                    var decErr = gs.LastDecErr ?? gs.DecRMS * (new Random(tickCount + 1).NextDouble() * 2 - 1);
                    var isDither = _ditherPending;
                    if (isDither) _ditherPending = false;
                    var isSettling = _guiderState is "Settling";
                    AppendGuideErrorSample(new GuideErrorSample(
                        _timeProvider.GetUtcNow(), raErr, decErr,
                        gs.LastRaPulseMs ?? 0, gs.LastDecPulseMs ?? 0,
                        isDither, isSettling));
                }
            }

            if (isGuiding)
            {
                guiderRecoveryStartedTicks = null; // healthy -> reset the recovery clock
            }
            else
            {
                guiderRecoveryStartedTicks ??= _timeProvider.GetTimestamp();
                var recoveringFor = _timeProvider.GetElapsedTime(guiderRecoveryStartedTicks.Value);

                if (DecideGuiderIntervention(isGuiding, _guiderState, recoveringFor, GuiderRecoveryGrace)
                    is GuiderInterventionAction.DeferForRecovery)
                {
                    // The guider is recovering a lock in place (re-acquire after a star loss,
                    // recalibrate after a divergence). It bounds that itself; restarting here would
                    // fight it (see DecideGuiderIntervention). Defer the next frame and let it finish.
                    _logger.LogDebug(
                        "Guider recovering in place ({GuiderState}) on {Target} for {Elapsed:F0}s; deferring next frame instead of restarting.",
                        _guiderState, observation.Target.Name, recoveringFor.TotalSeconds);
                    await ticker.WaitForNextTickAsync(cancellationToken);
                    continue;
                }

                // Restart: genuinely stopped, or a stuck settle past the grace backstop. Surface why
                // it stopped, force-stop any stuck recovery so the (re)start isn't rejected, then
                // restart -- rescheduling the target only if that fails.
                while (_guiderEvents.TryDequeue(out var guiderEvent))
                {
                    if (guiderEvent is GuidingErrorEventArgs guidingError)
                    {
                        _logger.LogWarning("Guider reported an error before stopping on {Target}: {Message}",
                            observation.Target.Name, guidingError.Message);
                    }
                }

                if (_guiderState is "Calibrating" or "Settling")
                {
                    _logger.LogWarning(
                        "Guider stuck recovering ({GuiderState}) for {Elapsed:F0}s on {Target}; forcing a clean restart.",
                        _guiderState, recoveringFor.TotalSeconds, observation.Target.Name);
                    await CatchAsync(ct => guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(5), ct), cancellationToken).ConfigureAwait(false);
                }

                var guiderRestartedSuccess =
                    await CatchAsync(guider.Driver.ConnectAsync, cancellationToken) &&
                    await ResilientInvokeAsync(
                        guider.Driver,
                        ct => guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, ct),
                        ResilientCallOptions.NonIdempotentAction, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Cancellation requested, abort setting up guider \"{GuiderName}\" and quit imaging loop for observation {Observation}.", guider.Driver, observation);
                    next = ImageLoopNextAction.BreakObservationLoop;
                    break;
                }
                else if (!guiderRestartedSuccess)
                {
                    _logger.LogError("Reschedule target {Observation} as starting guider \"{GuiderName}\" failed after trying {GuiderTries} times.", observation, guider.Driver, Configuration.GuidingTries);
                    next = ImageLoopNextAction.RepeatCurrentObservation;
                    break;
                }

                guiderRecoveryStartedTicks = null; // restarted OK -> reset the recovery clock
            }

            for (var i = 0; i < scopes; i++)
            {
                var telescope = Setup.Telescopes[i];
                var camerDriver = telescope.Camera.Driver;
                if (await camerDriver.GetCameraStateAsync(cancellationToken) is CameraState.Idle)
                {
                    // Advance filter cursor if the current batch is complete
                    var plan = filterPlans[i];
                    var cursor = filterCursors[i];
                    var currentEntry = plan[cursor];

                    if (filterFrameCounters[i] >= currentEntry.Count && plan.Length > 1)
                    {
                        var prevCursor = cursor;
                        filterFrameCounters[i] = 0;
                        cursor = AdvanceFilterCursor(ref filterCursors[i], plan.Length, filterAscending);
                        currentEntry = plan[cursor];

                        _logger.LogInformation(
                            "Telescope #{TelescopeNumber}: filter ladder step {PrevCursor} → {Cursor} ({Direction}), next filter position {FilterPosition}.",
                            i + 1, prevCursor, cursor, filterAscending ? "ascending" : "descending", currentEntry.FilterPosition);
                    }

                    // Switch filter if needed
                    if (currentEntry.FilterPosition >= 0 && telescope.FilterWheel?.Driver is { Connected: true } filterWheelDriver)
                    {
                        await SwitchFilterIfNeededAsync(i, filterWheelDriver, currentEntry.FilterPosition, cancellationToken);
                    }

                    // Stamp per-exposure FITS denorm fields (focuser position, filter)
                    // via the shared helper -- same path used by polar alignment and the
                    // live preview button so the three never drift on FITS metadata.
                    // Target/site/optics are managed separately by Session.Lifecycle and
                    // the scheduled-observation logic, so we skip those args here.
                    await CameraExposureActions.StampDenormAsync(
                        camerDriver,
                        otaName: telescope.Name,
                        focalLengthMm: telescope.FocalLength,
                        apertureMm: telescope.Aperture,
                        focuser: telescope.Focuser?.Driver,
                        filterWheel: telescope.FilterWheel?.Driver,
                        logger: _logger,
                        ct: cancellationToken).ConfigureAwait(false);

                    var subExposureSec = (int)Math.Ceiling(currentEntry.SubExposure.TotalSeconds);
                    currentSubExposuresSec[i] = subExposureSec;
                    var frameExpTime = TimeSpan.FromSeconds(subExposureSec);
                    expStartTimes[i] = await ResilientInvokeAsync(
                        camerDriver,
                        ct => camerDriver.StartExposureAsync(frameExpTime, cancellationToken: ct),
                        ResilientCallOptions.NonIdempotentAction, cancellationToken);
                    expTicks[i] = subExposureSec / tickSec;
                    filterFrameCounters[i]++;
                    var frameNo = ++frameNumbers[i];

                    var focuserTemp = await CatchAsync(async ct => telescope.Focuser?.Driver is { Connected: true } f ? await f.GetTemperatureAsync(ct) : double.NaN, cancellationToken, double.NaN);
                    var focuserMoving = await CatchAsync(async ct => telescope.Focuser?.Driver is { Connected: true } f && await f.GetIsMovingAsync(ct), cancellationToken, false);
                    _cameraStates[i] = new CameraExposureState(i, expStartTimes[i], frameExpTime, frameNo,
                        camerDriver.Filter.DisplayName, camerDriver.FocusPosition, Devices.CameraState.Exposing,
                        focuserTemp, focuserMoving);

                    _logger.LogInformation("Camera #{CameraNumber} {CamerName} starting {ExposureStartTime} exposure of frame #{FrameNo} (filter: {Filter}).",
                        i + 1, camerDriver.Name, frameExpTime, frameNo, camerDriver.Filter);
                }
            }

            await WriteQueuedImagesToFitsFilesAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Cancellation requested, all images in queue written to disk, abort image acquisition and quit imaging loop");
                next = ImageLoopNextAction.BreakObservationLoop;
                break;
            }

            // A mechanical limit was hit and acted on by the poll. Nothing is broken -- the rig
            // reached the edge of where it may point -- so drain writes and end the night the same
            // way an unrecoverable driver does, but under its own name.
            if (Volatile.Read(ref _limitReached))
            {
                _logger.LogError("Mount safety limit reached ({Verdict}); ending the observation.", _limitVerdict.Describe());
                await WriteQueuedImagesToFitsFilesAsync();
                next = ImageLoopNextAction.LimitReached;
                break;
            }

            // Driver-fault escalation: if any driver has burned through the allowed
            // reconnect budget, drain pending writes and bail out cleanly.
            if (TryFindEscalatedDriver() is { } escalated)
            {
                _logger.LogError(
                    "Driver {Device} crossed fault threshold ({Count}/{Threshold}); aborting observation as DeviceUnrecoverable.",
                    escalated.Name, GetFaultCount(escalated), Configuration.DeviceFaultEscalationThreshold);
                await WriteQueuedImagesToFitsFilesAsync();
                next = ImageLoopNextAction.DeviceUnrecoverable;
                break;
            }

            await ticker.WaitForNextTickAsync(cancellationToken);

            var imageFetchSuccess = new BitVector32(scopes);
            for (var i = 0; i < scopes && !cancellationToken.IsCancellationRequested; i++)
            {
                var tick = --expTicks[i];

                var camDriver = Setup.Telescopes[i].Camera.Driver;
                imageFetchSuccess[i] = false;
                if (tick <= 0)
                {
                    var frameExpTime = TimeSpan.FromSeconds(currentSubExposuresSec[i]);
                    var frameNo = frameNumbers[i];
                    var polled = TimeSpan.Zero;

                    // How well THIS sub was guided, for its header. Stamped here because the statistic is
                    // only complete once the shutter has closed and GetImageAsync (just below) is the one
                    // place an ImageMeta is built. Null on an unguided rig, which writes no cards.
                    camDriver.GuideStats = GuideStatistics.OverExposure(
                        GuideSamples, expStartTimes[i], frameExpTime);
                    do // wait for image loop
                    {
                        if (await ResilientInvokeAsync(
                                camDriver, camDriver.GetImageAsync,
                                ResilientCallOptions.IdempotentRead, cancellationToken) is { Width: > 0, Height: > 0 } image)
                        {
                            imageFetchSuccess[i] = true;
                            _cameraStates[i] = _cameraStates[i] with { State = Devices.CameraState.Download };

                            _logger.LogInformation("Camera #{CameraNumber} {CameraName} finished {ExposureStartTime} exposure of frame #{FrameNo}",
                                i + 1, camDriver.Name, frameExpTime, frameNo);

                            // 1. Enqueue raw image for FITS write: image holds its own ChannelBuffer ref via AddRef in GetImageAsync
                            imageWriteQueue.Enqueue(new QueuedImageWrite(image, observation, expStartTimes[i], frameNo, frameExpTime, i));

                            // Drop camera's ref: the Image's ChannelBuffer ref keeps the float[,] alive until Release()

                            // 2. Pass raw image to GPU: shader does debayer + normalize + stretch.
                            //    Star detection runs on raw channel 0 (works for both mono and Bayer).
                            FrameMetrics metrics = default;
                            if (i < _lastCapturedImages.Length)
                            {
                                _lastCapturedImages[i] = image;

                                var stars = await image.FindStarsAsync(image.ReferenceStarChannel, snrMin: 10, maxStars: 1000, cancellationToken: cancellationToken);
                                var currentGain = await camDriver.GetGainAsync(cancellationToken);
                                metrics = FrameMetrics.FromStarList(stars, frameExpTime, currentGain, image.Width, image.Height);
                                _lastFrameMetrics[i] = metrics;
                                _frameMetricsHistory[i].Add(metrics);
                            }

                            // 3. Add to exposure log + frame history with metrics
                            var newTotal = Interlocked.Increment(ref _totalFramesWritten);
                            Interlocked.Add(ref _totalExposureTimeTicks, frameExpTime.Ticks);
                            // Sustained healthy frames decay the per-driver fault counters so a
                            // bad hour doesn't poison the rest of the session.
                            DecayFaultCountersOnFrameSuccess();
                            _logger.LogInformation("Frame #{FrameNo} fetched for camera #{CameraNum}, total frames: {Total}",
                                frameNo, i + 1, newTotal);
                            var logEntry = new ExposureLogEntry(
                                Timestamp: expStartTimes[i],
                                TargetName: observation.Target.Name,
                                FilterName: camDriver.Filter.DisplayName,
                                Exposure: frameExpTime,
                                FrameNumber: frameNo,
                                MedianHfd: metrics.MedianHfd,
                                StarCount: metrics.StarCount);
                            _exposureLog.Enqueue(logEntry);
                            FrameWritten?.Invoke(this, new FrameWrittenEventArgs(logEntry));
                            break;
                        }
                        else
                        {
                            var spinDuration = TimeSpan.FromMilliseconds(100);
                            polled += spinDuration;

                            await _timeProvider.SleepAsync(spinDuration, cancellationToken);
                        }
                    }
                    while (polled < (tickDuration / 5)
                        && await camDriver.GetCameraStateAsync(cancellationToken) is not CameraState.Error and not CameraState.NotConnected
                        && !cancellationToken.IsCancellationRequested
                    );

                    if (!imageFetchSuccess[i])
                    {
                        _logger.LogError("Failed fetching camera #{CameraNumber)} {CameraName} {ExposureStartTime} exposure of frame #{FrameNo}, camera state: {CameraState}",
                            i + 1, camDriver.Name, frameExpTime, frameNo, await camDriver.GetCameraStateAsync(cancellationToken));
                    }
                }
            }

            var fetchImagesSuccessAll = imageFetchSuccess.AllSet(scopes);

            // Check if scheduled observation duration has elapsed (tick-based to avoid clock drift)
            if (tickCount >= maxTicks)
            {
                _logger.LogInformation(
                    "Observation duration {Duration} for target {Target} has elapsed ({TickCount}/{MaxTicks} ticks), advancing.",
                    observation.Duration, observation.Target, tickCount, maxTicks);
                await WriteQueuedImagesToFitsFilesAsync();
                break; // falls through to return AdvanceToNextObservation
            }

            // Check if target has dropped below minimum altitude
            if (await mount.Driver.TryGetTransformAsync(ResolveSiteConditions(), cancellationToken) is { } altTransform
                && await mount.Driver.TryTransformJ2000ToMountNativeAsync(
                    altTransform, observation.Target.RA, observation.Target.Dec,
                    updateTime: true, cancellationToken) is { } altCoords
                && altCoords.Alt < Configuration.MinHeightAboveHorizon)
            {
                _logger.LogInformation(
                    "Target {Target} dropped below minimum altitude ({Alt:F1}° < {Min}°), advancing.",
                    observation.Target, altCoords.Alt, Configuration.MinHeightAboveHorizon);
                await WriteQueuedImagesToFitsFilesAsync();
                break; // falls through to return AdvanceToNextObservation
            }

            // Observe + decide flip action.
            // For AcrossMeridian targets: composite check (obstruction zone + HA window + pier change); 
            //   trust observed state so a firmware auto-flip / handbox / track-past-meridian all work.
            // For non-AcrossMeridian targets: keep the legacy HA-jump detection, if the pier side
            //   changes unexpectedly, abort the target.
            var flipAction = FlipAction.Continue;
            if (isGermanEquatorial && !await CatchAsync(mount.Driver.IsSlewingAsync, cancellationToken))
            {
                if (observation.AcrossMeridian)
                {
                    var currentHA = await CatchAsync(mount.Driver.GetHourAngleAsync, cancellationToken, double.NaN);
                    var currentPier = await GetSideOfPierAsync(cancellationToken);
                    var pierSideChanged = pierSideAtSlewTime != PointingState.Unknown
                        && currentPier != PointingState.Unknown
                        && currentPier != pierSideAtSlewTime;

                    // Is the mount already on the destination side for where it points? If so, no flip is
                    // needed even though HA is past the meridian: we slewed straight to a target that had
                    // already crossed (joined an in-progress AcrossMeridian observation), or we are already
                    // re-acquired on the new side. Without this, a mount whose reported pier side never
                    // changes (SkyWatcher: Dec-encoder Normal throughout a west track) flips forever.
                    var destinationPier = await CatchAsync(
                        ct => mount.Driver.DestinationSideOfPierAsync(observation.Target.RA, observation.Target.Dec, ct),
                        cancellationToken, PointingState.Unknown);
                    var alreadyOnCorrectSide = currentPier != PointingState.Unknown
                        && destinationPier != PointingState.Unknown
                        && currentPier == destinationPier;

                    if (!double.IsNaN(currentHA))
                    {
                        // Setup.MountLimits clamps the flip window: the rig's mechanical bound caps how
                        // late a flip may be scheduled, so the limit can never stop the mount at the
                        // moment it was about to flip.
                        flipAction = MeridianFlipDecision.DecideFlipAction(
                            currentHA, pierSideChanged, alreadyOnCorrectSide, hasFlipped, Configuration,
                            Setup.MountLimits);

                        // Published from the same HA reading the decision was taken on, so the countdown a
                        // dashboard shows and the flip the loop performs can never disagree. Null once we
                        // have flipped (or are already on the destination side): the countdown would
                        // otherwise point at a flip that is never coming.
                        MeridianFlipUtc = !hasFlipped && !alreadyOnCorrectSide
                            && MeridianFlipDecision.TimeUntilFlip(currentHA, Configuration) is { } untilFlip
                            ? _timeProvider.GetUtcNow() + untilFlip
                            : null;
                    }
                }
                else if (!await mount.Driver.IsOnSamePierSideAsync(hourAngleAtSlewTime, cancellationToken))
                {
                    // Out-of-band pier-side change for a target that wasn't supposed to cross; abort.
                    flipAction = FlipAction.CommandFlip;
                }
            }

            if (flipAction is FlipAction.WaitForObstructionClear)
            {
                _logger.LogInformation(
                    "Obstruction zone entered for {Target}: pausing exposure starts until HA clears the {ObsMin:F1}-min pre-meridian zone.",
                    observation.Target, Configuration.MeridianFlipObstructionZoneMinutesBefore);
                // Skip the exposure-start block by short-circuiting to the next tick.
                continue;
            }

            if (flipAction is FlipAction.CommandFlip or FlipAction.AlreadyFlipped)
            {
                // write all images before stopping
                await WriteQueuedImagesToFitsFilesAsync();

                // Let nearly-complete exposures finish; only abort if mostly remaining
                for (var i = 0; i < scopes; i++)
                {
                    var camDriver = Setup.Telescopes[i].Camera.Driver;
                    if (await camDriver.GetCameraStateAsync(cancellationToken) is CameraState.Exposing)
                    {
                        var elapsed = _timeProvider.GetUtcNow() - expStartTimes[i];
                        var total = TimeSpan.FromSeconds(currentSubExposuresSec[i]);
                        var remaining = total - elapsed;

                        if (remaining > TimeSpan.FromSeconds(30))
                        {
                            // >30s remaining, abort to flip promptly; ≤30s, wait and save the frame to avoid wasting integration time
                            _logger.LogInformation("Aborting exposure on camera #{CameraNumber} ({Remaining:F0}s remaining of {Total}s).",
                                i + 1, remaining.TotalSeconds, total.TotalSeconds);
                            if (camDriver.CanAbortExposure)
                            {
                                await camDriver.AbortExposureAsync(cancellationToken);
                            }
                            else if (camDriver.CanStopExposure)
                            {
                                await camDriver.StopExposureAsync(cancellationToken);
                            }
                        }
                        else
                        {
                            // Nearly done: wait for it to finish and save the frame
                            _logger.LogInformation("Waiting for exposure on camera #{CameraNumber} to finish ({Remaining:F0}s remaining).", i + 1, remaining.TotalSeconds);
                            await _timeProvider.SleepAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
                            if (await ResilientInvokeAsync(
                                    camDriver, camDriver.GetImageAsync,
                                    ResilientCallOptions.IdempotentRead, cancellationToken) is { Width: > 0, Height: > 0 } image)
                            {
                                imageWriteQueue.Enqueue(new QueuedImageWrite(image, observation, expStartTimes[i], frameNumbers[i], total, i));
                            }
                            await WriteQueuedImagesToFitsFilesAsync();
                        }
                    }
                }

                if (observation.AcrossMeridian)
                {
                    var flipResult = await PerformMeridianFlipAsync(
                        observation,
                        alreadyFlipped: flipAction is FlipAction.AlreadyFlipped,
                        cancellationToken);
                    if (flipResult.Success)
                    {
                        hourAngleAtSlewTime = flipResult.HourAngle;
                        // A flip happened (commanded or detected), never flip again for this target.
                        hasFlipped = true;
                        MeridianFlipCount++;
                        // Update the pier-side baseline so the next out-of-band-flip detection
                        // compares against where we are now, not where we were three flips ago.
                        if (flipResult.PierSide != PointingState.Unknown)
                        {
                            pierSideAtSlewTime = flipResult.PierSide;
                        }

                        // Reverse the altitude ladder: target is now descending
                        filterAscending = false;
                        _logger.LogInformation(
                            "Meridian flip complete: reversing filter ladder direction to descending for {Target}.",
                            observation.Target);

                        continue; // resume imaging loop on the new pier side
                    }

                    next = ImageLoopNextAction.RepeatCurrentObservation;
                    break;
                }
                else
                {
                    // finished this target
                    break;
                }
            }
            else if (fetchImagesSuccessAll)
            {
                // Check for focus drift using pre-computed frame results (no duplicate star detection)
                var currentBaselines = GetBaselineForCurrentObservation();
                {
                    for (var i = 0; i < scopes && i < _lastFrameMetrics.Length; i++)
                    {
                        var currentMetrics = _lastFrameMetrics[i];
                        if (currentMetrics.StarCount <= 3)
                        {
                            continue;
                        }

                        // If no baseline yet for this observation, collect samples from first frames
                        if (currentBaselines is null || !currentBaselines[i].IsValid)
                        {
                            AccumulateBaselineSample(i, currentMetrics);
                            continue;
                        }

                        // Only compare metrics captured with the same acquisition settings
                        if (!currentMetrics.IsComparableTo(currentBaselines[i]))
                        {
                            continue;
                        }

                        // Trend-based drift detection: least-squares fit over the recent comparable
                        // frames (FocusDriftDetector) instead of a single-frame comparison, so one
                        // bloated frame (wind, passing haze) does not trigger a spurious refocus.
                        var trendHfd = FocusDriftDetector.EstimateTrendHfd(
                            _frameMetricsHistory[i].Snapshot.AsSpan(), currentBaselines[i],
                            fallbackHfd: currentMetrics.MedianHfd, Configuration.FocusDriftMinSamples);

                        var ratio = trendHfd / currentBaselines[i].MedianHfd;

                        if (ratio > Configuration.FocusDriftThreshold)
                        {
                            _logger.LogWarning("Focus drift detected on telescope #{TelescopeNumber}: trend HFD={TrendHFD:F2} (current={CurrentHFD:F2}) vs baseline={BaselineHFD:F2} (ratio={Ratio:F2}), triggering auto-refocus.",
                                i + 1, trendHfd, currentMetrics.MedianHfd, currentBaselines[i].MedianHfd, ratio);

                            // The focuser is about to move: pre-refocus samples no longer describe
                            // the new focus position, and a stale high-HFD window fitted against the
                            // fresh baseline would re-trigger immediately (refocus oscillation).
                            _frameMetricsHistory[i].Clear();

                            // Write pending images before refocusing
                            await WriteQueuedImagesToFitsFilesAsync();

                            // Stop guiding, refocus, restart guiding
                            await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

                            var (converged, newBaseline) = await AutoFocusAsync(i, cancellationToken);
                            if (converged && newBaseline.IsValid)
                            {
                                var baselines = GetBaselineForCurrentObservation() ?? new FrameMetrics[scopes];
                                baselines[i] = newBaseline;
                                SetBaselineForCurrentObservation(baselines);
                            }

                            await ResilientInvokeAsync(
                                guider.Driver,
                                ct => guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, ct),
                                ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);
                            break; // restart imaging loop after refocus
                        }

                        // Check for condition deterioration (clouds, fog, dew):
                        // star count drop relative to baseline indicates sky transparency loss
                        var starCountRatio = (float)currentMetrics.StarCount / currentBaselines[i].StarCount;
                        if (starCountRatio < Configuration.ConditionDeteriorationThreshold)
                        {
                            _logger.LogWarning(
                                "Condition deterioration detected on telescope #{TelescopeNumber}: {CurrentStars} stars vs baseline {BaselineStars} (ratio={Ratio:F2}), pausing guiding.",
                                i + 1, currentMetrics.StarCount, currentBaselines[i].StarCount, starCountRatio);

                            await WriteQueuedImagesToFitsFilesAsync();
                            await guider.Driver.PauseAsync(cancellationToken).ConfigureAwait(false);

                            var recoveryTimeout = Configuration.ConditionRecoveryTimeout ?? TimeSpan.FromMinutes(10);
                            var recovered = await WaitForConditionRecoveryAsync(
                                i, currentBaselines[i], recoveryTimeout, cancellationToken);

                            if (recovered)
                            {
                                _logger.LogInformation("Conditions recovered on telescope #{TelescopeNumber}, resuming imaging.", i + 1);
                                await guider.Driver.UnpauseAsync(cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                _logger.LogWarning("Conditions did not recover within {Timeout} on telescope #{TelescopeNumber}, advancing to next observation.",
                                    recoveryTimeout, i + 1);
                                await guider.Driver.UnpauseAsync(cancellationToken).ConfigureAwait(false);
                                return ImageLoopNextAction.AdvanceToNextObservation;
                            }
                        }
                    }
                }

                if (ditherEveryNTicks > 0)
                {
                    var shouldDither = (tickCount % ditherEveryNTicks) == 0;
                    if (shouldDither)
                    {
                        _ditherPending = true;
                        if (await ResilientInvokeAsync(
                                guider.Driver,
                                ct => guider.Driver.DitherWaitAsync(Configuration.DitherPixel, Configuration.SettlePixel, Configuration.SettleTime, WriteQueuedImagesToFitsFilesAsync, ct),
                                ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false))
                        {
                            _logger.LogInformation("Dithering using \"{GuiderName}\" succeeded.", guider.Driver);
                        }
                        else
                        {
                            _logger.LogWarning("Dithering using \"{GuiderName}\" failed.", guider.Driver);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Skipping dithering ({DitheringRound}/{DitherEveryNthFrame} ticks)",
                            tickCount % ditherEveryNTicks, ditherEveryNTicks);
                    }
                }
            }
        } // end imaging loop

        // The while condition above leaves on the FIRST "not tracking" read -- before the poll's
        // driver-stop detector has had its second look (DetectDriverEnforcedStop debounces over two
        // polls, and the condition's read is not a poll at all). Left like that, a mount that stopped
        // itself is answered with AdvanceToNextObservation and the next observation's
        // EnsureTrackingAsync switches tracking straight back on against the driver's own limit -- the
        // fight P5 exists to prevent. So an undecided exit asks the detector again, at the tick cadence,
        // until it has had its full look; a stop that turns out to be ours, or a goto-completion race,
        // still leaves the way it always did.
        if (next is null && !cancellationToken.IsCancellationRequested && mount.Driver.Connected)
        {
            for (var look = 0; look < DriverStopDebouncePolls && !Volatile.Read(ref _limitReached); look++)
            {
                if (look > 0)
                {
                    await _timeProvider.SleepAsync(tickDuration, cancellationToken);
                }
                await PollDeviceStatesAsync(cancellationToken);
            }
            if (Volatile.Read(ref _limitReached))
            {
                _logger.LogError("Mount safety limit reached as the imaging loop ended ({Verdict}); ending the observation.", _limitVerdict.Describe());
                next = ImageLoopNextAction.LimitReached;
            }
        }

        if (imageWriteQueue.TryPeek(out _))
        {
            // write all images as the loop is ending here
            await WriteQueuedImagesToFitsFilesAsync();
        }

        _logger.LogInformation("ImagingLoop ended. Frames written: {Total}, total exposure: {Exposure}",
            TotalFramesWritten, TotalExposureTime);
        return next ?? ImageLoopNextAction.AdvanceToNextObservation;

        async ValueTask WriteQueuedImagesToFitsFilesAsync()
        {
            while (imageWriteQueue.TryDequeue(out var imageWrite))
            {
                try
                {
                    await WriteImageToFitsFileAsync(imageWrite);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception while saving frame #{FrameNumber} taken at {ExposureStartTime:o} by {Instrument}",
                        imageWrite.FrameNumber, imageWrite.ExpStartTime, imageWrite.Image.ImageMeta.Instrument);
                }
                finally
                {
                    // Release consumer's ref on the channel buffer.
                    // Camera's ref was already dropped by ReleaseImageData() after enqueue.
                    // When both refs are gone, onRelease fires → camera gets float[,] back.
                    imageWrite.Image.Release();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    GC.WaitForPendingFinalizers();
                    var gcInfo = GC.GetGCMemoryInfo();
                    _logger.LogInformation(
                        "Memory after FITS Release+GC: working={WorkingMB:F0}MB, managed={ManagedMB:F0}MB, " +
                        "gen0={Gen0}KB, gen1={Gen1}KB, gen2={Gen2}KB, LOH={LOH}KB, POH={POH}KB, " +
                        "committed={CommittedMB:F0}MB, promoted={PromotedMB:F0}MB",
                        Environment.WorkingSet / (1024.0 * 1024),
                        GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024),
                        gcInfo.GenerationInfo[0].SizeAfterBytes / 1024,
                        gcInfo.GenerationInfo[1].SizeAfterBytes / 1024,
                        gcInfo.GenerationInfo[2].SizeAfterBytes / 1024,
                        gcInfo.GenerationInfo[3].SizeAfterBytes / 1024, // LOH
                        gcInfo.GenerationInfo[4].SizeAfterBytes / 1024, // POH
                        gcInfo.TotalCommittedBytes / (1024.0 * 1024),
                        gcInfo.PromotedBytes / (1024.0 * 1024));
                }
            }
        }
    }

    /// <summary>
    /// Advances the filter cursor forward (ascending) or backward (descending) through
    /// the altitude ladder. Clamps at the ends, once the ladder is fully traversed,
    /// stays on the last filter (narrowband at low alt, or luminance at peak).
    /// </summary>
    private static int AdvanceFilterCursor(ref int cursor, int planLength, bool ascending)
    {
        if (ascending)
        {
            if (cursor < planLength - 1)
            {
                cursor++;
            }
        }
        else
        {
            if (cursor > 0)
            {
                cursor--;
            }
        }

        return cursor;
    }

    /// <summary>
    /// Switches the filter wheel to the target position if it's not already there.
    /// Waits for the wheel to finish moving, then applies the focuser offset delta
    /// relative to the reference filter if the OTA has a focuser and non-zero offsets.
    /// </summary>
    private async ValueTask SwitchFilterIfNeededAsync(
        int telescopeIndex,
        IFilterWheelDriver filterWheelDriver,
        int targetFilterPosition,
        CancellationToken cancellationToken)
    {
        var currentPosition = await ResilientInvokeAsync(
            filterWheelDriver, filterWheelDriver.GetPositionAsync,
            ResilientCallOptions.IdempotentRead, cancellationToken);
        if (currentPosition == targetFilterPosition)
        {
            return;
        }

        var telescope = Setup.Telescopes[telescopeIndex];
        var targetFilter = targetFilterPosition < filterWheelDriver.Filters.Count
            ? filterWheelDriver.Filters[targetFilterPosition]
            : new InstalledFilter(Filter.Unknown, 0);

        _logger.LogInformation("Telescope #{TelescopeNumber}: switching filter to {Filter} (position {Position}).",
            telescopeIndex + 1, targetFilter.Filter, targetFilterPosition);

        await ResilientInvokeAsync(
            filterWheelDriver,
            ct => filterWheelDriver.BeginMoveAsync(targetFilterPosition, ct),
            ResilientCallOptions.AbsoluteMove, cancellationToken);

        // Poll until the wheel reports it has arrived (position != -1 and equals target)
        while (!cancellationToken.IsCancellationRequested)
        {
            var pos = await ResilientInvokeAsync(
                filterWheelDriver, filterWheelDriver.GetPositionAsync,
                ResilientCallOptions.IdempotentRead, cancellationToken);
            if (pos == targetFilterPosition)
            {
                break;
            }

            await _timeProvider.SleepAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        // Apply focuser offset delta if the telescope has a focuser and the filter has an offset
        if (telescope.Focuser?.Driver is { Connected: true } focuserDriver && targetFilter.Position != 0)
        {
            // Find the reference filter (luminance or position 0) to compute the delta
            var refOffset = 0;
            for (var j = 0; j < filterWheelDriver.Filters.Count; j++)
            {
                if (filterWheelDriver.Filters[j].Filter.Bandpass == Bandpass.Luminance)
                {
                    refOffset = filterWheelDriver.Filters[j].Position;
                    break;
                }
            }

            var delta = targetFilter.Position - refOffset;
            if (delta != 0)
            {
                var currentFocusPos = await ResilientInvokeAsync(
                    focuserDriver, focuserDriver.GetPositionAsync,
                    ResilientCallOptions.IdempotentRead, cancellationToken);
                var targetFocusPos = currentFocusPos + delta;

                _logger.LogInformation("Telescope #{TelescopeNumber}: applying focus offset {Delta} steps for filter {Filter} (pos {From} -> {To}).",
                    telescopeIndex + 1, delta, targetFilter.Filter, currentFocusPos, targetFocusPos);

                var (filterBacklashIn, filterBacklashOut) = GetEffectiveBacklash(focuserDriver);
                await BacklashCompensation.MoveWithCompensationAsync(
                    focuserDriver, targetFocusPos, currentFocusPos,
                    filterBacklashIn, filterBacklashOut,
                    telescope.FocusDirection, _timeProvider, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Performs a meridian flip: stops guiding, re-slews to the target with a small westward
    /// RA offset to ensure the mount lands on the west side of the meridian, verifies the HA
    /// has flipped (retries if needed), then restarts guiding.
    /// After a GEM flip the DEC guide axis is reversed; the guider is responsible for detecting
    /// the flip and adjusting its calibration accordingly (e.g., PHD2's "reverse Dec after flip").
    /// When <paramref name="alreadyFlipped"/> is <c>true</c> the re-slew is skipped; the mount
    /// has already physically flipped (firmware auto-flip past limit, handbox press, or a prior
    /// <c>:MNe</c>/<c>:MNw</c>) and we just need to plate-solve recenter and restart the guider.
    /// </summary>
    /// <returns>A <see cref="MeridianFlipResult"/> indicating success, the post-flip hour angle, and observed pier side.</returns>
    internal async ValueTask<MeridianFlipResult> PerformMeridianFlipAsync(
        ScheduledObservation observation,
        bool alreadyFlipped,
        CancellationToken cancellationToken)
    {
        const int maxFlipAttempts = 3;
        const double raOffsetHours = 0.05; // ~3 min westward to ensure mount lands past meridian

        var mount = Setup.Mount;
        var guider = Setup.Guider;

        // The field as it lies BEFORE the flip: the last solve this OTA produced, which is the
        // centering that put us on this target (every observation is centred on arrival, and nothing
        // between then and now can roll the field -- an equatorial mount's field rotation is a
        // function of the pier side alone, not of where it points). Taken before anything moves,
        // because the recenter inside CompleteMeridianFlipAsync will append the solve it is compared
        // against.
        var preFlipSolution = LastFieldOrientationSolve(Setup.Telescopes[0].Name);

        // Whose word is final on whether the flip happened. A mount that MEASURES its pointing state
        // knows something the image cannot improve on, so there the frame is only a cross-check. A
        // mount that COMPUTES it from the hour angle is, at the meridian, asserting exactly the thing
        // under test -- it reports the flipped state whether or not the tube moved -- so there the
        // image wins. Never let this become an hour-angle test again: that IS the bug.
        var imageHasTheLastWord = mount.Driver.PointingStateSource is PointingStateSource.Computed;

        _logger.LogInformation("Meridian flip: stopping guider for {Target} (alreadyFlipped={AlreadyFlipped}).", observation.Target, alreadyFlipped);
        await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        if (alreadyFlipped)
        {
            // Mount already on the new pier side: skip the re-slew; just centre + restart guider.
            var detected = await CompleteMeridianFlipAsync(observation, preFlipSolution, cancellationToken);
            if (!detected.Success || !imageHasTheLastWord || detected.Verdict.Evidence is not FlipEvidence.NotFlipped)
            {
                return detected;
            }

            // The pier side changed and the field did not: on a computed-state mount that is not an
            // auto-flip at all, it is the reported state turning over as the POINTING crossed the
            // meridian. Command the flip after all rather than image on from the side we are still on.
            _logger.LogWarning(
                "Meridian flip: {Target} reported an auto-flip but the field did not rotate ({Delta:F1} deg); "
                + "the mount computes its pointing state, so commanding the flip instead.",
                observation.Target, detected.Verdict.RotationDeltaDeg);
            await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }

        // Wait for any ongoing slew to complete before attempting the flip
        while (await CatchAsync(mount.Driver.IsSlewingAsync, cancellationToken) && !cancellationToken.IsCancellationRequested)
        {
            await _timeProvider.SleepAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        for (var attempt = 1; attempt <= maxFlipAttempts; attempt++)
        {
            // Offset RA slightly westward (lower RA = more positive HA) to ensure
            // the mount doesn't land right on the meridian and flip back
            var offsetRA = observation.Target.RA - raOffsetHours * attempt;
            if (offsetRA < 0) offsetRA += 24;
            var slewTarget = observation.Target with { RA = offsetRA };

            _logger.LogInformation("Meridian flip: slewing to {Target} (attempt {Attempt}/{MaxAttempts}, RA offset {Offset:F3}h).",
                observation.Target, attempt, maxFlipAttempts, raOffsetHours * attempt);

            // Ensure no slew is in progress before starting the flip slew
            if (await CatchAsync(mount.Driver.IsSlewingAsync, cancellationToken))
            {
                await ResilientInvokeAsync(
                    mount.Driver,
                    ct => mount.Driver.WaitForSlewCompleteAsync(PollDeviceStatesAsync, ct),
                    ResilientCallOptions.IdempotentRead, cancellationToken).ConfigureAwait(false);
            }

            var (postCondition, _) = await ResilientInvokeAsync(
                mount.Driver,
                ct => mount.Driver.BeginSlewToTargetAsync(slewTarget, Configuration.MinHeightAboveHorizon, ct),
                ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);

            if (postCondition is not SlewPostCondition.Slewing)
            {
                _logger.LogError("Meridian flip: slew failed with {PostCondition} on attempt {Attempt}.", postCondition, attempt);
                continue;
            }

            if (!await ResilientInvokeAsync(
                    mount.Driver,
                    ct => mount.Driver.WaitForSlewCompleteAsync(PollDeviceStatesAsync, ct),
                    ResilientCallOptions.IdempotentRead, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogError("Meridian flip: slew did not complete on attempt {Attempt}.", attempt);
                continue;
            }

            var newHourAngle = await ResilientInvokeAsync(
                mount.Driver, mount.Driver.GetHourAngleAsync,
                ResilientCallOptions.IdempotentRead, cancellationToken);
            _logger.LogInformation("Meridian flip: slew complete, HA={NewHA:F4}h (attempt {Attempt}).", newHourAngle, attempt);

            // HA positive means the POINTING is west of the meridian. On a mount that measures its
            // pointing state that is the end of it; on one that computes it, it is true the moment
            // the target crosses and says nothing about the tube, so the recentre's own plate solve
            // is asked whether the field turned over.
            if (newHourAngle > 0)
            {
                var flipped = await CompleteMeridianFlipAsync(observation, preFlipSolution, cancellationToken);
                if (!flipped.Success || !imageHasTheLastWord || flipped.Verdict.Evidence is not FlipEvidence.NotFlipped)
                {
                    return flipped;
                }

                _logger.LogWarning(
                    "Meridian flip: HA={NewHA:F4}h says the flip took on attempt {Attempt}, but the field did not "
                    + "rotate ({Delta:F1} deg) -- the mount did not move the tube. Retrying with a larger offset.",
                    newHourAngle, attempt, flipped.Verdict.RotationDeltaDeg);
                await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                continue;
            }

            _logger.LogWarning("Meridian flip: HA={NewHA:F4}h still east of meridian after attempt {Attempt}, retrying with larger offset.",
                newHourAngle, attempt);
        }

        _logger.LogError("Meridian flip: failed after {MaxAttempts} attempts for {Target}.", maxFlipAttempts, observation.Target);
        return MeridianFlipResult.Failed;
    }

    /// <summary>
    /// Post-flip work shared between the commanded-flip and already-flipped paths:
    /// observe HA + pier side, plate-solve recenter, restart guiding.
    /// </summary>
    private async ValueTask<MeridianFlipResult> CompleteMeridianFlipAsync(
        ScheduledObservation observation,
        WCS? preFlipSolution,
        CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var guider = Setup.Guider;

        var newHourAngle = await ResilientInvokeAsync(
            mount.Driver, mount.Driver.GetHourAngleAsync,
            ResilientCallOptions.IdempotentRead, cancellationToken);
        var newPierSide = await GetSideOfPierAsync(cancellationToken);

        // Iterative plate-solve centering after flip
        _currentActivity = $"Centering on {observation.Target.Name} after flip\u2026";
        if (!await CenterOnTargetAsync(observation.Target, 0, thresholdArcmin: 1.0, maxAttempts: 5, cancellationToken))
        {
            _logger.LogWarning("Meridian flip: centering did not converge, proceeding with current pointing.");
        }

        // The recentre has just solved this OTA's field; comparing its orientation against the
        // pre-flip one is the only check here that does not go through the mount.
        var verdict = MeridianFlipVerification.FromSolves(
            preFlipSolution, LastFieldOrientationSolve(Setup.Telescopes[0].Name));
        switch (verdict.Evidence)
        {
            case FlipEvidence.Flipped:
                _logger.LogInformation("Meridian flip: the field rotated {Delta:F1} deg, so the tube went over.",
                    verdict.RotationDeltaDeg);
                break;
            case FlipEvidence.NotFlipped:
                _logger.LogWarning(
                    "Meridian flip: the field did NOT rotate ({Delta:F1} deg) -- whatever the mount reports, "
                    + "the tube is still on the side it started on.", verdict.RotationDeltaDeg);
                break;
            default:
                _logger.LogDebug(
                    "Meridian flip: no usable pair of solves to read the field rotation from (delta={Delta}); "
                    + "going on the mount's own report.", verdict.RotationDeltaDeg);
                break;
        }

        // Move the latch on evidence only. A confirmed flip puts the tube on the other side; a
        // confirmed non-flip leaves it exactly where it was, which is the whole point -- the mount is
        // meanwhile reporting the opposite. No evidence, no move.
        if (verdict.Evidence is FlipEvidence.Flipped && _verifiedPointingState is not PointingState.Unknown)
        {
            _verifiedPointingState = _verifiedPointingState.Flipped;
        }

        _logger.LogInformation("Meridian flip: restarting guiding for {Target}.", observation.Target);
        if (!await ResilientInvokeAsync(
                guider.Driver,
                ct => guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, ct),
                ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogError("Meridian flip: failed to restart guider after flip for {Target}.", observation.Target);
            return MeridianFlipResult.Failed;
        }

        _logger.LogInformation("Meridian flip: completed successfully for {Target}, HA={NewHA:F4}h, PierSide={PierSide}, Field={Evidence}.",
            observation.Target, newHourAngle, newPierSide, verdict.Evidence);
        return new MeridianFlipResult(true, newHourAngle, newPierSide, verdict);
    }

    /// <summary>
    /// The pier side the session has EVIDENCE for, as opposed to the one the mount reports. Latched by
    /// <c>PollDeviceStatesAsync</c> on the slewing-to-idle edge -- a goto is the only thing in ordinary
    /// operation that moves a tube across the pier, and the driver's report IS right the moment one
    /// lands, on any mount -- and thereafter moved only by a flip the image confirmed.
    /// <see cref="PointingState.Unknown"/> until the first slew completes.
    /// </summary>
    private PointingState _verifiedPointingState = PointingState.Unknown;

    /// <summary>
    /// True once a slew has been observed, until the next idle poll accepts the pier side it landed on.
    /// </summary>
    private bool _pierSideMayHaveMoved;

    /// <summary>
    /// **The canonical pier side. Prefer this over <see cref="IMountDriver.GetSideOfPierAsync"/>
    /// anywhere a decision depends on where the TUBE is.**
    /// <para>
    /// A mount that MEASURES its pointing state is believed verbatim -- it reads its own mechanics and
    /// the session cannot improve on that. A mount that COMPUTES it from the hour angle is believed
    /// only until the session knows better: that report is correct when a goto lands and then DRIFTS,
    /// turning over the moment the pointing crosses the meridian while the tube stays where the goto
    /// left it. Everything downstream of that drift is a bug -- the imaging loop reading an auto-flip
    /// that never happened, the guider reversing a calibration and inverting the sense that keeps it
    /// converging.
    /// </para>
    /// <para>
    /// So the answer here is the latched <see cref="_verifiedPointingState"/> once there is one, and
    /// the driver's report before that. It is handed to the built-in guider through
    /// <c>PointingStateOracle</c> rather than the guider reaching for the mount itself.
    /// </para>
    /// </summary>
    internal async ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken)
    {
        var reported = await CatchAsync(Setup.Mount.Driver.GetSideOfPierAsync, cancellationToken, PointingState.Unknown);
        return Setup.Mount.Driver.PointingStateSource is PointingStateSource.Computed
            && _verifiedPointingState is not PointingState.Unknown
                ? _verifiedPointingState
                : reported;
    }

    /// <summary>
    /// The most recent solve that can speak for how <paramref name="otaName"/>'s field is oriented:
    /// successful, carrying a CD matrix, and from that OTA's own camera.
    /// <para>
    /// The OTA filter is load-bearing on a multi-OTA rig, where the sensors sit at different rolls in
    /// their focusers -- a pair drawn from two of them differs by that constant and says nothing about
    /// the pier. It also excludes <see cref="PlateSolveContext.GuiderFocus"/> for the same reason:
    /// the guide camera is a different sensor at a different roll.
    /// </para>
    /// </summary>
    private WCS? LastFieldOrientationSolve(string otaName)
    {
        WCS? newest = null;
        // The queue is in solve order, so the last match wins; it is short (one session's solves) and
        // this runs once per flip.
        foreach (var record in _plateSolveHistory)
        {
            if (record is { Succeeded: true, Context: not PlateSolveContext.GuiderFocus, Solution: { HasCDMatrix: true } wcs }
                && string.Equals(record.OtaName, otaName, StringComparison.Ordinal))
            {
                newest = wcs;
            }
        }
        return newest;
    }

    /// <summary>
    /// Estimates how long until a target rises above <paramref name="minAlt"/> degrees,
    /// by sampling altitude at 5-minute intervals. Returns <c>null</c> if the target is
    /// setting (altitude decreasing) or won't rise within <paramref name="maxLookahead"/>.
    /// </summary>
    internal async ValueTask<TimeSpan?> EstimateTimeUntilTargetRisesAsync(
        Target target, byte minAlt, TimeSpan maxLookahead, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        if (await mount.Driver.TryGetTransformAsync(ResolveSiteConditions(), cancellationToken) is not { } transform)
        {
            return null;
        }

        var now = await GetMountUtcNowAsync(cancellationToken);
        var step = TimeSpan.FromMinutes(5);

        // Sample current altitude
        transform.DateTime = now;
        transform.SetJ2000(target.RA, target.Dec);
        transform.Refresh();
        var altNow = transform.ElevationTopocentric;

        // Check if already above threshold (shouldn't normally be called in this case)
        if (altNow >= minAlt)
        {
            return TimeSpan.Zero;
        }

        // Sample one step ahead to check if rising
        transform.DateTime = now.Add(step);
        transform.Refresh();
        var altNext = transform.ElevationTopocentric;

        if (altNext <= altNow)
        {
            // Target is setting, not rising
            return null;
        }

        // Target is rising: scan forward to find when it clears the threshold
        var elapsed = step;
        var prevAlt = altNext;
        while (elapsed < maxLookahead)
        {
            if (prevAlt >= minAlt)
            {
                return elapsed;
            }

            elapsed += step;
            transform.DateTime = now.Add(elapsed);
            transform.Refresh();
            prevAlt = transform.ElevationTopocentric;
        }

        // Won't rise within maxLookahead
        return null;
    }

    /// <summary>
    /// Waits for sky conditions to recover by periodically taking short exposures and checking star count.
    /// Returns true if star count recovers to at least <see cref="SessionConfiguration.ConditionDeteriorationThreshold"/>
    /// of the baseline within <paramref name="timeout"/>.
    /// </summary>
    private async ValueTask<bool> WaitForConditionRecoveryAsync(
        int telescopeIndex, FrameMetrics baseline, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pollInterval = TimeSpan.FromMinutes(1);
        var elapsed = TimeSpan.Zero;

        while (elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            await _timeProvider.SleepAsync(pollInterval, cancellationToken);
            elapsed += pollInterval;

            var camera = Setup.Telescopes[telescopeIndex].Camera.Driver;

            // Abort any in-progress exposure before taking a short test exposure
            if (await ResilientInvokeAsync(camera, camera.GetCameraStateAsync, ResilientCallOptions.IdempotentRead, cancellationToken) is CameraState.Exposing)
            {
                await ResilientInvokeAsync(camera, camera.AbortExposureAsync, ResilientCallOptions.NonIdempotentAction, cancellationToken);
                await _timeProvider.SleepAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }

            var testExposure = TimeSpan.FromSeconds(Math.Min(baseline.Exposure.TotalSeconds, 5));
            await ResilientInvokeAsync(
                camera,
                ct => camera.StartExposureAsync(testExposure, cancellationToken: ct),
                ResilientCallOptions.NonIdempotentAction, cancellationToken);
            await _timeProvider.SleepAsync(testExposure + TimeSpan.FromSeconds(2), cancellationToken);

            if (!await ResilientInvokeAsync(camera, camera.GetImageReadyAsync, ResilientCallOptions.IdempotentRead, cancellationToken))
            {
                continue;
            }

            var image = await ResilientInvokeAsync(
                camera, ((ICameraDriver)camera).GetImageAsync,
                ResilientCallOptions.IdempotentRead, cancellationToken);
            if (image is null)
            {
                continue;
            }

            var stars = await image.FindStarsAsync(image.ReferenceStarChannel, snrMin: 10, maxStars: 100, cancellationToken: cancellationToken);
            var imgW = image.Width;
            var imgH = image.Height;
            image.Release();
            var currentGain = await ResilientInvokeAsync(camera, camera.GetGainAsync, ResilientCallOptions.IdempotentRead, cancellationToken);
            var metrics = FrameMetrics.FromStarList(stars, testExposure, currentGain, imgW, imgH);

            if (!metrics.IsValid)
            {
                _logger.LogInformation("Condition check: {Stars} stars detected (waiting for recovery, {Elapsed}/{Timeout}).",
                    stars.Count, elapsed, timeout);
                continue;
            }

            var starCountRatio = (float)metrics.StarCount / baseline.StarCount;
            _logger.LogInformation("Condition check: {Stars} stars (ratio={Ratio:F2} vs baseline {Baseline}, {Elapsed}/{Timeout}).",
                metrics.StarCount, starCountRatio, baseline.StarCount, elapsed, timeout);

            if (starCountRatio >= Configuration.ConditionDeteriorationThreshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Computes spectrophotometric white balance for an image using its metadata
    /// to build per-channel system throughput curves (sensor QE + CFA + filters),
    /// then logs the resulting channel multipliers. Requires a plate-solved WCS
    /// for star-to-catalog matching.
    /// </summary>
    /// <remarks>
    /// Results are cached per (sensor, filter, sensorType) across frames; the
    /// system throughput and WCS don't change when imaging the same target with
    /// the same camera and filter.
    /// </remarks>
    internal async ValueTask LogSpectrophotometricCalibrationAsync(
        Image image, StarList stars, WCS wcs, CancellationToken ct)
    {
        try
        {
            if (!FilterCurveDatabase.IsLoaded)
                await FilterCurveDatabase.LoadAsync(ct);

            var db = await External.GetCelestialObjectDBAsync(ct);

            // T_sys keyed on (sensorModel | filterRawName | sensorType)
            var meta = image.ImageMeta;
            var tsysKey = $"{meta.SensorModel}|{meta.Filter.FilterNameForFits}|{(int)meta.SensorType}";

            if (_cachedTsys is not { } cached || _cachedTsysKey != tsysKey)
            {
                var channels = FilterCurveDatabase.BuildChannelThroughputs(meta);
                if (channels is null)
                {
                    _logger.LogDebug("SPCC: could not build channel throughputs for {Instrument} / {Sensor} / {Filter}",
                        meta.Instrument, meta.SensorModel, meta.Filter.FilterNameForFits);
                    return;
                }
                _cachedTsys = channels;
                _cachedTsysKey = tsysKey;
            }

            var (tsysR, tsysG, tsysB) = _cachedTsys.Value;

            var result = Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance(
                image, stars, wcs, db, tsysR, tsysG, tsysB,
                minStars: 3);

            if (result.HasValue)
            {
                var r = result.Value;
                _logger.LogInformation(
                    "SPCC: {Final}/{Initial} stars in {Iters} iter(s), WB=(R:{R:F3} G:{G:F3} B:{B:F3}) for {Instrument} sensor={Sensor} filter={Filter}",
                    r.FinalMatches, r.InitialMatches, r.Iterations, r.R, r.G, r.B,
                    meta.Instrument,
                    meta.SensorModel is { Length: > 0 } s ? s : "?",
                    meta.Filter.FilterNameForFits);
            }
            else
            {
                _logger.LogDebug("SPCC: insufficient matches for {Instrument}", meta.Instrument);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SPCC: diagnostic failed for {Instrument}", image.ImageMeta.Instrument);
        }
    }
}