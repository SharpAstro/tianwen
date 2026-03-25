using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
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
            if (!await mount.Driver.EnsureTrackingAsync(cancellationToken: cancellationToken))
            {
                External.AppLogger.LogError("Failed to enable tracking of {Mount}.", mount);
                return;
            }

            _currentActivity = $"Slewing to {observation.Target.Name}\u2026";
            External.AppLogger.LogInformation("Stop guiding to start slewing mount to target {Observation}.", observation);
            await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            double hourAngleAtSlewTime;
            try
            {
                (var postCondition, hourAngleAtSlewTime) = await mount.Driver.BeginSlewToTargetAsync(observation.Target, Configuration.MinHeightAboveHorizon, cancellationToken).ConfigureAwait(false);
                if (postCondition is SlewPostCondition.SlewNotPossible or SlewPostCondition.TargetBelowHorizonLimit)
                {
                    var maxWait = Configuration.MaxWaitForRisingTarget ?? TimeSpan.FromMinutes(15);

                    // If target is rising and will clear the horizon soon, wait for it
                    if (postCondition is SlewPostCondition.TargetBelowHorizonLimit
                        && await EstimateTimeUntilTargetRisesAsync(observation.Target, Configuration.MinHeightAboveHorizon, maxWait, cancellationToken) is { } waitTime
                        && waitTime > TimeSpan.Zero)
                    {
                        External.AppLogger.LogInformation(
                            "Target {Target} is rising, waiting {WaitMinutes:F0} min until it clears {MinAlt}°.",
                            observation.Target, waitTime.TotalMinutes, Configuration.MinHeightAboveHorizon);
                        await External.SleepAsync(waitTime, cancellationToken);

                        // Retry slew after waiting
                        (postCondition, hourAngleAtSlewTime) = await mount.Driver.BeginSlewToTargetAsync(
                            observation.Target, Configuration.MinHeightAboveHorizon, cancellationToken).ConfigureAwait(false);
                    }

                    // Still not available — try spare targets, then advance
                    if (postCondition is SlewPostCondition.SlewNotPossible or SlewPostCondition.TargetBelowHorizonLimit)
                    {
                        if (Observations.TryGetNextSpare(_activeObservation, ref _spareIndex) is { } spare)
                        {
                            External.AppLogger.LogInformation("Primary target {Target} not available ({PostCondition}), trying spare target {SpareTarget}.",
                                observation.Target, postCondition, spare.Target);
                            observation = spare;

                            (postCondition, hourAngleAtSlewTime) = await mount.Driver.BeginSlewToTargetAsync(spare.Target, Configuration.MinHeightAboveHorizon, cancellationToken).ConfigureAwait(false);
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
                    if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
                    {
                        External.AppLogger.LogError("Failed to complete slewing of mount {Mount}", mount);

                        throw new InvalidOperationException($"Failed to complete slewing of mount {mount} while slewing to {observation.Target}");
                    }

                    // Recompute hour angle now that the mount is pointing at the target
                    // (BeginSlewToTargetAsync returns the pre-slew HA, which may be on a different pier side)
                    hourAngleAtSlewTime = await mount.Driver.GetHourAngleAsync(cancellationToken);

                    // Iterative plate-solve + sync + reslew centering
                    if (!await CenterOnTargetAsync(observation.Target, 0, thresholdArcmin: 1.0, maxAttempts: 3, cancellationToken))
                    {
                        External.AppLogger.LogWarning("Centering on {Target} did not converge, continuing with current pointing.", observation.Target);
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unknown post condition {postCondition} after slewing to target {observation.Target}");
                }
            }
            catch (Exception ex)
            {
                External.AppLogger.LogError(ex, "Error while slewing to {Observation}, retrying", observation);
                continue;
            }

            _currentActivity = $"Starting guider on {observation.Target.Name}\u2026";
            var guidingSuccess = await guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                External.AppLogger.LogWarning("Cancellation requested, abort setting up guider \"{GuiderName}\" and quit observation loop.", guider.Driver);
                break;
            }
            else if (!guidingSuccess)
            {
                External.AppLogger.LogError("Skipping target {Observation} as starting guider \"{GuiderName}\" failed after trying {GuiderTries} times.", observation, guider.Driver, Configuration.GuidingTries);
                _ = AdvanceObservation();
                continue;
            }

            // Optionally refocus when switching to a new target
            if (Configuration.AlwaysRefocusOnNewTarget && !_baselineByObservation.ContainsKey(ActiveObservationIndex))
            {
                External.AppLogger.LogInformation("Refocusing for new target {Target}.", observation.Target);
                await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

                if (!await AutoFocusAllTelescopesAsync(cancellationToken))
                {
                    External.AppLogger.LogWarning("Auto-focus did not converge for all telescopes on new target, proceeding.");
                }

                await guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, cancellationToken).ConfigureAwait(false);
            }

            var imageLoopStart = await GetMountUtcNowAsync(cancellationToken);
            var imageLoopResult = await ImagingLoopAsync(observation, hourAngleAtSlewTime, cancellationToken).ConfigureAwait(false);
            if (imageLoopResult is ImageLoopNextAction.AdvanceToNextObservation)
            {
                _ = AdvanceObservation();
                continue;
            }
            else if (imageLoopResult is ImageLoopNextAction.RepeatCurrentObservation)
            {
                // todo maybe wait a bit for better weather/tree out of the way, etc.
                continue;
            }
            else
            {
                External.AppLogger.LogError("Imaging loop for {Observation} did not complete successfully, total runtime: {TotalRuntime:c}", observation, await GetMountUtcNowAsync(cancellationToken) - imageLoopStart);
                break;
            }
        } // end observation loop
    }

    /// <summary>
    /// Imaging loop for one observation, handles exposing frames + dithering, handles meridian flip.
    /// </summary>
    /// <param name="observation">Observation to image.</param>
    /// <param name="hourAngleAtSlewTime">provide hour angle current as of start of session, used to calculate meridian flip.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>loop result</returns>
    /// <exception cref="InvalidOperationException"></exception>
    internal async ValueTask<ImageLoopNextAction> ImagingLoopAsync(ScheduledObservation observation, double hourAngleAtSlewTime, CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;
        var scopes = Setup.Telescopes.Length;

        // Ensure camera states array is initialized (tests may call ImagingLoopAsync directly)
        if (_cameraStates.Length != scopes)
        {
            _cameraStates = new CameraExposureState[scopes];
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
        var currentSubExposuresSec = new int[scopes];

        for (var i = 0; i < scopes; i++)
        {
            var camera = Setup.Telescopes[i].Camera;
            camera.Driver.Target = observation.Target;

            // Each telescope gets its own copy of the filter plan.
            // Single-position filter wheels (manual holders) get a single-entry plan
            // using the observation's first sub-exposure — they can't switch filters.
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

        _currentActivity = null; // clear — PhaseStatusText takes over for imaging
        External.AppLogger.LogInformation(
            "ImagingLoop starting for {Target}: {FilterCount} filters, direction={Direction}, tick={TickSec}s, duration={Duration}, GCD={GCD}s.",
            observation.Target, observation.FilterPlan.Length,
            filterAscending ? "ascending" : "descending",
            tickSec, observation.Duration, gcdSec);

        using var ticker = new PeriodicTimer(tickDuration, External.TimeProvider);

        while (!cancellationToken.IsCancellationRequested
            && mount.Driver.Connected
            && await CatchAsync(mount.Driver.IsTrackingAsync, cancellationToken)
        )
        {
            tickCount++;

            // Poll all device states (focuser temp/pos/moving, mount RA/Dec/HA/pier)
            await PollDeviceStatesAsync(cancellationToken);

            var isGuiding = await CatchAsync(guider.Driver.IsGuidingAsync, cancellationToken).ConfigureAwait(false);

            // Poll guide stats each tick for the guide graph
            if (isGuiding)
            {
                GuideStats? guideStats = null;
                try { guideStats = await guider.Driver.GetStatsAsync(cancellationToken); } catch { /* ignore */ }
                if (guideStats is { } gs)
                {
                    UpdateGuideStats(gs);
                    // Synthetic sample from RMS stats (random within ±RMS)
                    var sampleRng = new Random(tickCount);
                    AppendGuideErrorSample(new GuideErrorSample(
                        External.TimeProvider.GetUtcNow(),
                        gs.RaRMS * (sampleRng.NextDouble() * 2 - 1),
                        gs.DecRMS * (sampleRng.NextDouble() * 2 - 1)));
                }
            }

            if (!isGuiding)
            {
                var guiderRestartedSuccess =
                    await CatchAsync(guider.Driver.ConnectAsync, cancellationToken) &&
                    await guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    External.AppLogger.LogWarning("Cancellation requested, abort setting up guider \"{GuiderName}\" and quit imaging loop for observation {Observation}.", guider.Driver, observation);
                    next = ImageLoopNextAction.BreakObservationLoop;
                    break;
                }
                else if (!guiderRestartedSuccess)
                {
                    External.AppLogger.LogError("Reschedule target {Observation} as starting guider \"{GuiderName}\" failed after trying {GuiderTries} times.", observation, guider.Driver, Configuration.GuidingTries);
                    next = ImageLoopNextAction.RepeatCurrentObservation;
                    break;
                }
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

                        External.AppLogger.LogInformation(
                            "Telescope #{TelescopeNumber}: filter ladder step {PrevCursor} → {Cursor} ({Direction}), next filter position {FilterPosition}.",
                            i + 1, prevCursor, cursor, filterAscending ? "ascending" : "descending", currentEntry.FilterPosition);
                    }

                    // Switch filter if needed
                    if (currentEntry.FilterPosition >= 0 && telescope.FilterWheel?.Driver is { Connected: true } filterWheelDriver)
                    {
                        await SwitchFilterIfNeededAsync(i, filterWheelDriver, currentEntry.FilterPosition, cancellationToken);
                    }

                    // set denormalized parameters so that the image driver can write proper headers in the image file
                    camerDriver.FocusPosition = await CatchAsync(async ct => telescope.Focuser?.Driver is { Connected: true } focuserDriver ? await focuserDriver.GetPositionAsync(ct) : -1, cancellationToken, -1);
                    camerDriver.Filter = await CatchAsync(async ct => telescope.FilterWheel?.Driver is { Connected: true } fwDriver ? (await fwDriver.GetCurrentFilterAsync(ct)).Filter : Filter.Unknown, cancellationToken, Filter.Unknown);

                    var subExposureSec = (int)Math.Ceiling(currentEntry.SubExposure.TotalSeconds);
                    currentSubExposuresSec[i] = subExposureSec;
                    var frameExpTime = TimeSpan.FromSeconds(subExposureSec);
                    expStartTimes[i] = await camerDriver.StartExposureAsync(frameExpTime, cancellationToken: cancellationToken);
                    expTicks[i] = subExposureSec / tickSec;
                    filterFrameCounters[i]++;
                    var frameNo = ++frameNumbers[i];

                    var focuserTemp = await CatchAsync(async ct => telescope.Focuser?.Driver is { Connected: true } f ? await f.GetTemperatureAsync(ct) : double.NaN, cancellationToken, double.NaN);
                    var focuserMoving = await CatchAsync(async ct => telescope.Focuser?.Driver is { Connected: true } f && await f.GetIsMovingAsync(ct), cancellationToken, false);
                    _cameraStates[i] = new CameraExposureState(i, expStartTimes[i], frameExpTime, frameNo,
                        camerDriver.Filter.DisplayName, camerDriver.FocusPosition, Devices.CameraState.Exposing,
                        focuserTemp, focuserMoving);

                    External.AppLogger.LogInformation("Camera #{CameraNumber} {CamerName} starting {ExposureStartTime} exposure of frame #{FrameNo} (filter: {Filter}).",
                        i + 1, camerDriver.Name, frameExpTime, frameNo, camerDriver.Filter);
                }
            }

            await WriteQueuedImagesToFitsFilesAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                External.AppLogger.LogWarning("Cancellation requested, all images in queue written to disk, abort image acquisition and quit imaging loop");
                next = ImageLoopNextAction.BreakObservationLoop;
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
                    do // wait for image loop
                    {
                        if (await camDriver.GetImageAsync(cancellationToken) is { Width: > 0, Height: > 0 } image)
                        {
                            imageFetchSuccess[i] = true;
                            _cameraStates[i] = _cameraStates[i] with { State = Devices.CameraState.Download };
                            // Prepare viewer copy: RGGB → debayer+normalize (1 alloc), mono → normalize (1 alloc)
                            // Raw image goes to FITS queue untouched
                            if (i < _lastCapturedImages.Length)
                            {
                                _lastCapturedImages[i] = image.ImageMeta.SensorType is Imaging.SensorType.RGGB
                                    ? await image.DebayerAsync(Imaging.DebayerAlgorithm.AHD, normalizeToUnit: true, cancellationToken)
                                    : image.ScaleFloatValuesToUnit();
                            }
                            External.AppLogger.LogInformation("Camera #{CameraNumber} {CameraName} finished {ExposureStartTime} exposure of frame #{FrameNo}",
                                i + 1, camDriver.Name, frameExpTime, frameNo);

                            // Enqueue raw image for FITS write (not normalized, not debayered)
                            imageWriteQueue.Enqueue(new QueuedImageWrite(image, observation, expStartTimes[i], frameNo, frameExpTime, i));
                            break;
                        }
                        else
                        {
                            var spinDuration = TimeSpan.FromMilliseconds(100);
                            polled += spinDuration;

                            await External.SleepAsync(spinDuration, cancellationToken);
                        }
                    }
                    while (polled < (tickDuration / 5)
                        && await camDriver.GetCameraStateAsync(cancellationToken) is not CameraState.Error and not CameraState.NotConnected
                        && !cancellationToken.IsCancellationRequested
                    );

                    if (!imageFetchSuccess[i])
                    {
                        External.AppLogger.LogError("Failed fetching camera #{CameraNumber)} {CameraName} {ExposureStartTime} exposure of frame #{FrameNo}, camera state: {CameraState}",
                            i + 1, camDriver.Name, frameExpTime, frameNo, await camDriver.GetCameraStateAsync(cancellationToken));
                    }
                }
            }

            var fetchImagesSuccessAll = imageFetchSuccess.AllSet(scopes);

            // Check if scheduled observation duration has elapsed (tick-based to avoid clock drift)
            if (tickCount >= maxTicks)
            {
                External.AppLogger.LogInformation(
                    "Observation duration {Duration} for target {Target} has elapsed ({TickCount}/{MaxTicks} ticks), advancing.",
                    observation.Duration, observation.Target, tickCount, maxTicks);
                await WriteQueuedImagesToFitsFilesAsync();
                break; // falls through to return AdvanceToNextObservation
            }

            // Check if target has dropped below minimum altitude
            if (await mount.Driver.TryGetTransformAsync(cancellationToken) is { } altTransform
                && await mount.Driver.TryTransformJ2000ToMountNativeAsync(
                    altTransform, observation.Target.RA, observation.Target.Dec,
                    updateTime: true, cancellationToken) is { } altCoords
                && altCoords.Alt < Configuration.MinHeightAboveHorizon)
            {
                External.AppLogger.LogInformation(
                    "Target {Target} dropped below minimum altitude ({Alt:F1}° < {Min}°), advancing.",
                    observation.Target, altCoords.Alt, Configuration.MinHeightAboveHorizon);
                await WriteQueuedImagesToFitsFilesAsync();
                break; // falls through to return AdvanceToNextObservation
            }

            if (!await CatchAsync(mount.Driver.IsSlewingAsync, cancellationToken)
                && !await mount.Driver.IsOnSamePierSideAsync(hourAngleAtSlewTime, cancellationToken))
            {
                // write all images before stopping
                await WriteQueuedImagesToFitsFilesAsync();

                // Let nearly-complete exposures finish; only abort if mostly remaining
                for (var i = 0; i < scopes; i++)
                {
                    var camDriver = Setup.Telescopes[i].Camera.Driver;
                    if (await camDriver.GetCameraStateAsync(cancellationToken) is CameraState.Exposing)
                    {
                        var elapsed = External.TimeProvider.GetUtcNow() - expStartTimes[i];
                        var total = TimeSpan.FromSeconds(currentSubExposuresSec[i]);
                        var remaining = total - elapsed;

                        if (remaining > TimeSpan.FromSeconds(30))
                        {
                            // Mostly remaining — abort to avoid long wait
                            External.AppLogger.LogInformation("Aborting exposure on camera #{CameraNumber} ({Remaining:F0}s remaining of {Total}s).",
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
                            // Nearly done — wait for it to finish and save the frame
                            External.AppLogger.LogInformation("Waiting for exposure on camera #{CameraNumber} to finish ({Remaining:F0}s remaining).", i + 1, remaining.TotalSeconds);
                            await External.SleepAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
                            if (await camDriver.GetImageAsync(cancellationToken) is { Width: > 0, Height: > 0 } image)
                            {
                                imageWriteQueue.Enqueue(new QueuedImageWrite(image, observation, expStartTimes[i], frameNumbers[i], total, i));
                            }
                            await WriteQueuedImagesToFitsFilesAsync();
                        }
                    }
                }

                if (observation.AcrossMeridian)
                {
                    // TODO: detect auto-flipping mounts (e.g. iOptron CEM) where the mount
                    // already flipped physically — skip re-slew, just plate solve and restart guiding
                    var flipResult = await PerformMeridianFlipAsync(observation, cancellationToken);
                    if (flipResult.Success)
                    {
                        hourAngleAtSlewTime = flipResult.HourAngle;

                        // Reverse the altitude ladder: target is now descending
                        filterAscending = false;
                        External.AppLogger.LogInformation(
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
                // Check for focus drift on the last fetched image from each telescope
                var currentBaselines = GetBaselineForCurrentObservation();
                if (imageWriteQueue.Count > 0)
                {
                    for (var i = 0; i < scopes; i++)
                    {
                        var lastImage = imageWriteQueue.Last().Image;
                        var driftStars = await lastImage.FindStarsAsync(0, snrMin: 10, maxStars: 100, cancellationToken: cancellationToken);
                        if (driftStars.Count <= 3)
                        {
                            continue;
                        }

                        var camera = Setup.Telescopes[i].Camera.Driver;
                        var currentGain = await camera.GetGainAsync(cancellationToken);
                        var currentMetrics = FrameMetrics.FromStarList(driftStars, TimeSpan.FromSeconds(currentSubExposuresSec[i]), currentGain);

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

                        var ratio = currentMetrics.MedianHfd / currentBaselines[i].MedianHfd;

                        if (ratio > Configuration.FocusDriftThreshold)
                        {
                            External.AppLogger.LogWarning("Focus drift detected on telescope #{TelescopeNumber}: HFD={CurrentHFD:F2} vs baseline={BaselineHFD:F2} (ratio={Ratio:F2}), triggering auto-refocus.",
                                i + 1, currentMetrics.MedianHfd, currentBaselines[i].MedianHfd, ratio);

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

                            await guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, cancellationToken).ConfigureAwait(false);
                            break; // restart imaging loop after refocus
                        }

                        // Check for condition deterioration (clouds, fog, dew):
                        // star count drop relative to baseline indicates sky transparency loss
                        var starCountRatio = (float)currentMetrics.StarCount / currentBaselines[i].StarCount;
                        if (starCountRatio < Configuration.ConditionDeteriorationThreshold)
                        {
                            External.AppLogger.LogWarning(
                                "Condition deterioration detected on telescope #{TelescopeNumber}: {CurrentStars} stars vs baseline {BaselineStars} (ratio={Ratio:F2}), pausing guiding.",
                                i + 1, currentMetrics.StarCount, currentBaselines[i].StarCount, starCountRatio);

                            await WriteQueuedImagesToFitsFilesAsync();
                            await guider.Driver.PauseAsync(cancellationToken).ConfigureAwait(false);

                            var recoveryTimeout = Configuration.ConditionRecoveryTimeout ?? TimeSpan.FromMinutes(10);
                            var recovered = await WaitForConditionRecoveryAsync(
                                i, currentBaselines[i], recoveryTimeout, cancellationToken);

                            if (recovered)
                            {
                                External.AppLogger.LogInformation("Conditions recovered on telescope #{TelescopeNumber}, resuming imaging.", i + 1);
                                await guider.Driver.UnpauseAsync(cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                External.AppLogger.LogWarning("Conditions did not recover within {Timeout} on telescope #{TelescopeNumber}, advancing to next observation.",
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
                        if (await guider.Driver.DitherWaitAsync(Configuration.DitherPixel, Configuration.SettlePixel, Configuration.SettleTime, WriteQueuedImagesToFitsFilesAsync, cancellationToken).ConfigureAwait(false))
                        {
                            External.AppLogger.LogInformation("Dithering using \"{GuiderName}\" succeeded.", guider.Driver);
                        }
                        else
                        {
                            External.AppLogger.LogWarning("Dithering using \"{GuiderName}\" failed.", guider.Driver);
                        }
                    }
                    else
                    {
                        External.AppLogger.LogDebug("Skipping dithering ({DitheringRound}/{DitherEveryNthFrame} ticks)",
                            tickCount % ditherEveryNTicks, ditherEveryNTicks);
                    }
                }
            }
        } // end imaging loop

        if (imageWriteQueue.TryPeek(out _))
        {
            // write all images as the loop is ending here
            await WriteQueuedImagesToFitsFilesAsync();
        }

        return next ?? ImageLoopNextAction.AdvanceToNextObservation;

        async ValueTask WriteQueuedImagesToFitsFilesAsync()
        {
            while (imageWriteQueue.TryDequeue(out var imageWrite))
            {
                try
                {
                    var filePath = await WriteImageToFitsFileAsync(imageWrite);
                    Interlocked.Increment(ref _totalFramesWritten);
                    Interlocked.Add(ref _totalExposureTimeTicks, imageWrite.ActualSubExposure.Ticks);

                    // Append to exposure log and fire event
                    var entry = new ExposureLogEntry(
                        Timestamp: imageWrite.ExpStartTime,
                        TargetName: imageWrite.Observation.Target.Name,
                        FilterName: imageWrite.Image.ImageMeta.Filter.Name,
                        Exposure: imageWrite.ActualSubExposure,
                        FrameNumber: imageWrite.FrameNumber,
                        MedianHfd: 0f, // HFD computed during drift detection, not here
                        FilePath: filePath);
                    _exposureLog.Enqueue(entry);
                    FrameWritten?.Invoke(this, new FrameWrittenEventArgs(entry));
                }
                catch (Exception ex)
                {
                    External.AppLogger.LogError(ex, "Exception while saving frame #{FrameNumber} taken at {ExposureStartTime:o} by {Instrument}",
                        imageWrite.FrameNumber, imageWrite.ExpStartTime, imageWrite.Image.ImageMeta.Instrument);
                }
            }
        }
    }

    /// <summary>
    /// Advances the filter cursor forward (ascending) or backward (descending) through
    /// the altitude ladder. Clamps at the ends — once the ladder is fully traversed,
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
        var currentPosition = await filterWheelDriver.GetPositionAsync(cancellationToken);
        if (currentPosition == targetFilterPosition)
        {
            return;
        }

        var telescope = Setup.Telescopes[telescopeIndex];
        var targetFilter = targetFilterPosition < filterWheelDriver.Filters.Count
            ? filterWheelDriver.Filters[targetFilterPosition]
            : new InstalledFilter(Filter.Unknown, 0);

        External.AppLogger.LogInformation("Telescope #{TelescopeNumber}: switching filter to {Filter} (position {Position}).",
            telescopeIndex + 1, targetFilter.Filter, targetFilterPosition);

        await filterWheelDriver.BeginMoveAsync(targetFilterPosition, cancellationToken);

        // Poll until the wheel reports it has arrived (position != -1 and equals target)
        while (!cancellationToken.IsCancellationRequested)
        {
            var pos = await filterWheelDriver.GetPositionAsync(cancellationToken);
            if (pos == targetFilterPosition)
            {
                break;
            }

            await External.SleepAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
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
                var currentFocusPos = await focuserDriver.GetPositionAsync(cancellationToken);
                var targetFocusPos = currentFocusPos + delta;

                External.AppLogger.LogInformation("Telescope #{TelescopeNumber}: applying focus offset {Delta} steps for filter {Filter} (pos {From} -> {To}).",
                    telescopeIndex + 1, delta, targetFilter.Filter, currentFocusPos, targetFocusPos);

                await BacklashCompensation.MoveWithCompensationAsync(
                    focuserDriver, targetFocusPos, currentFocusPos,
                    focuserDriver.BacklashStepsIn, focuserDriver.BacklashStepsOut,
                    telescope.FocusDirection, External, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Performs a meridian flip: stops guiding, re-slews to the target with a small westward
    /// RA offset to ensure the mount lands on the west side of the meridian, verifies the HA
    /// has flipped (retries if needed), then restarts guiding.
    /// After a GEM flip the DEC guide axis is reversed; the guider is responsible for detecting
    /// the flip and adjusting its calibration accordingly (e.g., PHD2's "reverse Dec after flip").
    /// </summary>
    /// <returns>A <see cref="MeridianFlipResult"/> indicating success and the post-flip hour angle.</returns>
    private async ValueTask<MeridianFlipResult> PerformMeridianFlipAsync(
        ScheduledObservation observation,
        CancellationToken cancellationToken)
    {
        const int maxFlipAttempts = 3;
        const double raOffsetHours = 0.05; // ~3 min westward to ensure mount lands past meridian

        var mount = Setup.Mount;
        var guider = Setup.Guider;

        External.AppLogger.LogInformation("Meridian flip: stopping guider for {Target}.", observation.Target);
        await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        // Wait for any ongoing slew to complete before attempting the flip
        while (await CatchAsync(mount.Driver.IsSlewingAsync, cancellationToken) && !cancellationToken.IsCancellationRequested)
        {
            await External.SleepAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        for (var attempt = 1; attempt <= maxFlipAttempts; attempt++)
        {
            // Offset RA slightly westward (lower RA = more positive HA) to ensure
            // the mount doesn't land right on the meridian and flip back
            var offsetRA = observation.Target.RA - raOffsetHours * attempt;
            if (offsetRA < 0) offsetRA += 24;
            var slewTarget = observation.Target with { RA = offsetRA };

            External.AppLogger.LogInformation("Meridian flip: slewing to {Target} (attempt {Attempt}/{MaxAttempts}, RA offset {Offset:F3}h).",
                observation.Target, attempt, maxFlipAttempts, raOffsetHours * attempt);

            // Ensure no slew is in progress before starting the flip slew
            if (await CatchAsync(mount.Driver.IsSlewingAsync, cancellationToken))
            {
                await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            var (postCondition, _) = await mount.Driver.BeginSlewToTargetAsync(
                slewTarget, Configuration.MinHeightAboveHorizon, cancellationToken).ConfigureAwait(false);

            if (postCondition is not SlewPostCondition.Slewing)
            {
                External.AppLogger.LogError("Meridian flip: slew failed with {PostCondition} on attempt {Attempt}.", postCondition, attempt);
                continue;
            }

            if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
            {
                External.AppLogger.LogError("Meridian flip: slew did not complete on attempt {Attempt}.", attempt);
                continue;
            }

            var newHourAngle = await mount.Driver.GetHourAngleAsync(cancellationToken);
            External.AppLogger.LogInformation("Meridian flip: slew complete, HA={NewHA:F4}h (attempt {Attempt}).", newHourAngle, attempt);

            // Verify the HA is now positive (west of meridian) — the flip actually happened
            if (newHourAngle > 0)
            {
                // Iterative plate-solve centering after flip
                _currentActivity = $"Centering on {observation.Target.Name} after flip\u2026";
                if (!await CenterOnTargetAsync(observation.Target, 0, thresholdArcmin: 1.0, maxAttempts: 5, cancellationToken))
                {
                    External.AppLogger.LogWarning("Meridian flip: centering did not converge, proceeding with current pointing.");
                }

                External.AppLogger.LogInformation("Meridian flip: restarting guiding for {Target}.", observation.Target);
                if (!await guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, cancellationToken).ConfigureAwait(false))
                {
                    External.AppLogger.LogError("Meridian flip: failed to restart guider after flip for {Target}.", observation.Target);
                    return MeridianFlipResult.Failed;
                }

                External.AppLogger.LogInformation("Meridian flip: completed successfully for {Target}, HA={NewHA:F4}h.", observation.Target, newHourAngle);
                return new MeridianFlipResult(true, newHourAngle);
            }

            External.AppLogger.LogWarning("Meridian flip: HA={NewHA:F4}h still east of meridian after attempt {Attempt}, retrying with larger offset.",
                newHourAngle, attempt);
        }

        External.AppLogger.LogError("Meridian flip: failed after {MaxAttempts} attempts for {Target}.", maxFlipAttempts, observation.Target);
        return MeridianFlipResult.Failed;
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
        if (await mount.Driver.TryGetTransformAsync(cancellationToken) is not { } transform)
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

        // Target is rising — scan forward to find when it clears the threshold
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
            await External.SleepAsync(pollInterval, cancellationToken);
            elapsed += pollInterval;

            var camera = Setup.Telescopes[telescopeIndex].Camera.Driver;

            // Take a short test exposure to check conditions
            var testExposure = TimeSpan.FromSeconds(Math.Min(baseline.Exposure.TotalSeconds, 5));
            await camera.StartExposureAsync(testExposure, cancellationToken: cancellationToken);
            await External.SleepAsync(testExposure + TimeSpan.FromSeconds(2), cancellationToken);

            if (!await camera.GetImageReadyAsync(cancellationToken))
            {
                continue;
            }

            var image = await ((ICameraDriver)camera).GetImageAsync(cancellationToken);
            if (image is null)
            {
                continue;
            }

            var stars = await image.FindStarsAsync(0, snrMin: 10, maxStars: 100, cancellationToken: cancellationToken);
            var currentGain = await camera.GetGainAsync(cancellationToken);
            var metrics = FrameMetrics.FromStarList(stars, testExposure, currentGain);

            if (!metrics.IsValid)
            {
                External.AppLogger.LogInformation("Condition check: {Stars} stars detected (waiting for recovery, {Elapsed}/{Timeout}).",
                    stars.Count, elapsed, timeout);
                continue;
            }

            var starCountRatio = (float)metrics.StarCount / baseline.StarCount;
            External.AppLogger.LogInformation("Condition check: {Stars} stars (ratio={Ratio:F2} vs baseline {Baseline}, {Elapsed}/{Timeout}).",
                metrics.StarCount, starCountRatio, baseline.StarCount, elapsed, timeout);

            if (starCountRatio >= Configuration.ConditionDeteriorationThreshold)
            {
                return true;
            }
        }

        return false;
    }
}