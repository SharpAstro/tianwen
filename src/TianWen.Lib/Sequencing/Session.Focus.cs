using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    /// <summary>
    /// Rough focus in this context is defined as: at least 15 stars can be detected by plate-solving when doing a short, high-gain exposure.
    /// Assumes that zenith is visible, which should hopefully be the default for most setups.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>true iff all cameras have at least rough focus.</returns>
    internal async ValueTask<bool> InitialRoughFocusAsync(CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var distMeridian = TimeSpan.FromMinutes(15);

        if (!await mount.Driver.EnsureTrackingAsync(cancellationToken: cancellationToken))
        {
            External.AppLogger.LogError("Failed to enable tracking of {Mount}.", mount);

            return false;
        }

        External.AppLogger.LogInformation("Slew mount {Mount} near zenith to verify that we have rough focus.", mount);

        // coordinates not quite accurate at this point (we have not plate-solved yet) but good enough for this purpose.
        await mount.Driver.BeginSlewToZenithAsync(distMeridian, cancellationToken).ConfigureAwait(false);
        var slewTime = await GetMountUtcNowAsync(cancellationToken);

        _currentActivity = "Waiting for slew to complete\u2026";
        External.AppLogger.LogInformation("RoughFocus: waiting for slew to complete...");
        if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            External.AppLogger.LogError("Failed to complete slewing of mount {Mount}", mount);

            return false;
        }

        // Update camera targets with current mount position for FITS headers and synthetic star rendering
        var zenithRa = await mount.Driver.GetRightAscensionAsync(cancellationToken);
        var zenithDec = await mount.Driver.GetDeclinationAsync(cancellationToken);
        var zenithTarget = new Target(zenithRa, zenithDec, "Zenith", null);
        for (var i = 0; i < Setup.Telescopes.Length; i++)
        {
            Setup.Telescopes[i].Camera.Driver.Target = zenithTarget;
        }

        _currentActivity = "Guider plate-solve (60s timeout)\u2026";
        External.AppLogger.LogInformation("RoughFocus: slew complete, starting guider plate-solve loop (1 min timeout)...");
        if (!await GuiderFocusLoopAsync(TimeSpan.FromMinutes(1), cancellationToken))
        {
            External.AppLogger.LogWarning("RoughFocus: guider focus loop timed out or failed, continuing with rough focus detection.");
        }

        var count = Setup.Telescopes.Length;

        // Move filter wheels to the focus filter before rough focus
        for (var i = 0; i < count; i++)
        {
            var telescope = Setup.Telescopes[i];
            if (telescope.FilterWheel?.Driver is { Connected: true, Filters.Count: > 0 } fwDriver)
            {
                var refFilter = FilterPlanBuilder.GetReferenceFilter(fwDriver.Filters, telescope.OpticalDesign);
                if (refFilter >= 0)
                {
                    await SwitchFilterIfNeededAsync(i, fwDriver, refFilter, cancellationToken);
                }
            }
        }

        var origGain = new short[count];
        for (var i = 0; i < count; i++)
        {
            var camDriver = Setup.Telescopes[i].Camera.Driver;

            if (camDriver.UsesGainValue)
            {
                origGain[i] = await camDriver.GetGainAsync(cancellationToken);

                // set high gain
                await camDriver.SetGainAsync((short)MathF.Truncate((camDriver.GainMin + camDriver.GainMin) * 0.75f), cancellationToken);
            }
            else
            {
                origGain[i] = short.MinValue;
            }

            await camDriver.StartExposureAsync(TimeSpan.FromSeconds(1), cancellationToken: cancellationToken);
        }

        var expTimesSec = new int[count];
        var hasRoughFocus = new bool[count];
        Array.Fill(expTimesSec, 1);

        while (!cancellationToken.IsCancellationRequested)
        {
            for (var i = 0; i < count && !cancellationToken.IsCancellationRequested; i++)
            {
                var camDriver = Setup.Telescopes[i].Camera.Driver;

                if (cancellationToken.IsCancellationRequested) break;

                if (await camDriver.GetImageAsync(cancellationToken) is { Width: > 0, Height: > 0 } image)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var stars = await image.FindStarsAsync(0, snrMin: 15, cancellationToken: cancellationToken);

                    _currentActivity = $"Stars: {stars.Count}/15 (exposure {expTimesSec[i]}s)";
                    External.AppLogger.LogInformation("RoughFocus: telescope #{TelescopeNumber} exposure {ExpTime}s → {StarCount} stars detected (need ≥15)",
                        i + 1, expTimesSec[i], stars.Count);

                    if (stars.Count < 15)
                    {
                        expTimesSec[i]++;

                        if (await GetMountUtcNowAsync(cancellationToken) - slewTime + TimeSpan.FromSeconds(count * 5 + expTimesSec[i]) < distMeridian)
                        {
                            await camDriver.StartExposureAsync(TimeSpan.FromSeconds(expTimesSec[i]), cancellationToken: cancellationToken);
                        }
                    }
                    else
                    {
                        if (camDriver.UsesGainValue && origGain[i] is >= 0)
                        {
                            await camDriver.SetGainAsync(origGain[i], cancellationToken);
                        }

                        hasRoughFocus[i] = true;
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested) break;

            // slew back to start position
            if (await GetMountUtcNowAsync(cancellationToken) - slewTime > distMeridian)
            {
                await mount.Driver.BeginSlewToZenithAsync(distMeridian, cancellationToken).ConfigureAwait(false);
                
                slewTime = await GetMountUtcNowAsync(cancellationToken);

                if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
                {
                    External.AppLogger.LogError("Failed to complete slewing of mount {Mount}", mount);

                    return false;
                }
            }

            if (hasRoughFocus.All(v => v))
            {
                return true;
            }

            await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Runs V-curve auto-focus for all telescopes that have a focuser attached.
    /// Stores the resulting baseline metrics per telescope for drift detection.
    /// </summary>
    internal async ValueTask<bool> AutoFocusAllTelescopesAsync(CancellationToken cancellationToken)
    {
        var scopes = Setup.Telescopes.Length;
        var baselines = new FrameMetrics[scopes];
        var allConverged = true;

        for (var i = 0; i < scopes; i++)
        {
            var (converged, baseline) = await AutoFocusAsync(i, cancellationToken);
            baselines[i] = baseline;
            if (!converged)
            {
                allConverged = false;
            }
        }

        SetBaselineForCurrentObservation(baselines);
        return allConverged;
    }

    private int ActiveObservationIndex => _activeObservation is >= 0 ? _activeObservation : 0;

    private void SetBaselineForCurrentObservation(FrameMetrics[] baselines)
    {
        var obsIndex = ActiveObservationIndex;
        _baselineByObservation[obsIndex] = baselines;
        _baselineSamples.TryRemove(obsIndex, out _);
    }

    private FrameMetrics[]? GetBaselineForCurrentObservation()
    {
        return _baselineByObservation.TryGetValue(ActiveObservationIndex, out var baselines) ? baselines : null;
    }

    /// <summary>
    /// Accumulates frame metrics from the first frames of a new target.
    /// Once <see cref="SessionConfiguration.BaselineHfdFrameCount"/> samples are collected,
    /// the median metrics are used as the baseline for focus drift detection.
    /// </summary>
    private void AccumulateBaselineSample(int telescopeIndex, FrameMetrics metrics)
    {
        var obsIndex = ActiveObservationIndex;
        var scopes = Setup.Telescopes.Length;
        var samples = _baselineSamples.GetOrAdd(obsIndex, _ =>
        {
            var arr = new List<FrameMetrics>[scopes];
            for (var j = 0; j < scopes; j++)
            {
                arr[j] = new List<FrameMetrics>();
            }
            return arr;
        });

        samples[telescopeIndex].Add(metrics);

        if (samples[telescopeIndex].Count >= Configuration.BaselineHfdFrameCount)
        {
            var frameSamples = samples[telescopeIndex];
            frameSamples.Sort((a, b) => a.MedianHfd.CompareTo(b.MedianHfd));
            var medianIndex = frameSamples.Count / 2;
            var medianMetrics = frameSamples[medianIndex];

            var baselines = GetBaselineForCurrentObservation() ?? new FrameMetrics[scopes];
            baselines[telescopeIndex] = medianMetrics;
            SetBaselineForCurrentObservation(baselines);

            External.AppLogger.LogInformation(
                "Established baseline for telescope #{TelescopeNumber} on observation #{ObservationIndex}: HFD={BaselineHFD:F2}, FWHM={BaselineFWHM:F2}, stars={StarCount} (from {FrameCount} frames).",
                telescopeIndex + 1, obsIndex + 1, medianMetrics.MedianHfd, medianMetrics.MedianFwhm, medianMetrics.StarCount, Configuration.BaselineHfdFrameCount);
        }
    }

    private void AppendFocusRunRecord(
        int telescopeIndex, OTA telescope, IFilterWheelDriver? filterWheelDriver,
        int filterPosition, int bestPos, float bestHfd, MetricSampleMap sampleMap)
    {
        var filterName = filterPosition >= 0 && filterWheelDriver?.Filters is { } filters && filterPosition < filters.Count
            ? filters[filterPosition].DisplayName
            : "?";

        // Build curve from sample map
        var curveBuilder = System.Collections.Immutable.ImmutableArray.CreateBuilder<(int Position, float Hfd)>();
        foreach (var sample in sampleMap.Keys())
        {
            var aggregated = sampleMap.Aggregate(sample);
            if (aggregated.HasValue)
            {
                curveBuilder.Add((sample, aggregated.Value));
            }
        }
        curveBuilder.Sort((a, b) => a.Position.CompareTo(b.Position));

        _focusHistory.Enqueue(new FocusRunRecord(
            Timestamp: External.TimeProvider.GetUtcNow(),
            OtaName: telescope.Name,
            FilterName: filterName,
            BestPosition: bestPos,
            BestHfd: bestHfd,
            Curve: curveBuilder.ToImmutable()));
    }

    /// <summary>
    /// Performs V-curve auto-focus for a single telescope: scans focuser positions,
    /// takes short exposures, measures median HFD, fits hyperbola, moves to best focus.
    /// </summary>
    /// <returns>Whether the fit converged, and the baseline metrics at best focus.</returns>
    internal async ValueTask<(bool Converged, FrameMetrics Baseline)> AutoFocusAsync(int telescopeIndex, CancellationToken cancellationToken)
    {
        var telescope = Setup.Telescopes[telescopeIndex];
        var focuser = telescope.Focuser?.Driver;
        var camera = telescope.Camera.Driver;

        if (focuser is not { Connected: true })
        {
            External.AppLogger.LogWarning("Telescope #{TelescopeNumber} has no connected focuser, skipping auto-focus.", telescopeIndex + 1);
            return (false, default);
        }

        // Determine which filter to focus on based on the strategy
        var preFocusFilterPosition = -1;
        var filterWheelDriver = telescope.FilterWheel?.Driver is { Connected: true } fwd ? fwd : null;
        if (filterWheelDriver is { Filters.Count: > 0 })
        {
            var strategy = Configuration.FocusFilterStrategy;
            if (strategy is FocusFilterStrategy.UseLuminance)
            {
                preFocusFilterPosition = FilterPlanBuilder.GetReferenceFilter(filterWheelDriver.Filters, telescope.OpticalDesign);
                if (preFocusFilterPosition < 0)
                {
                    preFocusFilterPosition = 0; // fallback to first filter
                }
            }
            else if (strategy is FocusFilterStrategy.Auto)
            {
                var refFilter = FilterPlanBuilder.GetReferenceFilter(filterWheelDriver.Filters, telescope.OpticalDesign);
                if (refFilter >= 0)
                {
                    // Mirror-based or has offsets: focus on luminance
                    preFocusFilterPosition = refFilter;
                }
                // else: refFilter == -1 means refractive + no offsets → focus on current (scheduled) filter
            }
            // UseScheduledFilter: keep preFocusFilterPosition = -1 (don't switch)

            if (preFocusFilterPosition >= 0)
            {
                await SwitchFilterIfNeededAsync(telescopeIndex, filterWheelDriver, preFocusFilterPosition, cancellationToken);
            }
        }

        var autoFocusExposure = TimeSpan.FromSeconds(2);
        var currentGain = await camera.GetGainAsync(cancellationToken);
        var currentPos = await focuser.GetPositionAsync(cancellationToken);
        var range = Configuration.AutoFocusRange;
        var stepCount = Configuration.AutoFocusStepCount;
        var stepSize = range / (stepCount - 1);
        var startPos = Math.Max(0, currentPos - range / 2);

        External.AppLogger.LogInformation("Auto-focus telescope #{TelescopeNumber}: scanning {StepCount} positions from {StartPos} with step size {StepSize}.",
            telescopeIndex + 1, stepCount, startPos, stepSize);

        var sampleMap = new MetricSampleMap(SampleKind.HFD, AggregationMethod.Median);

        // Move to start position with backlash compensation
        var focusDir = telescope.FocusDirection;
        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, startPos, currentPos, focuser.BacklashStepsIn, focuser.BacklashStepsOut, focusDir, External, cancellationToken);

        // Scan from start to end (always moving outward — no backlash needed)
        for (var i = 0; i < stepCount && !cancellationToken.IsCancellationRequested; i++)
        {
            var targetPos = startPos + i * stepSize;
            _currentActivity = $"#{telescopeIndex + 1} V-curve {i + 1}/{stepCount} pos={targetPos}";
            await focuser.BeginMoveAsync(targetPos, cancellationToken);
            while (await focuser.GetIsMovingAsync(cancellationToken) && !cancellationToken.IsCancellationRequested)
            {
                await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            camera.FocusPosition = targetPos;

            // Update camera state for the UI
            if (telescopeIndex < _cameraStates.Length)
            {
                var focTemp = await CatchAsync(focuser.GetTemperatureAsync, cancellationToken, double.NaN);
                _cameraStates[telescopeIndex] = new CameraExposureState(
                    telescopeIndex, External.TimeProvider.GetUtcNow(), autoFocusExposure,
                    i + 1, $"Focus {i + 1}/{stepCount}", targetPos, Devices.CameraState.Exposing,
                    focTemp, FocuserIsMoving: false);
            }

            await camera.StartExposureAsync(autoFocusExposure, cancellationToken: cancellationToken);

            Image? image = null;
            var retries = 0;
            while (image is null && retries++ < 100 && !cancellationToken.IsCancellationRequested)
            {
                image = await camera.GetImageAsync(cancellationToken);
                if (image is null)
                {
                    await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
                }
            }

            if (image is { Width: > 0, Height: > 0 })
            {
                // Update state: downloading / processing
                if (telescopeIndex < _cameraStates.Length)
                {
                    _cameraStates[telescopeIndex] = _cameraStates[telescopeIndex] with { State = Devices.CameraState.Download };
                }
                // Make focus image available for the mini viewer
                if (telescopeIndex < _lastCapturedImages.Length)
                {
                    _lastCapturedImages[telescopeIndex] = image.ScaleFloatValuesToUnitInPlace();
                }

                var stars = await image.FindStarsAsync(0, snrMin: 10, cancellationToken: cancellationToken);
                if (stars.Count > 3)
                {
                    var hfd = stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
                    sampleMap.AddSampleAtFocusPosition(targetPos, hfd);
                    External.AppLogger.LogDebug("Auto-focus pos={Position} stars={StarCount} HFD={HFD:F2}", targetPos, stars.Count, hfd);
                }
                else
                {
                    External.AppLogger.LogDebug("Auto-focus pos={Position} too few stars ({StarCount})", targetPos, stars.Count);
                }
            }
        }

        // Reset camera state after V-curve loop
        if (telescopeIndex < _cameraStates.Length)
        {
            _cameraStates[telescopeIndex] = default;
        }

        // Fit hyperbola
        _currentActivity = $"#{telescopeIndex + 1} Fitting hyperbola\u2026";
        if (sampleMap.TryGetBestFocusSolution(out var solution, out _, out _))
        {
            var bestPos = (int)Math.Round(solution.Value.BestFocus);
            var currentPosNow = await focuser.GetPositionAsync(cancellationToken);

            _currentActivity = $"#{telescopeIndex + 1} Moving to best focus ({bestPos})";
            External.AppLogger.LogInformation("Auto-focus telescope #{TelescopeNumber}: best focus at position {BestFocus} (A={A:F2}, B={B:F2}, error={Error:F4}).",
                telescopeIndex + 1, bestPos, solution.Value.A, solution.Value.B, solution.Value.Error);

            await BacklashCompensation.MoveWithCompensationAsync(
                focuser, bestPos, currentPosNow, focuser.BacklashStepsIn, focuser.BacklashStepsOut, focusDir, External, cancellationToken);

            // Take a verification exposure at best focus to get baseline HFD
            camera.FocusPosition = bestPos;
            await camera.StartExposureAsync(TimeSpan.FromSeconds(2), cancellationToken: cancellationToken);

            Image? verifyImage = null;
            var retries = 0;
            while (verifyImage is null && retries++ < 100 && !cancellationToken.IsCancellationRequested)
            {
                verifyImage = await camera.GetImageAsync(cancellationToken);
                if (verifyImage is null)
                {
                    await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
                }
            }

            if (verifyImage is { Width: > 0, Height: > 0 })
            {
                var verifyStars = await verifyImage.FindStarsAsync(0, snrMin: 10, cancellationToken: cancellationToken);
                if (verifyStars.Count > 3)
                {
                    var baseline = FrameMetrics.FromStarList(verifyStars, autoFocusExposure, currentGain);
                    External.AppLogger.LogInformation("Auto-focus telescope #{TelescopeNumber}: baseline HFD={BaselineHFD:F2}, FWHM={BaselineFWHM:F2}, stars={StarCount}.",
                        telescopeIndex + 1, baseline.MedianHfd, baseline.MedianFwhm, baseline.StarCount);
                    AppendFocusRunRecord(telescopeIndex, telescope, filterWheelDriver, preFocusFilterPosition, bestPos, baseline.MedianHfd, sampleMap);
                    return (true, baseline);
                }
            }

            // Fit converged but we couldn't measure baseline — use the hyperbola minimum as HFD estimate
            AppendFocusRunRecord(telescopeIndex, telescope, filterWheelDriver, preFocusFilterPosition, bestPos, (float)solution.Value.A, sampleMap);
            return (true, new FrameMetrics(0, (float)solution.Value.A, float.NaN, autoFocusExposure, currentGain));
        }

        External.AppLogger.LogWarning("Auto-focus telescope #{TelescopeNumber}: hyperbola fit did not converge.", telescopeIndex + 1);
        return (false, default);
    }

    /// <summary>
    /// Takes a short exposure on the main imaging camera, plate solves the image,
    /// and syncs the mount to the solved J2000 coordinates for accurate pointing.
    /// </summary>
    /// <param name="telescopeIndex">Which telescope/camera to use (default 0).</param>
    /// <param name="exposureTime">Exposure duration for the plate solve frame.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if plate solve succeeded and mount was synced.</returns>
    internal async ValueTask<bool> PlateSolveAndSyncAsync(int telescopeIndex, TimeSpan exposureTime, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var camDriver = Setup.Telescopes[telescopeIndex].Camera.Driver;

        // Take a short exposure
        await camDriver.StartExposureAsync(exposureTime, cancellationToken: cancellationToken);

        // Wait for exposure to complete
        Image? image = null;
        var polled = TimeSpan.Zero;
        var maxPoll = exposureTime + exposureTime; // wait up to 2x exposure time
        while (polled < maxPoll && !cancellationToken.IsCancellationRequested)
        {
            if (await camDriver.GetImageAsync(cancellationToken) is { Width: > 0, Height: > 0 } img)
            {
                image = img;
                break;
            }

            var spinDuration = TimeSpan.FromMilliseconds(250);
            polled += spinDuration;
            await External.SleepAsync(spinDuration, cancellationToken);
        }

        if (image is null)
        {
            External.AppLogger.LogWarning("Plate solve: failed to capture image from camera #{CameraNumber}.", telescopeIndex + 1);
            return false;
        }

        // Plate solve using mount's current position as search origin
        var mountRa = await mount.Driver.GetRightAscensionAsync(cancellationToken);
        var mountDec = await mount.Driver.GetDeclinationAsync(cancellationToken);
        var searchOrigin = new WCS(mountRa, mountDec);

        var result = await PlateSolver.SolveImageAsync(image, searchOrigin: searchOrigin, searchRadius: 10, cancellationToken: cancellationToken);

        if (result.Solution is not { } wcs)
        {
            External.AppLogger.LogWarning("Plate solve: failed to solve image from camera #{CameraNumber}.", telescopeIndex + 1);
            return false;
        }

        External.AppLogger.LogInformation(
            "Plate solve: solved at ({SolvedRA}, {SolvedDec}), mount was at ({MountRA}, {MountDec}), offset=({DeltaRA:F4}h, {DeltaDec:F2}°).",
            Astrometry.CoordinateUtils.HoursToHMS(wcs.CenterRA), Astrometry.CoordinateUtils.DegreesToDMS(wcs.CenterDec),
            Astrometry.CoordinateUtils.HoursToHMS(mountRa), Astrometry.CoordinateUtils.DegreesToDMS(mountDec),
            wcs.CenterRA - mountRa, wcs.CenterDec - mountDec);

        // Sync mount to solved J2000 coordinates
        await mount.Driver.SyncRaDecJ2000Async(wcs.CenterRA, wcs.CenterDec, cancellationToken);

        External.AppLogger.LogInformation("Plate solve: mount synced to solved position.");
        return true;
    }

    internal async ValueTask<bool> GuiderFocusLoopAsync(TimeSpan timeoutAfter, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var guider = Setup.Guider;

        var plateSolveTimeout = timeoutAfter > TimeSpan.FromSeconds(5) ? timeoutAfter - TimeSpan.FromSeconds(3) : timeoutAfter;

        Astrometry.PlateSolve.PlateSolveResult result;
        try
        {
            result = await guider.Driver.PlateSolveGuiderImageAsync(PlateSolver,
                await mount.Driver.GetRightAscensionAsync(cancellationToken),
                await mount.Driver.GetDeclinationAsync(cancellationToken),
                plateSolveTimeout,
                10d,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            External.AppLogger.LogWarning(ex, "Guider plate-solve failed");
            return false;
        }

        if (result.Solution is var (solvedRa, solvedDec))
        {
            External.AppLogger.LogInformation("Guider \"{GuiderName}\" is in focus and camera image plate solve succeeded with ({SolvedRa}, {SolvedDec})",
                guider.Driver, solvedRa, solvedDec);
            return true;
        }
        else
        {
            External.AppLogger.LogWarning("Failed to plate solve guider \"{GuiderName}\" without a specific reason (probably not enough stars detected)",
                guider.Driver);
        }

        return false;
    }



}
