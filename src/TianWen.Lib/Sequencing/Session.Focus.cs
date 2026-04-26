using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    /// <summary>
    /// Returns the per-direction backlash to use for the next BacklashCompensation move.
    /// Prefers the per-focuser EWMA (sized by <see cref="BACKLASH_OVERSHOOT_SAFETY"/>) once
    /// we have at least one inferred sample; otherwise falls back to whatever the driver
    /// reports (URI seed). Negative driver values fall through to a generous default
    /// derived from AutoFocusRange so day-one moves still overshoot enough.
    /// </summary>
    private (int In, int Out) GetEffectiveBacklash(IFocuserDriver focuser)
    {
        // Default seed if neither EWMA nor URI provides a usable value: 2 V-curve step-sizes,
        // generous enough to mask typical SCT mirror flop on the first run.
        var defaultSeed = Math.Max(20, 2 * Configuration.AutoFocusRange / Math.Max(2, Configuration.AutoFocusStepCount - 1));

        var hasEwma = _focuserBacklashEstimates.TryGetValue(focuser, out var ewma);

        int Apply(int driverValue, int ewmaValue)
        {
            if (hasEwma && ewmaValue > 0)
            {
                return (int)Math.Round(ewmaValue * BACKLASH_OVERSHOOT_SAFETY);
            }
            return driverValue >= 0 ? driverValue : defaultSeed;
        }

        return (Apply(focuser.BacklashStepsIn, ewma?.EwmaIn ?? 0),
                Apply(focuser.BacklashStepsOut, ewma?.EwmaOut ?? 0));
    }

    /// <summary>
    /// Loads the persisted backlash EWMA for <paramref name="focuser"/> from the sidecar
    /// JSON if we haven't already this session. Idempotent and cheap on the hot path —
    /// guarded by <see cref="_focuserBacklashLoaded"/>.
    /// </summary>
    private async ValueTask LoadBacklashHistoryIfNeededAsync(IFocuserDriver focuser, DeviceBase focuserDevice, CancellationToken cancellationToken)
    {
        if (!_focuserBacklashLoaded.TryAdd(focuser, true))
        {
            return;
        }

        var loaded = await BacklashHistoryPersistence.TryLoadAsync(External, focuserDevice.DeviceId, cancellationToken);
        if (loaded is not null)
        {
            _focuserBacklashEstimates[focuser] = loaded;
            _logger.LogInformation(
                "Backlash history loaded for focuser {FocuserId}: ewma in={EwmaIn} out={EwmaOut}, samples={Samples}, last updated {LastUpdated:O}.",
                focuserDevice.DeviceId, loaded.EwmaIn, loaded.EwmaOut, loaded.Samples, loaded.LastUpdatedUtc);
        }
    }

    /// <summary>
    /// Returns the overshoot magnitude (one direction) that <see cref="BacklashCompensation.MoveWithCompensationAsync"/>
    /// would apply for a move from <paramref name="currentPos"/> to <paramref name="targetPos"/>.
    /// Returns 0 when the move is in the preferred direction (no overshoot performed).
    /// </summary>
    private static int ComputeOvershootForMove(int currentPos, int targetPos, int backlashIn, int backlashOut, FocusDirection focusDir)
    {
        if (targetPos == currentPos)
        {
            return 0;
        }
        var movingPositive = targetPos > currentPos;
        var approachingFromPreferred = movingPositive == focusDir.PreferredDirectionIsPositive;
        if (approachingFromPreferred)
        {
            return 0;
        }
        return movingPositive ? backlashOut : backlashIn;
    }

    /// <summary>
    /// Runs <see cref="BacklashEstimator.InferFromVerification"/> against the verification
    /// exposure HFD and folds the result into the per-focuser EWMA. Skips low-confidence
    /// samples (HFD too close to predicted minimum) so a clean focus run doesn't drift the
    /// estimate. Persists the updated EWMA to the sidecar JSON so the next session bootstraps
    /// from the same value.
    /// </summary>
    private async ValueTask UpdateBacklashEstimateFromVerificationAsync(
        IFocuserDriver focuser,
        DeviceBase focuserDevice,
        FocusSolution solution,
        int bestPos,
        int currentPosBeforeMove,
        double verifyHfd,
        int overshootUsed,
        FocusDirection focusDir,
        int telescopeIndex,
        CancellationToken cancellationToken)
    {
        // Minimum confidence below which we treat the inference as noise and skip the EWMA update.
        const float MinConfidence = 0.3f;

        var (bInferred, confidence, mechanicalPos) = BacklashEstimator.InferFromVerification(
            solution, bestPos, verifyHfd, overshootUsed, focusDir);

        if (bInferred is null)
        {
            // Either no overshoot was performed (final move was direct) or verification was at the
            // hyperbola minimum (overshoot was sufficient — no usable signal but a healthy outcome).
            return;
        }

        if (confidence < MinConfidence)
        {
            _logger.LogDebug("Auto-focus telescope #{TelescopeNumber}: backlash inference {B} steps with low confidence {Confidence:F2} — skipping EWMA update.",
                telescopeIndex + 1, bInferred.Value, confidence);
            return;
        }

        // Determine which direction this measurement applies to. The overshoot only ran on
        // the non-preferred-direction move; whether that move was "in" or "out" is what
        // tells us which of the two backlash values we just measured. The other direction's
        // EWMA is preserved.
        var movingPositive = bestPos > currentPosBeforeMove;
        var measuredIn = !movingPositive;

        var prior = _focuserBacklashEstimates.GetValueOrDefault(focuser);
        var samples = prior?.Samples ?? 0;

        var updatedIn = measuredIn
            ? BacklashEstimator.UpdateEwma(prior?.EwmaIn ?? 0, bInferred.Value, samples)
            : prior?.EwmaIn ?? 0;
        var updatedOut = !measuredIn
            ? BacklashEstimator.UpdateEwma(prior?.EwmaOut ?? 0, bInferred.Value, samples)
            : prior?.EwmaOut ?? 0;

        var record = new BacklashEstimateRecord(
            EwmaIn: updatedIn,
            EwmaOut: updatedOut,
            Samples: samples + 1,
            LastUpdatedUtc: _timeProvider.GetUtcNow());

        _focuserBacklashEstimates[focuser] = record;

        _logger.LogInformation(
            "Auto-focus telescope #{TelescopeNumber}: backlash inferred={Inferred} steps {Direction} (mechanical pos={Mech:F0}, overshoot used={Overshoot}, confidence={Confidence:F2}); EWMA in={EwmaIn} out={EwmaOut} after {Samples} samples; next overshoot in={NextIn} out={NextOut}.",
            telescopeIndex + 1, bInferred.Value, measuredIn ? "in" : "out", mechanicalPos, overshootUsed, confidence,
            updatedIn, updatedOut, record.Samples,
            (int)Math.Round(updatedIn * BACKLASH_OVERSHOOT_SAFETY),
            (int)Math.Round(updatedOut * BACKLASH_OVERSHOOT_SAFETY));

        // Persist async — don't fail the focus run if the disk write blows up.
        try
        {
            await BacklashHistoryPersistence.SaveAsync(External, focuserDevice.DeviceId, record, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist backlash history for focuser {FocuserId}; in-memory EWMA still updated.", focuserDevice.DeviceId);
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
            _logger.LogError("Failed to enable tracking of {Mount}.", mount);

            return false;
        }

        _logger.LogInformation("Slew mount {Mount} near zenith to verify that we have rough focus.", mount);

        // coordinates not quite accurate at this point (we have not plate-solved yet) but good enough for this purpose.
        await mount.Driver.BeginSlewToZenithAsync(distMeridian, cancellationToken).ConfigureAwait(false);
        var slewTime = await GetMountUtcNowAsync(cancellationToken);

        _currentActivity = "Waiting for slew to complete\u2026";
        _logger.LogInformation("RoughFocus: waiting for slew to complete...");
        if (!await ResilientInvokeAsync(
                mount.Driver,
                ct => mount.Driver.WaitForSlewCompleteAsync(PollDeviceStatesAsync, ct),
                ResilientCallOptions.IdempotentRead, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogError("Failed to complete slewing of mount {Mount}", mount);

            return false;
        }

        // Update camera targets with current mount position for FITS headers and synthetic star rendering
        var zenithRa = await ResilientInvokeAsync(
            mount.Driver, mount.Driver.GetRightAscensionAsync,
            ResilientCallOptions.IdempotentRead, cancellationToken);
        var zenithDec = await ResilientInvokeAsync(
            mount.Driver, mount.Driver.GetDeclinationAsync,
            ResilientCallOptions.IdempotentRead, cancellationToken);
        var zenithTarget = new Target(zenithRa, zenithDec, "Zenith", null);
        for (var i = 0; i < Setup.Telescopes.Length; i++)
        {
            var cam = Setup.Telescopes[i].Camera.Driver;
            cam.Target = zenithTarget;

            // Sync camera's FocusPosition from the focuser so defocus-dependent rendering is correct
            if (Setup.Telescopes[i].Focuser?.Driver is { Connected: true } foc)
            {
                cam.FocusPosition = await ResilientInvokeAsync(
                    foc, foc.GetPositionAsync,
                    ResilientCallOptions.IdempotentRead, cancellationToken);
            }
        }

        _currentActivity = "Guider plate-solve (60s timeout)\u2026";
        _logger.LogInformation("RoughFocus: slew complete, starting guider plate-solve loop (1 min timeout)...");
        if (!await GuiderFocusLoopAsync(TimeSpan.FromMinutes(1), cancellationToken))
        {
            _logger.LogWarning("RoughFocus: guider focus loop timed out or failed, continuing with rough focus detection.");
        }

        var count = Setup.Telescopes.Length;

        // Ensure _lastCapturedImages is sized (normally done by InitialisationAsync)
        if (_lastCapturedImages.Length < count)
        {
            _lastCapturedImages = new Image?[count];
        }

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

                await PollDeviceStatesAsync(cancellationToken);

                if (await camDriver.GetImageAsync(cancellationToken) is { Width: > 0, Height: > 0 } image)
                {
                    _lastCapturedImages[i] = image;

                    var stars = await image.FindStarsAsync(0, snrMin: 15, cancellationToken: cancellationToken);

                    _currentActivity = $"Stars: {stars.Count}/15 (exposure {expTimesSec[i]}s)";
                    _logger.LogInformation("RoughFocus: telescope #{TelescopeNumber} exposure {ExpTime}s focPos={FocusPosition} → {StarCount} stars detected (need ≥15)",
                        i + 1, expTimesSec[i], camDriver.FocusPosition, stars.Count);

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

                if (!await ResilientInvokeAsync(
                        mount.Driver,
                        ct => mount.Driver.WaitForSlewCompleteAsync(PollDeviceStatesAsync, ct),
                        ResilientCallOptions.IdempotentRead, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogError("Failed to complete slewing of mount {Mount}", mount);

                    return false;
                }
            }

            if (hasRoughFocus.All(v => v))
            {
                return true;
            }

            await _timeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
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

            _logger.LogInformation(
                "Established baseline for telescope #{TelescopeNumber} on observation #{ObservationIndex}: HFD={BaselineHFD:F2}, FWHM={BaselineFWHM:F2}, stars={StarCount} (from {FrameCount} frames).",
                telescopeIndex + 1, obsIndex + 1, medianMetrics.MedianHfd, medianMetrics.MedianFwhm, medianMetrics.StarCount, Configuration.BaselineHfdFrameCount);
        }
    }

    private void AppendFocusRunRecord(
        int telescopeIndex, OTA telescope, IFilterWheelDriver? filterWheelDriver,
        int filterPosition, int bestPos, float bestHfd, MetricSampleMap sampleMap,
        double fitA = double.NaN, double fitB = double.NaN)
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
            Timestamp: _timeProvider.GetUtcNow(),
            OtaName: telescope.Name,
            FilterName: filterName,
            BestPosition: bestPos,
            BestHfd: bestHfd,
            Curve: curveBuilder.ToImmutable(),
            FitA: fitA,
            FitB: fitB));
        _activeFocusSamples = [];
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
            _logger.LogWarning("Telescope #{TelescopeNumber} has no connected focuser, skipping auto-focus.", telescopeIndex + 1);
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

        // Bootstrap per-focuser EWMA from disk on first encounter so the very first move
        // of the session uses last-night's overshoot estimate, not the URI seed.
        await LoadBacklashHistoryIfNeededAsync(focuser, telescope.Focuser!.Device, cancellationToken);

        var currentPos = await ResilientInvokeAsync(
            focuser, focuser.GetPositionAsync,
            ResilientCallOptions.IdempotentRead, cancellationToken);
        var range = Configuration.AutoFocusRange;
        var stepCount = Configuration.AutoFocusStepCount;
        var stepSize = range / (stepCount - 1);
        var startPos = Math.Max(0, currentPos - range / 2);

        _logger.LogInformation("Auto-focus telescope #{TelescopeNumber}: scanning {StepCount} positions from {StartPos} with step size {StepSize}.",
            telescopeIndex + 1, stepCount, startPos, stepSize);

        var sampleMap = new MetricSampleMap(SampleKind.HFD, AggregationMethod.Median);
        _activeFocusSamples = [];

        // Move to start position with backlash compensation
        var focusDir = telescope.FocusDirection;
        var (backlashIn, backlashOut) = GetEffectiveBacklash(focuser);
        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, startPos, currentPos, backlashIn, backlashOut, focusDir, _timeProvider, cancellationToken);

        // Scan from start to end (always moving outward — no backlash needed)
        for (var i = 0; i < stepCount && !cancellationToken.IsCancellationRequested; i++)
        {
            var targetPos = startPos + i * stepSize;
            _currentActivity = $"#{telescopeIndex + 1} V-curve {i + 1}/{stepCount} pos={targetPos}";
            // Move may have been started during previous iteration's download overlap
            if (!await focuser.GetIsMovingAsync(cancellationToken))
            {
                await ResilientInvokeAsync(
                    focuser,
                    ct => focuser.BeginMoveAsync(targetPos, ct),
                    ResilientCallOptions.AbsoluteMove, cancellationToken);
            }
            while (await focuser.GetIsMovingAsync(cancellationToken) && !cancellationToken.IsCancellationRequested)
            {
                await PollDeviceStatesAsync(cancellationToken);
                await _timeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            camera.FocusPosition = targetPos;
            await PollDeviceStatesAsync(cancellationToken);

            // Update camera state for the UI
            if (telescopeIndex < _cameraStates.Length)
            {
                var focTemp = await CatchAsync(focuser.GetTemperatureAsync, cancellationToken, double.NaN);
                _cameraStates[telescopeIndex] = new CameraExposureState(
                    telescopeIndex, _timeProvider.GetUtcNow(), autoFocusExposure,
                    i + 1, $"Focus {i + 1}/{stepCount}", targetPos, Devices.CameraState.Exposing,
                    focTemp, FocuserIsMoving: false);
            }

            await ResilientInvokeAsync(
                camera,
                ct => camera.StartExposureAsync(autoFocusExposure, cancellationToken: ct),
                ResilientCallOptions.NonIdempotentAction, cancellationToken);

            // Pipeline optimization: start moving focuser to next position while camera downloads
            var nextPos = (i + 1 < stepCount) ? startPos + (i + 1) * stepSize : -1;
            Image? image = null;
            var retries = 0;
            var moveStarted = false;
            while (image is null && retries++ < 100 && !cancellationToken.IsCancellationRequested)
            {
                image = await ResilientInvokeAsync(
                    camera, camera.GetImageAsync,
                    ResilientCallOptions.IdempotentRead, cancellationToken);
                if (image is null)
                {
                    // Start moving to next position during download (overlap)
                    if (!moveStarted && nextPos >= 0 && retries > 5)
                    {
                        await ResilientInvokeAsync(
                            focuser,
                            ct => focuser.BeginMoveAsync(nextPos, ct),
                            ResilientCallOptions.AbsoluteMove, cancellationToken);
                        moveStarted = true;
                    }
                    await _timeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
                }
            }

            if (image is { Width: > 0, Height: > 0 })
            {
                // Update state: downloading / processing
                if (telescopeIndex < _cameraStates.Length)
                {
                    _cameraStates[telescopeIndex] = _cameraStates[telescopeIndex] with { State = Devices.CameraState.Download };
                }
                // Push raw image to mini viewer (GPU handles debayer)
                if (telescopeIndex < _lastCapturedImages.Length)
                {
                    _lastCapturedImages[telescopeIndex] = image;
                }

                // Star detection on the raw image
                var stars = await image.FindStarsAsync(0, snrMin: 10, cancellationToken: cancellationToken);
                if (stars.Count > 3)
                {
                    var hfd = stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
                    sampleMap.AddSampleAtFocusPosition(targetPos, hfd);
                    _activeFocusSamples = _activeFocusSamples.Add((targetPos, hfd));
                    _logger.LogInformation("Auto-focus pos={Position} stars={StarCount} HFD={HFD:F2}", targetPos, stars.Count, hfd);
                }
                else
                {
                    _logger.LogInformation("Auto-focus pos={Position} too few stars ({StarCount})", targetPos, stars.Count);
                }

                _logger.LogInformation(
                    "Memory after AF pos={Position}: working={WorkingMB:F0}MB, managed={ManagedMB:F0}MB | pool: {Pooled} pooled, {Hits}h/{Misses}m/{Returns}r, {FinalizerReturns} finalizer",
                    targetPos,
                    Environment.WorkingSet / (1024.0 * 1024),
                    GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024),
                    Array2DPool<float>.TotalPooled,
                    Array2DPool<float>.HitCount,
                    Array2DPool<float>.MissCount,
                    Array2DPool<float>.ReturnCount,
                    0);

                // Release raw image's ChannelBuffer after star detection
                {
                    image.Release();
                    image = null;
                }
            }
        }

        // Return last captured viewer image and reclaim V-curve intermediates
        if (telescopeIndex < _lastCapturedImages.Length)
        {

            _lastCapturedImages[telescopeIndex] = null;
        }
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
        GC.WaitForPendingFinalizers();
        _logger.LogInformation(
            "Memory after AF cleanup: working={WorkingMB:F0}MB, managed={ManagedMB:F0}MB | pool: {Pooled} pooled, {FinalizerReturns} finalizer",
            Environment.WorkingSet / (1024.0 * 1024),
            GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024),
            Array2DPool<float>.TotalPooled,
            0);

        // Reset camera state after V-curve loop
        if (telescopeIndex < _cameraStates.Length)
        {
            _cameraStates[telescopeIndex] = default;
        }

        // Fit hyperbola
        _currentActivity = $"#{telescopeIndex + 1} Fitting hyperbola\u2026";
        if (sampleMap.TryGetBestFocusSolution(out var solution, out _, out _))
        {
            var bestPos = Math.Clamp((int)Math.Round(solution.Value.BestFocus), 0, focuser.MaxStep);
            var currentPosNow = await ResilientInvokeAsync(
                focuser, focuser.GetPositionAsync,
                ResilientCallOptions.IdempotentRead, cancellationToken);

            _currentActivity = $"#{telescopeIndex + 1} Moving to best focus ({bestPos})";
            _logger.LogInformation("Auto-focus telescope #{TelescopeNumber}: best focus at position {BestFocus} (A={A:F2}, B={B:F2}, error={Error:F4}).",
                telescopeIndex + 1, bestPos, solution.Value.A, solution.Value.B, solution.Value.Error);

            var (effBacklashIn, effBacklashOut) = GetEffectiveBacklash(focuser);
            var overshootUsed = ComputeOvershootForMove(currentPosNow, bestPos, effBacklashIn, effBacklashOut, focusDir);
            await BacklashCompensation.MoveWithCompensationAsync(
                focuser, bestPos, currentPosNow, effBacklashIn, effBacklashOut, focusDir, _timeProvider, cancellationToken);

            // Take a verification exposure at best focus to get baseline HFD
            camera.FocusPosition = bestPos;
            await ResilientInvokeAsync(
                camera,
                ct => camera.StartExposureAsync(TimeSpan.FromSeconds(2), cancellationToken: ct),
                ResilientCallOptions.NonIdempotentAction, cancellationToken);

            Image? verifyImage = null;
            var retries = 0;
            while (verifyImage is null && retries++ < 100 && !cancellationToken.IsCancellationRequested)
            {
                verifyImage = await ResilientInvokeAsync(
                    camera, camera.GetImageAsync,
                    ResilientCallOptions.IdempotentRead, cancellationToken);
                if (verifyImage is null)
                {
                    await _timeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
                }
            }

            if (verifyImage is { Width: > 0, Height: > 0 })
            {
                var verifyStars = await verifyImage.FindStarsAsync(0, snrMin: 10, cancellationToken: cancellationToken);
                verifyImage.Release();
                if (verifyStars.Count > 3)
                {
                    var baseline = FrameMetrics.FromStarList(verifyStars, autoFocusExposure, currentGain);
                    var expectedHfd = solution.Value.A;
                    var hfdRatio = baseline.MedianHfd / expectedHfd;

                    _logger.LogInformation("Auto-focus telescope #{TelescopeNumber}: baseline HFD={BaselineHFD:F2} (expected={Expected:F2}, ratio={Ratio:F2}), FWHM={BaselineFWHM:F2}, stars={StarCount}.",
                        telescopeIndex + 1, baseline.MedianHfd, expectedHfd, hfdRatio, baseline.MedianFwhm, baseline.StarCount);

                    if (hfdRatio > 1.5)
                    {
                        _logger.LogWarning("Auto-focus telescope #{TelescopeNumber}: verification HFD is {Ratio:F0}% worse than expected, focus result may be unreliable.",
                            telescopeIndex + 1, (hfdRatio - 1) * 100);
                    }

                    await UpdateBacklashEstimateFromVerificationAsync(
                        focuser, telescope.Focuser!.Device, solution.Value,
                        bestPos, currentPosNow, baseline.MedianHfd, overshootUsed, focusDir, telescopeIndex,
                        cancellationToken);

                    AppendFocusRunRecord(telescopeIndex, telescope, filterWheelDriver, preFocusFilterPosition, bestPos, baseline.MedianHfd, sampleMap, solution.Value.A, solution.Value.B);
                    return (true, baseline);
                }
            }
            else
            {
                verifyImage?.Release();
            }

            // Fit converged but we couldn't measure baseline — use the hyperbola minimum as HFD estimate
            AppendFocusRunRecord(telescopeIndex, telescope, filterWheelDriver, preFocusFilterPosition, bestPos, (float)solution.Value.A, sampleMap, solution.Value.A, solution.Value.B);
            return (true, new FrameMetrics(0, (float)solution.Value.A, float.NaN, autoFocusExposure, currentGain));
        }

        _logger.LogWarning("Auto-focus telescope #{TelescopeNumber}: hyperbola fit did not converge.", telescopeIndex + 1);
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
        => (await PlateSolveAndSyncCoreAsync(telescopeIndex, exposureTime, PlateSolveContext.MountSync, cancellationToken)).Solved;

    internal async ValueTask<(bool Solved, double RaJ2000, double DecJ2000)> PlateSolveAndSyncCoreAsync(int telescopeIndex, TimeSpan exposureTime, PlateSolveContext context, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var telescope = Setup.Telescopes[telescopeIndex];
        var camDriver = telescope.Camera.Driver;

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
            await _timeProvider.SleepAsync(spinDuration, cancellationToken);
        }

        if (image is null)
        {
            _logger.LogWarning("Plate solve: failed to capture image from camera #{CameraNumber}.", telescopeIndex + 1);
            RecordPlateSolve(context, telescope.Name, succeeded: false, solution: null, elapsed: TimeSpan.Zero);
            return (false, double.NaN, double.NaN);
        }

        // Plate solve using mount's current position as search origin
        var mountRa = await ResilientInvokeAsync(
            mount.Driver, mount.Driver.GetRightAscensionAsync,
            ResilientCallOptions.IdempotentRead, cancellationToken);
        var mountDec = await ResilientInvokeAsync(
            mount.Driver, mount.Driver.GetDeclinationAsync,
            ResilientCallOptions.IdempotentRead, cancellationToken);
        var searchOrigin = new WCS(mountRa, mountDec);

        var result = await PlateSolver.SolveImageAsync(image, searchOrigin: searchOrigin, searchRadius: 10, cancellationToken: cancellationToken);
        image.Release();

        if (result.Solution is not { } wcs)
        {
            _logger.LogWarning("Plate solve: failed to solve image from camera #{CameraNumber}.", telescopeIndex + 1);
            RecordPlateSolve(context, telescope.Name, succeeded: false, solution: null, result);
            return (false, double.NaN, double.NaN);
        }

        _logger.LogInformation(
            "Plate solve: solved at ({SolvedRA}, {SolvedDec}), mount was at ({MountRA}, {MountDec}), offset=({DeltaRA:F4}h, {DeltaDec:F2}°).",
            Astrometry.CoordinateUtils.HoursToHMS(wcs.CenterRA), Astrometry.CoordinateUtils.DegreesToDMS(wcs.CenterDec),
            Astrometry.CoordinateUtils.HoursToHMS(mountRa), Astrometry.CoordinateUtils.DegreesToDMS(mountDec),
            wcs.CenterRA - mountRa, wcs.CenterDec - mountDec);

        // Sync mount to solved J2000 coordinates
        await mount.Driver.SyncRaDecJ2000Async(wcs.CenterRA, wcs.CenterDec, cancellationToken);

        _logger.LogInformation("Plate solve: mount synced to solved position.");
        RecordPlateSolve(context, telescope.Name, succeeded: true, solution: wcs, result);
        return (true, wcs.CenterRA, wcs.CenterDec);
    }

    /// <summary>
    /// Iterative plate-solve + sync + reslew centering loop.
    /// Converges on the target until the offset is below <paramref name="thresholdArcmin"/>
    /// or <paramref name="maxAttempts"/> is reached.
    /// </summary>
    internal async ValueTask<bool> CenterOnTargetAsync(Target target, int telescopeIndex, double thresholdArcmin = 1.0, int maxAttempts = 5, CancellationToken cancellationToken = default)
    {
        var mount = Setup.Mount;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _currentActivity = $"Centering {target.Name} (attempt {attempt}/{maxAttempts})\u2026";

            var (solved, solvedRa, solvedDec) = await PlateSolveAndSyncCoreAsync(telescopeIndex, TimeSpan.FromSeconds(5), PlateSolveContext.Centering, cancellationToken);
            if (!solved)
            {
                _logger.LogWarning("Centering: plate solve failed on attempt {Attempt}/{Max}", attempt, maxAttempts);
                continue;
            }

            // Compare plate solve result (J2000) directly against target (J2000) — no coordinate system mismatch
            var cosDec = Math.Cos(double.DegreesToRadians(target.Dec));
            var deltaRaArcmin = Math.Abs(solvedRa - target.RA) * 15.0 * cosDec * 60.0;
            var deltaDecArcmin = Math.Abs(solvedDec - target.Dec) * 60.0;
            var totalOffsetArcmin = Math.Sqrt(deltaRaArcmin * deltaRaArcmin + deltaDecArcmin * deltaDecArcmin);

            _logger.LogInformation("Centering: offset={Offset:F2}' (RA={DeltaRA:F2}' Dec={DeltaDec:F2}') attempt {Attempt}/{Max}",
                totalOffsetArcmin, deltaRaArcmin, deltaDecArcmin, attempt, maxAttempts);

            if (totalOffsetArcmin <= thresholdArcmin)
            {
                _logger.LogInformation("Centering: converged within {Threshold}' after {Attempt} attempt(s)", thresholdArcmin, attempt);
                return true;
            }

            // Re-slew to the target (mount model is now corrected by sync)
            _currentActivity = $"Re-slewing to {target.Name}\u2026";
            var (postCondition, _) = await mount.Driver.BeginSlewToTargetAsync(target, Configuration.MinHeightAboveHorizon, cancellationToken).ConfigureAwait(false);
            if (postCondition is SlewPostCondition.Slewing)
            {
                await ResilientInvokeAsync(
                    mount.Driver,
                    ct => mount.Driver.WaitForSlewCompleteAsync(PollDeviceStatesAsync, ct),
                    ResilientCallOptions.IdempotentRead, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogWarning("Centering: did not converge within {Max} attempts for {Target}", maxAttempts, target.Name);
        return false;
    }

    internal async ValueTask<bool> GuiderFocusLoopAsync(TimeSpan timeoutAfter, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var guider = Setup.Guider;

        var plateSolveTimeout = timeoutAfter > TimeSpan.FromSeconds(5) ? timeoutAfter - TimeSpan.FromSeconds(3) : timeoutAfter;

        Astrometry.PlateSolve.PlateSolveResult result;
        try
        {
            var mountRa = await ResilientInvokeAsync(
                mount.Driver, mount.Driver.GetRightAscensionAsync,
                ResilientCallOptions.IdempotentRead, cancellationToken);
            var mountDec = await ResilientInvokeAsync(
                mount.Driver, mount.Driver.GetDeclinationAsync,
                ResilientCallOptions.IdempotentRead, cancellationToken);
            result = await guider.Driver.PlateSolveGuiderImageAsync(PlateSolver,
                mountRa,
                mountDec,
                plateSolveTimeout,
                10d,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Guider plate-solve failed");
            RecordPlateSolve(PlateSolveContext.GuiderFocus, guider.Device.DisplayName, succeeded: false, solution: null, elapsed: TimeSpan.Zero);
            return false;
        }

        var guiderName = guider.Device.DisplayName;

        if (result.Solution is { } wcs)
        {
            _logger.LogInformation("Guider \"{GuiderName}\" is in focus and camera image plate solve succeeded with ({SolvedRa}, {SolvedDec})",
                guiderName, wcs.CenterRA, wcs.CenterDec);
            RecordPlateSolve(PlateSolveContext.GuiderFocus, guiderName, succeeded: true, solution: wcs, result);
            return true;
        }
        else
        {
            _logger.LogWarning("Failed to plate solve guider \"{GuiderName}\" without a specific reason (probably not enough stars detected)",
                guiderName);
            RecordPlateSolve(PlateSolveContext.GuiderFocus, guiderName, succeeded: false, solution: null, result);
        }

        return false;
    }

    private void RecordPlateSolve(PlateSolveContext context, string otaName, bool succeeded, WCS? solution, Astrometry.PlateSolve.PlateSolveResult result)
    {
        RecordPlateSolve(context, otaName, succeeded, solution, result.Elapsed, result.DetectedStars, result.MatchedStars);
    }

    private void RecordPlateSolve(PlateSolveContext context, string otaName, bool succeeded, WCS? solution, TimeSpan elapsed, int detectedStars = 0, int matchedStars = 0)
    {
        var record = new PlateSolveRecord(
            Timestamp: _timeProvider.GetUtcNow(),
            Context: context,
            OtaName: otaName,
            Succeeded: succeeded,
            Solution: solution,
            Elapsed: elapsed,
            DetectedStars: detectedStars,
            MatchedStars: matchedStars);

        _plateSolveHistory.Enqueue(record);
        PlateSolveCompleted?.Invoke(this, new PlateSolveCompletedEventArgs(record));
    }
}
