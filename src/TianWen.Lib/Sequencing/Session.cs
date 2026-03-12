using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using static TianWen.Lib.Astrometry.CoordinateUtils;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Sequencing;

internal record Session(
    Setup Setup,
    in SessionConfiguration Configuration,
    IPlateSolver PlateSolver,
    IExternal External,
    IReadOnlyList<Observation> PlannedObservations
) : ISession
{
    const int UNINITIALIZED_OBSERVATION_INDEX = -1;

    private readonly ConcurrentQueue<GuiderEventArgs> _guiderEvents = [];
    private readonly ConcurrentDictionary<int, FrameMetrics[]> _baselineByObservation = [];
    private readonly ConcurrentDictionary<int, List<FrameMetrics>[]> _baselineSamples = [];
    private int _activeObservation = UNINITIALIZED_OBSERVATION_INDEX;

    /// <summary>
    /// Per-observation, per-telescope baseline metrics for focus drift and environmental anomaly detection.
    /// Keyed by observation index because metrics vary with sky area, altitude, and guiding quality.
    /// </summary>
    internal IReadOnlyDictionary<int, FrameMetrics[]> BaselineByObservation => _baselineByObservation;

    public Observation? ActiveObservation => _activeObservation is int active and >= 0 && active < PlannedObservations.Count ? PlannedObservations[active] : null;

    private int AdvanceObservation() => Interlocked.Increment(ref _activeObservation);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var active = AdvanceObservation();
            // run initialisation code
            if (active == 0)
            {
                if (!await InitialisationAsync(cancellationToken))
                {
                    External.AppLogger.LogError("Initialization failed, aborting session.");
                    return;
                }
            }
            else if (ActiveObservation is null)
            {
                External.AppLogger.LogInformation("Session complete, finished {ObservationCount} observations, finalizing.", _activeObservation);
                return;
            }

            await WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(cancellationToken).ConfigureAwait(false);

            await CoolCamerasToSetpointAsync(Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, SetupointDirection.Down, cancellationToken).ConfigureAwait(false);

            // TODO wait until 5 min to astro dark, and/or implement IExternal.IsPolarAligned

            if (!await InitialRoughFocusAsync(cancellationToken))
            {
                External.AppLogger.LogError("Failed to focus cameras (first time), aborting session.");
                return;
            }

            if (!await AutoFocusAllTelescopesAsync(cancellationToken))
            {
                External.AppLogger.LogWarning("Auto-focus did not converge for all telescopes, proceeding with rough focus.");
            }

            await CalibrateGuiderAsync(cancellationToken).ConfigureAwait(false);

            await ObservationLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            External.AppLogger.LogError(e, "Exception while in main run loop, unrecoverable, aborting session.");
        }
        finally
        {
            await Finalise(cancellationToken).ConfigureAwait(false);
        }
    }

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

        if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            External.AppLogger.LogError("Failed to complete slewing of mount {Mount}", mount);

            return false;
        }
        
        if (!await GuiderFocusLoopAsync(TimeSpan.FromMinutes(1), cancellationToken))
        {
            return false;
        }

        var count = Setup.Telescopes.Count;
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
            for (var i = 0; i < count; i++)
            {
                var camDriver = Setup.Telescopes[i].Camera.Driver;

                if (await camDriver.GetImageAsync(cancellationToken) is { Width: > 0, Height: > 0 } image)
                {
                    var stars = await image.FindStarsAsync(0, snrMin: 15, cancellationToken: cancellationToken);

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
        var scopes = Setup.Telescopes.Count;
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
        var scopes = Setup.Telescopes.Count;
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
            await focuser.BeginMoveAsync(targetPos, cancellationToken);
            while (await focuser.GetIsMovingAsync(cancellationToken) && !cancellationToken.IsCancellationRequested)
            {
                await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            camera.FocusPosition = targetPos;
            await camera.StartExposureAsync(TimeSpan.FromSeconds(2), cancellationToken: cancellationToken);

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

        // Fit hyperbola
        if (sampleMap.TryGetBestFocusSolution(out var solution, out _, out _))
        {
            var bestPos = (int)Math.Round(solution.Value.BestFocus);
            var currentPosNow = await focuser.GetPositionAsync(cancellationToken);

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
                    return (true, baseline);
                }
            }

            // Fit converged but we couldn't measure baseline — use the hyperbola minimum as HFD estimate
            return (true, new FrameMetrics(0, (float)solution.Value.A, float.NaN, autoFocusExposure, currentGain));
        }

        External.AppLogger.LogWarning("Auto-focus telescope #{TelescopeNumber}: hyperbola fit did not converge.", telescopeIndex + 1);
        return (false, default);
    }

    private async ValueTask<bool> GuiderFocusLoopAsync(TimeSpan timeoutAfter, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var guider = Setup.Guider;

        var plateSolveTimeout = timeoutAfter > TimeSpan.FromSeconds(5) ? timeoutAfter - TimeSpan.FromSeconds(3) : timeoutAfter;

        var result = await guider.Driver.PlateSolveGuiderImageAsync(PlateSolver,
            await mount.Driver.GetRightAscensionAsync(cancellationToken),
            await mount.Driver.GetDeclinationAsync(cancellationToken),
            plateSolveTimeout,
            10d,
            cancellationToken
        );

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

    internal async ValueTask CalibrateGuiderAsync(CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;

        // TODO: maybe slew slightly above/below 0 declination to avoid trees, etc.
        // slew half an hour to meridian, plate solve and slew closer
        var dec = 0;
        await mount.Driver.BeginSlewHourAngleDecAsync(TimeSpan.FromMinutes(30).TotalHours, dec, cancellationToken).ConfigureAwait(false);

        if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to slew mount {mount} to guider calibration position (near meridian, {DegreesToDMS(dec)} declination)");
        }

        // TODO: plate solve and sync and reslew

        var guider = Setup.Guider;

        if (!await guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to start guider loop of guider {guider.Driver}");
        }
    }

    internal async ValueTask Finalise(CancellationToken cancellationToken)
    {
        External.AppLogger.LogInformation("Executing session run finaliser: Stop guiding, stop tracking, disconnect guider, close covers, cool to ambient temp, turn off cooler, park scope.");

        var mount = Setup.Mount;
        var guider = Setup.Guider;

        var maybeCoversClosed = null as bool?;
        var maybeCooledCamerasToAmbient = null as bool?;

        var guiderStopped = await CatchAsync(async cancellationToken =>
        {
            await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return !await guider.Driver.IsGuidingAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        var trackingStopped = await CatchAsync(async cancellationToken => mount.Driver.CanSetTracking && !await mount.Driver.IsTrackingAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

        if (trackingStopped)
        {
            maybeCoversClosed ??= await CatchAsync(CloseCoversAsync, cancellationToken).ConfigureAwait(false);
            maybeCooledCamerasToAmbient ??= await CatchAsync(TurnOffCameraCoolingAsync, cancellationToken).ConfigureAwait(false);
        }

        var guiderDisconnected = await CatchAsync(guider.Driver.DisconnectAsync, cancellationToken).ConfigureAwait(false);
        bool parkInitiated = Catch(() => mount.Driver.CanPark) && await CatchAsync(mount.Driver.ParkAsync, cancellationToken).ConfigureAwait(false);

        var parkCompleted = parkInitiated && await CatchAsync(async cancellationToken =>
        {
            int i = 0;
            while (!await mount.Driver.AtParkAsync(cancellationToken) && i++ < IDeviceDriver.MAX_FAILSAFE)
            {
                await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            return await mount.Driver.AtParkAsync(cancellationToken);
        }, cancellationToken);

        if (parkCompleted)
        {
            maybeCoversClosed ??= await CatchAsync(CloseCoversAsync, cancellationToken).ConfigureAwait(false);
            maybeCooledCamerasToAmbient ??= await CatchAsync(TurnOffCameraCoolingAsync, cancellationToken).ConfigureAwait(false);
        }

        var coversClosed = maybeCoversClosed ??= await CatchAsync(CloseCoversAsync, cancellationToken).ConfigureAwait(false);
        var cooledCamerasToAmbient = maybeCooledCamerasToAmbient ??= await CatchAsync(TurnOffCameraCoolingAsync, cancellationToken).ConfigureAwait(false);

        var mountDisconnected = await CatchAsync(mount.Driver.DisconnectAsync, cancellationToken).ConfigureAwait(false);

        var shutdownReport = new Dictionary<string, bool>
        {
            ["Covers closed"] = coversClosed,
            ["Tracking stopped"] = trackingStopped,
            ["Guider stopped"] = guiderStopped,
            ["Park initiated"] = parkInitiated,
            ["Park completed"] = parkCompleted,
            ["Camera cooler at ambient"] = cooledCamerasToAmbient,
            ["Guider disconnected"] = guiderDisconnected,
            ["Mount disconnected"] = mountDisconnected
        };

        for (var i = 0; i < Setup.Telescopes.Count; i++)
        {
            var camDriver = Setup.Telescopes[i].Camera.Driver;
            if (Catch(() => camDriver.CanGetCoolerOn))
            {
                shutdownReport[$"Camera #{(i + 1)} Cooler Off"] = await CatchAsync(async ct =>
                {
                    if (await camDriver.GetCoolerOnAsync(ct))
                    {
                        await camDriver.SetCoolerOnAsync(false, ct);
                    }
                    return !await camDriver.GetCoolerOnAsync(ct);
                }, cancellationToken);
            }
            if (Catch(() => camDriver.CanGetCoolerPower))
            {
                shutdownReport[$"Camera #{(i + 1)} Cooler Power <= 0.1"] = await CatchAsync(async ct => await camDriver.GetCoolerPowerAsync(ct) is <= 0.1, cancellationToken);
            }
            if (Catch(() => camDriver.CanGetHeatsinkTemperature))
            {
                shutdownReport[$"Camera #{(i + 1)} Temp near ambient"] = await CatchAsync(async ct => Math.Abs(await camDriver.GetCCDTemperatureAsync(ct) - await camDriver.GetHeatSinkTemperatureAsync(ct)) < 1d, cancellationToken);
            }
        }

        if (shutdownReport.Values.Any(v => !v))
        {
            External.AppLogger.LogError("Partially failed shut-down of session: {@ShutdownReport}", shutdownReport.Select(p => p.Key + ": " + (p.Value ? "success" : "fail")));
        }
        else
        {
            External.AppLogger.LogInformation("Shutdown complete, session ended. Please turn off mount and camera cooler power.");
        }

        ValueTask<bool> CloseCoversAsync(CancellationToken cancellationToken) => MoveTelescopeCoversToStateAsync(CoverStatus.Closed, cancellationToken);

        ValueTask<bool> TurnOffCameraCoolingAsync(CancellationToken cancellationToken) => CoolCamerasToAmbientAsync(Configuration.WarmupRampInterval);
    }

    /// <summary>
    /// Does one-time (per session) initialisation, e.g. connecting, unparking
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>True if initialisation was successful.</returns>
    internal async ValueTask<bool> InitialisationAsync(CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var guider = Setup.Guider;

        await mount.Driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await guider.Driver.ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (await mount.Driver.AtParkAsync(cancellationToken)
            && (!mount.Driver.CanUnpark || !await CatchAsync(mount.Driver.UnparkAsync, cancellationToken).ConfigureAwait(false)))
        {
            External.AppLogger.LogError("Mount {Mount} is parked but cannot be unparked. Aborting.", mount);

            return false;
        }

        // try set the time to our time if supported
        await mount.Driver.SetUTCDateAsync(External.TimeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        for (var i = 0; i < Setup.Telescopes.Count; i++)
        {
            var telescope = Setup.Telescopes[i];
            var camera = telescope.Camera;
            await camera.Driver.ConnectAsync(cancellationToken).ConfigureAwait(false);

            // copy over denormalised properties if required
            camera.Driver.Telescope ??= telescope.Name;
            if (camera.Driver.FocalLength is <= 0)
            {
                camera.Driver.FocalLength = telescope.FocalLength;
            }
            camera.Driver.Latitude ??= await mount.Driver.GetSiteLatitudeAsync(cancellationToken);
            camera.Driver.Longitude ??= await mount.Driver.GetSiteLongitudeAsync(cancellationToken);
        }

        if (!await CoolCamerasToSensorTempAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
        {
            External.AppLogger.LogError("Failed to set camera cooler setpoint to current CCD temperature, aborting session.");
            return false;
        }

        if (await MoveTelescopeCoversToStateAsync(CoverStatus.Open, CancellationToken.None))
        {
            External.AppLogger.LogInformation("All covers opened, and calibrator turned off.");
        }
        else
        {
            External.AppLogger.LogError("Openening telescope covers failed, aborting session.");
            return false;
        }

        guider.Driver.GuiderStateChangedEvent += (_, e) => _guiderEvents.Enqueue(e);
        guider.Driver.GuidingErrorEvent +=  (_, e) => _guiderEvents.Enqueue(e);
        await guider.Driver.ConnectEquipmentAsync(cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return true;
    }

    internal async ValueTask ObservationLoopAsync(CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;
        var sessionStartTime = await GetMountUtcNowAsync(cancellationToken);
        var sessionEndTime = await SessionEndTimeAsync(sessionStartTime, cancellationToken);

        Observation? observation;
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

            External.AppLogger.LogInformation("Stop guiding to start slewing mount to target {Observation}.", observation);
            await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            double hourAngleAtSlewTime;
            try
            {
                (var postCondition, hourAngleAtSlewTime) = await mount.Driver.BeginSlewToTargetAsync(observation.Target, Configuration.MinHeightAboveHorizon, cancellationToken).ConfigureAwait(false);
                if (postCondition is SlewPostCondition.SlewNotPossible)
                {
                    _ = AdvanceObservation();
                    continue;
                }
                else if (postCondition is SlewPostCondition.TargetBelowHorizonLimit)
                {
                    // TODO: wait until target rises again instead of skipping
                    continue;
                }
                else if (postCondition is SlewPostCondition.Slewing)
                {
                    if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
                    {
                        External.AppLogger.LogError("Failed to complete slewing of mount {Mount}", mount);

                        throw new InvalidOperationException($"Failed to complete slewing of mount {mount} while slewing to {observation.Target}");
                    }

                    // TODO: Plate solve and re-slew
                }
                else
                {
                    throw new InvalidOperationException($"Unknown post condition {postCondition} after slewing to target {observation.Target}");
                }
            }
            catch (Exception ex)
            {
                External.AppLogger.LogError(ex, "Error while slewing to {Observation}, retrying", observation);

                // todo: if next observation is not yet risen, we need to wait and retry
                continue;
            }

            var guidingSuccess = await guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                External.AppLogger.LogWarning("Cancellation requested, abort setting up guider \"{GuiderName}\" and quit observation loop.", guider.Driver);
                break;
            }
            else if (!guidingSuccess)
            {
                External.AppLogger.LogError("Skipping target {Observation} as starting guider \"{GuiderName}\" failed after trying {GuiderTries} times.", observation, guider.Driver, Configuration.GuidingTries);
                
                // todo: if next observation is not yet risen, we need to wait and retry
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
    internal async ValueTask<ImageLoopNextAction> ImagingLoopAsync(Observation observation, double hourAngleAtSlewTime, CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;
        var scopes = Setup.Telescopes.Count;
        var frameNumbers = new int[scopes];
        var subExposuresSec = new int[scopes];

        for (var i = 0; i < scopes; i++)
        {
            var camera = Setup.Telescopes[i].Camera;

            camera.Driver.Target = observation.Target;

            // TODO per camera exposure calculation, e.g. via f/ratio
            var subExposure = observation.SubExposure;
            subExposuresSec[i] = (int)Math.Ceiling(subExposure.TotalSeconds);
        }

        var maxSubExposureSec = subExposuresSec.Max();
        var tickGCD = GCD(subExposuresSec);
        var tickLCM = LCM(tickGCD, subExposuresSec);
        var tickDuration = TimeSpan.FromSeconds(tickGCD);
        var ticksPerMaxSubExposure = maxSubExposureSec / tickGCD;
        var expStartTimes = new DateTimeOffset[scopes];
        var expTicks = new int[scopes];
        var ditherRound = 0;

        var overslept = TimeSpan.Zero;
        var imageWriteQueue = new Queue<QueuedImageWrite>();
        ImageLoopNextAction? next = null;

        while (!cancellationToken.IsCancellationRequested
            && mount.Driver.Connected
            && await CatchAsync(mount.Driver.IsTrackingAsync, cancellationToken)
        )
        {
            if (!await CatchAsync(guider.Driver.IsGuidingAsync, cancellationToken).ConfigureAwait(false))
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
                    // set denormalized parameters so that the image driver can write proper headers in the image file
                    camerDriver.FocusPosition = await CatchAsync(async ct => telescope.Focuser?.Driver is { Connected: true } focuserDriver ? await focuserDriver.GetPositionAsync(ct) : -1, cancellationToken, -1);
                    camerDriver.Filter = await CatchAsync(async ct => telescope.FilterWheel?.Driver is { Connected: true } filterWheelDriver ? (await filterWheelDriver.GetCurrentFilterAsync(ct)).Filter : Filter.Unknown, cancellationToken, Filter.Unknown);

                    var subExposureSec = subExposuresSec[i];
                    var frameExpTime = TimeSpan.FromSeconds(subExposureSec);
                    expStartTimes[i] = await camerDriver.StartExposureAsync(frameExpTime, cancellationToken: cancellationToken);
                    expTicks[i] = (int)(subExposureSec / tickGCD);
                    var frameNo = ++frameNumbers[i];

                    External.AppLogger.LogInformation("Camera #{CameraNumber} {CamerName} starting {ExposureStartTime} exposure of frame #{FrameNo}.",
                        i + 1, camerDriver.Name, frameExpTime, frameNo);
                }
            }

            var elapsed = await WriteQueuedImagesToFitsFilesAsync().ConfigureAwait(false);
            var tickMinusElapsed = tickDuration - elapsed - overslept;
            // clear overslept
            overslept = TimeSpan.Zero;
            if (cancellationToken.IsCancellationRequested)
            {
                External.AppLogger.LogWarning("Cancellation requested, all images in queue written to disk, abort image acquisition and quit imaging loop");
                next = ImageLoopNextAction.BreakObservationLoop;
                break;
            }
            else if (tickMinusElapsed > TimeSpan.Zero)
            {
                await External.SleepAsync(tickMinusElapsed, cancellationToken);
            }

            var imageFetchSuccess = new BitVector32(scopes);
            for (var i = 0; i < scopes && !cancellationToken.IsCancellationRequested; i++)
            {
                var tick = --expTicks[i];

                var camDriver = Setup.Telescopes[i].Camera.Driver;
                imageFetchSuccess[i] = false;
                if (tick <= 0)
                {
                    var frameExpTime = TimeSpan.FromSeconds(subExposuresSec[i]);
                    var frameNo = frameNumbers[i];
                    do // wait for image loop
                    {
                        if (await camDriver.GetImageAsync(cancellationToken) is { Width: > 0, Height: > 0 } image)
                        {
                            imageFetchSuccess[i] = true;
                            External.AppLogger.LogInformation("Camera #{CameraNumber} {CameraName} finished {ExposureStartTime} exposure of frame #{FrameNo}",
                                i + 1, camDriver.Name, frameExpTime, frameNo);

                            imageWriteQueue.Enqueue(new QueuedImageWrite(image, observation, expStartTimes[i], frameNo));
                            break;
                        }
                        else
                        {
                            var spinDuration = TimeSpan.FromMilliseconds(100);
                            overslept += spinDuration;

                            await External.SleepAsync(spinDuration, cancellationToken);
                        }
                    }
                    while (overslept < (tickDuration / 5)
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
            if (!await mount.Driver.IsOnSamePierSideAsync(hourAngleAtSlewTime, cancellationToken))
            {
                // write all images as the loop is ending here
                _ = await WriteQueuedImagesToFitsFilesAsync();

                // TODO stop exposures (if we can, and if there are any)

                if (observation.AcrossMeridian)
                {
                    // TODO, stop guiding flip, resync, verify and restart guiding
                    throw new NotSupportedException("Observing across meridian is not yet supported");
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
                        var currentMetrics = FrameMetrics.FromStarList(driftStars, observation.SubExposure, currentGain);

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
                            _ = await WriteQueuedImagesToFitsFilesAsync();

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
                    }
                }

                var shouldDither = (++ditherRound % Configuration.DitherEveryNthFrame) == 0;
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
                    External.AppLogger.LogDebug("Skipping dithering ({DitheringRound}/{DitherEveryNthFrame} frame)",
                        ditherRound % Configuration.DitherEveryNthFrame, Configuration.DitherEveryNthFrame);
                }
            }
        } // end imaging loop

        if (imageWriteQueue.TryPeek(out _))
        {
            // write all images as the loop is ending here
            _ = await WriteQueuedImagesToFitsFilesAsync();
        }

        return next ?? ImageLoopNextAction.AdvanceToNextObservation;

        async ValueTask<TimeSpan> WriteQueuedImagesToFitsFilesAsync()
        {
            var writeQueueStart = await GetMountUtcNowAsync(cancellationToken);
            while (imageWriteQueue.TryDequeue(out var imageWrite))
            {
                try
                {
                    await WriteImageToFitsFileAsync(imageWrite);
                }
                catch (Exception ex)
                {
                    External.AppLogger.LogError(ex, "Exception while saving frame #{FrameNumber} taken at {ExposureStartTime:o} by {Instrument}",
                        imageWrite.FrameNumber, imageWrite.ExpStartTime, imageWrite.Image.ImageMeta.Instrument);
                }
            }
            
            return await GetMountUtcNowAsync(cancellationToken) - writeQueueStart;
        }
    }

    /// <summary>
    /// Closes or opens telescope covers (if any). Also turns of a present calibrator when opening cover.
    /// </summary>
    /// <param name="finalCoverState">One of <see cref="CoverStatus.Open"/> or <see cref="CoverStatus.Closed"/></param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    internal async ValueTask<bool> MoveTelescopeCoversToStateAsync(CoverStatus finalCoverState, CancellationToken cancellationToken)
    {
        var scopes = Setup.Telescopes.Count;

        var finalCoverStateReached = new bool[scopes];
        var coversToWait = new List<int>();
        var shouldOpen = finalCoverState is CoverStatus.Open;

        for (var i = 0; i < scopes; i++)
        {
            if (Setup.Telescopes[i].Cover is { } cover)
            {
                await cover.Driver.ConnectAsync(cancellationToken).ConfigureAwait(false);

                bool calibratorActionCompleted;
                if (await cover.Driver.GetCoverStateAsync(cancellationToken) is CoverStatus.NotPresent)
                {
                    calibratorActionCompleted = true;
                    finalCoverStateReached[i] = true;
                }
                else if (finalCoverState is CoverStatus.Open)
                {
                    calibratorActionCompleted = await cover.Driver.TurnOffCalibratorAndWaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (finalCoverState is CoverStatus.Closed)
                {
                    calibratorActionCompleted = true;
                }
                else
                {
                    throw new ArgumentException($"Invalid final cover state {finalCoverState}, can only be open or closed", nameof(finalCoverState));
                }

                if (calibratorActionCompleted && !finalCoverStateReached[i])
                {
                    if (shouldOpen)
                    {
                        await cover.Driver.BeginOpen(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await cover.Driver.BeginClose(cancellationToken).ConfigureAwait(false);
                    }

                    coversToWait.Add(i);
                }
                else if (!calibratorActionCompleted)
                {
                    External.AppLogger.LogError("Failed to turn off calibrator of telescope {TelescopeNumber}, current state {CalibratorState}", i+1, await cover.Driver.GetCalibratorStateAsync(cancellationToken));
                }
            }
            else
            {
                finalCoverStateReached[i] = true;
            }
        }

        foreach (var i in coversToWait)
        {
            if (Setup.Telescopes[i].Cover is { } cover)
            {
                int failSafe = 0;
                CoverStatus cs;
                while ((finalCoverStateReached[i] = (cs = await cover.Driver.GetCoverStateAsync(cancellationToken)) == finalCoverState) is false
                    && cs is CoverStatus.Moving or CoverStatus.Unknown
                    && !cancellationToken.IsCancellationRequested
                    && ++failSafe < IDeviceDriver.MAX_FAILSAFE
                )
                {
                    External.AppLogger.LogInformation("Cover {Cover} of telescope {TelescopeNumber} is still {CurrentState} while reaching {FinalCoverState}, waiting.",
                        cover, i + 1, cs, finalCoverState);
                    await External.SleepAsync(TimeSpan.FromSeconds(3), cancellationToken);
                }

                var finalCoverStateAfterMoving = await cover.Driver.GetCoverStateAsync(cancellationToken);
                finalCoverStateReached[i] |= finalCoverStateAfterMoving == finalCoverState;

                if (!finalCoverStateReached[i])
                {
                    External.AppLogger.LogError("Failed to {CoverAction} cover of telescope {TelescopeNumber} after moving, current state {CurrentCoverState}",
                        shouldOpen ? "open" : "close",  i + 1, finalCoverStateAfterMoving);
                }
            }
        }

        return finalCoverStateReached.All(x => x);
    }

    /// <summary>
    /// Idea is that we keep cooler on but only on the currently reached temperature, so we have less cases to manage in the imaging loop.
    /// Assumes that power is switched on.
    /// </summary>
    /// <param name="rampTime">Interval between temperature checks</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal ValueTask<bool> CoolCamerasToSensorTempAsync(TimeSpan rampTime, CancellationToken cancellationToken)
        => CoolCamerasToSetpointAsync(new SetpointTemp(sbyte.MinValue, SetpointTempKind.CCD), rampTime, 0.1, SetupointDirection.Up, cancellationToken);


    /// <summary>
    /// Attention: Cannot be cancelled (as it would possibly destroy the cameras)
    /// </summary>
    /// <param name="rampTime">Interval between temperature checks</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal ValueTask<bool> CoolCamerasToAmbientAsync(TimeSpan rampTime)
        => CoolCamerasToSetpointAsync(new SetpointTemp(sbyte.MinValue, SetpointTempKind.Ambient), rampTime, 0.1, SetupointDirection.Up, CancellationToken.None);

    /// <summary>
    /// Assumes that power is on (c.f. <see cref="CoolCamerasToSensorTempAsync(TimeSpan, CancellationToken)"/>).
    /// </summary>
    /// <param name="desiredSetpointTemp">Desired degrees Celcius setpoint temperature,
    /// if <paramref name="desiredSetpointTemp"/>'s <see cref="SetpointTemp.Kind"/> is <see cref="SetpointTempKind.CCD" /> then sensor temperature is chosen,
    /// if its <see cref="SetpointTempKind.Normal" /> then the temp value is chosen
    /// or else ambient temperature is chosen (if available)</param>
    /// <param name="rampInterval">interval to wait until further adjusting setpoint.</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal async ValueTask<bool> CoolCamerasToSetpointAsync(
        SetpointTemp desiredSetpointTemp,
        TimeSpan rampInterval,
        double thresPower,
        SetupointDirection direction,
        CancellationToken cancellationToken
    )
    {
        var scopes = Setup.Telescopes.Count;
        var coolingStates = new CameraCoolingState[scopes];

        var accSleep = TimeSpan.Zero;
        do
        {
            for (var i = 0; i < Setup.Telescopes.Count; i++)
            {
                var camera = Setup.Telescopes[i].Camera;
                coolingStates[i] = await camera.Driver.CoolToSetpointAsync(desiredSetpointTemp, thresPower, direction, coolingStates[i], cancellationToken);
            }

            accSleep += rampInterval;
            if (cancellationToken.IsCancellationRequested)
            {
                External.AppLogger.LogWarning("Cancellation requested, quitting cooldown loop");
                break;
            }
            else
            {
                await External.SleepAsync(rampInterval, cancellationToken).ConfigureAwait(false);
            }
        } while (coolingStates.Any(state => state.IsRamping) && accSleep < rampInterval * 100 && !cancellationToken.IsCancellationRequested);

        return coolingStates.All(state => !(state.IsCoolable ?? false) || (state.TargetSetpointReached ?? false));
    }

    internal ValueTask WriteImageToFitsFileAsync(QueuedImageWrite imageWrite)
    {
        var targetName = imageWrite.Observation.Target.Name;
        var dateFolderUtc = imageWrite.ExpStartTime.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);

        // TODO: make configurable, add frame type
        var meta = imageWrite.Image.ImageMeta;
        var frameFolder = External.CreateSubDirectoryInOutputFolder(
            targetName,
            dateFolderUtc,
            meta.Filter.Name,
            meta.FrameType.ToString()
        ).FullName;
        var fitsFileName = External.GetSafeFileName($"frame_{imageWrite.ExpStartTime:o}_{imageWrite.FrameNumber:000000}.fits");
        var fitsFilePath = Path.Combine(frameFolder, fitsFileName);

        External.AppLogger.LogInformation("Writing FITS file {FitsFilePath}", fitsFilePath);
        return External.WriteFitsFileAsync(imageWrite.Image, fitsFilePath);
    }

    internal bool Catch(Action action) => External.Catch(action);

    internal T Catch<T>(Func<T> func, T @default = default) where T : struct => External.Catch(func, @default);
    internal ValueTask<bool> CatchAsync(Func<CancellationToken, ValueTask> asyncFunc, CancellationToken cancellationToken)
        => External.CatchAsync(asyncFunc, cancellationToken);
    internal Task<bool> CatchAsync(Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken)
        => External.CatchAsync(asyncFunc, cancellationToken);


    internal ValueTask<T> CatchAsync<T>(Func<CancellationToken, ValueTask<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
        => External.CatchAsync(asyncFunc, cancellationToken, @default);

    internal async ValueTask<DateTime> GetMountUtcNowAsync(CancellationToken cancellationToken)
        => await Setup.Mount.Driver.TryGetUTCDateFromMountAsync(cancellationToken) ?? External.TimeProvider.GetUtcNow().UtcDateTime;

    internal async ValueTask WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(CancellationToken cancellationToken)
    {
        if (await Setup.Mount.Driver.TryGetTransformAsync(cancellationToken) is not { } transform)
        {
            throw new InvalidOperationException("Failed to retrieve time transformation from mount");
        }

        var (_, _, set) = transform.EventTimes(Astrometry.SOFA.EventType.AmateurAstronomicalTwilight);
        if (set is { Count: 1 })
        {
            var now = External.TimeProvider.GetUtcNow().UtcDateTime;
            var localNow = new DateTimeOffset(now, transform.SiteTimeZone);
            var utcDayStart = now - now.TimeOfDay;
            var localAstroTwilightSet = new DateTimeOffset(utcDayStart, transform.SiteTimeZone) + set[0];
            var local10MinBeforeAstroTwilightSet = localAstroTwilightSet - TimeSpan.FromMinutes(10);
            var diff = local10MinBeforeAstroTwilightSet - now;

            if (diff > TimeSpan.Zero)
            {
                External.AppLogger.LogInformation("Current time {CurrentTimeLocal}, twilight ends {AmateurTwilightEndsLocal}, which is in {Diff}",
                    localNow, localAstroTwilightSet, diff);
                await External.SleepAsync(diff, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                External.AppLogger.LogWarning("Current time {CurrentTimeLocal}, twilight ends {AmateurTwilightEndsLocal}, ended {Diff} ago",
                    localNow, localAstroTwilightSet, -diff);
            }
        }
        else
        {
            throw new InvalidOperationException($"Failed to retrieve astro event time for {transform.DateTime}");
        }
    }

    internal async ValueTask<DateTime> SessionEndTimeAsync(DateTime startTime, CancellationToken cancellationToken)
    {
        if (await Setup.Mount.Driver.TryGetTransformAsync(cancellationToken) is not { } transform)
        {
            throw new InvalidOperationException("Failed to retrieve time transformation from mount");
        }

        // advance one day
        var nowPlusOneDay = transform.DateTime = startTime.AddDays(1);
        var (_, rise, _) = transform.EventTimes(Astrometry.SOFA.EventType.AstronomicalTwilight);

        if (rise is { Count: 1 })
        {
            var tomorrowStartOfDay = nowPlusOneDay - nowPlusOneDay.TimeOfDay;
            return (new DateTimeOffset(tomorrowStartOfDay, transform.SiteTimeZone) + rise[0]).UtcDateTime;
        }
        else
        {
            throw new InvalidOperationException($"Failed to retrieve astro event time for {transform.DateTime}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Setup.DisposeAsync();

        GC.SuppressFinalize(this);
    }
}