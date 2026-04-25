using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// FOV obstruction detection — predictive scout + altitude-nudge disambiguation.
/// Runs after <c>CenterOnTargetAsync</c> and before guider/imaging commitment so a
/// target behind a tree or roof is detected fast and either waited out (if the trajectory
/// will clear it soon) or skipped cleanly. See <c>PLAN-fov-obstruction-detection.md</c>.
/// </summary>
internal partial record Session
{
    /// <summary>
    /// Loop-side wrapper around <see cref="ScoutAndProbeAsync"/>. Runs the scout, applies the
    /// trajectory wait policy when an obstruction is detected, and returns the routing decision
    /// for <c>ObservationLoopAsync</c>.
    /// <list type="bullet">
    /// <item><see cref="ScoutClassification.Healthy"/> → <see cref="ScoutOutcome.Proceed"/>.</item>
    /// <item><see cref="ScoutClassification.Transparency"/> → <see cref="ScoutOutcome.Proceed"/>;
    /// the in-flight deterioration check in the imaging loop will re-detect it once the per-target
    /// baseline exists and route through <c>WaitForConditionRecoveryAsync</c>.</item>
    /// <item><see cref="ScoutClassification.Obstruction"/> with clear time within the
    /// <see cref="SessionConfiguration.ObstructionClearFractionOfRemaining"/> window → sleep, re-scout,
    /// proceed if healthy, otherwise advance.</item>
    /// <item><see cref="ScoutClassification.Obstruction"/> with no usable clear time →
    /// <see cref="ScoutOutcome.Advance"/>.</item>
    /// </list>
    /// </summary>
    internal async ValueTask<ScoutOutcome?> RunObstructionScoutAsync(
        ScheduledObservation observation, CancellationToken cancellationToken)
    {
        var scoutResult = await ScoutAndProbeAsync(observation, cancellationToken);

        switch (scoutResult.Classification)
        {
            case ScoutClassification.Healthy:
                return ScoutOutcome.Proceed;

            case ScoutClassification.Transparency:
                // Hand off to the existing recovery flow inside the imaging loop. The scout
                // already moved the entry point earlier (before guider start), but the actual
                // wait + recovery logic stays in WaitForConditionRecoveryAsync.
                return ScoutOutcome.Proceed;

            case ScoutClassification.Obstruction:
                var now = await GetMountUtcNowAsync(cancellationToken);
                var elapsed = now - observation.Start.UtcDateTime;
                var remaining = observation.Duration - (elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero);
                if (remaining <= TimeSpan.Zero) remaining = observation.Duration;

                if (scoutResult.EstimatedClearIn is { } clearIn
                    && clearIn > TimeSpan.Zero
                    && clearIn <= TimeSpan.FromTicks((long)(remaining.Ticks * Configuration.ObstructionClearFractionOfRemaining)))
                {
                    var waitFor = clearIn + TimeSpan.FromSeconds(30); // small margin for ephemeris error
                    _logger.LogInformation(
                        "Scout: obstruction on {Target} expected to clear in {Clear}; waiting {Wait} then re-scouting.",
                        observation.Target, clearIn, waitFor);
                    await _timeProvider.SleepAsync(waitFor, cancellationToken);

                    var retry = await ScoutAndProbeAsync(observation, cancellationToken);
                    if (retry.Classification == ScoutClassification.Healthy)
                    {
                        return ScoutOutcome.Proceed;
                    }
                    _logger.LogWarning(
                        "Scout: {Target} still obstructed after wait ({Class}); advancing.",
                        observation.Target, retry.Classification);
                    return ScoutOutcome.Advance;
                }

                _logger.LogWarning(
                    "Scout: obstruction on {Target} with no usable clear time (estimate={Clear}, remaining={Remaining}); advancing.",
                    observation.Target, scoutResult.EstimatedClearIn?.ToString() ?? "never", remaining);
                return ScoutOutcome.Advance;

            default:
                return ScoutOutcome.Proceed;
        }
    }

    /// <summary>
    /// Two-phase probe:
    /// <list type="number">
    /// <item>Take a short scout exposure on every OTA, compare star count to the previous
    /// observation's baseline (exposure-scaled). If healthy, return immediately.</item>
    /// <item>Otherwise nudge the mount up in altitude by <c>ObstructionNudgeRadii × half-FOV</c>
    /// of the widest OTA, scout again, and classify: recovery → obstruction; still bad →
    /// transparency. The mount is always re-slewed back to the target before returning.</item>
    /// </list>
    /// When the result is <see cref="ScoutClassification.Obstruction"/>, the trajectory is
    /// projected to estimate when the target's natural altitude reaches the nudged altitude;
    /// the caller decides whether to wait or advance based on
    /// <see cref="SessionConfiguration.ObstructionClearFractionOfRemaining"/>.
    /// </summary>
    internal async ValueTask<ScoutResult> ScoutAndProbeAsync(
        ScheduledObservation observation, CancellationToken cancellationToken)
    {
        var scoutExposure = Configuration.ScoutExposure ?? TimeSpan.FromSeconds(10);
        var scopes = Setup.Telescopes.Length;

        // Phase 1: scout exposure on every OTA. Single-mount invariant means all OTAs
        // shoot the same patch of sky simultaneously; we drive them sequentially since
        // FakeCameraDriver / real cameras don't share contention here.
        var preMetrics = new FrameMetrics[scopes];
        for (var i = 0; i < scopes; i++)
        {
            preMetrics[i] = await TakeScoutFrameAsync(i, scoutExposure, cancellationToken);
        }

        var prevBaseline = TryGetPreviousObservationBaseline();

        // No baseline yet (first observation of session) → no model to compare against.
        // Plan flags this as a known limitation; conservative answer is "trust and proceed".
        if (prevBaseline is null)
        {
            _logger.LogInformation(
                "Scout: no previous baseline for {Target}; skipping obstruction classification.",
                observation.Target);
            return new ScoutResult(preMetrics, ScoutClassification.Healthy, null);
        }

        var (classification, _) = ClassifyAgainstBaseline(preMetrics, prevBaseline);
        if (classification == ScoutClassification.Healthy)
        {
            _logger.LogInformation(
                "Scout: {Target} healthy ({Stars} stars vs prev baseline).",
                observation.Target, FormatStarCounts(preMetrics));
            return new ScoutResult(preMetrics, ScoutClassification.Healthy, null);
        }

        _logger.LogWarning(
            "Scout: {Target} flagged ({Stars} stars vs prev baseline {BaselineStars}); running altitude-nudge disambiguation.",
            observation.Target, FormatStarCounts(preMetrics), FormatStarCounts(prevBaseline));

        // Phase 2: nudge test
        var (postMetrics, refinedClassification) = await NudgeTestAsync(observation, preMetrics, scoutExposure, cancellationToken);
        if (refinedClassification == ScoutClassification.Transparency)
        {
            _logger.LogWarning(
                "Scout: {Target} still degraded after altitude nudge ({Stars}); classifying as Transparency.",
                observation.Target, FormatStarCounts(postMetrics));
            return new ScoutResult(preMetrics, ScoutClassification.Transparency, null);
        }

        // Phase 3: obstruction confirmed — estimate trajectory clear time
        var clearIn = await EstimateObstructionClearTimeAsync(observation, cancellationToken);
        _logger.LogWarning(
            "Scout: {Target} classified as Obstruction; estimated clear in {Clear}.",
            observation.Target, clearIn?.ToString() ?? "never");

        return new ScoutResult(preMetrics, ScoutClassification.Obstruction, clearIn);
    }

    /// <summary>
    /// Pure-ish classifier: compares per-OTA star counts to the previous observation's
    /// baseline (exposure-scaled by sqrt — sky-noise scales with sqrt(t), star detectability
    /// roughly follows). Returns the worst-OTA classification because the scout is rig-wide.
    /// </summary>
    internal (ScoutClassification Classification, float WorstRatio) ClassifyAgainstBaseline(
        FrameMetrics[] scout, FrameMetrics[] baseline)
    {
        var worstRatio = float.MaxValue;
        var anyComparable = false;

        for (var i = 0; i < scout.Length && i < baseline.Length; i++)
        {
            var s = scout[i];
            var b = baseline[i];
            if (!b.IsValid) continue;
            if (b.StarCount <= 0) continue;

            // Sky background scales with sqrt(t); star SNR scales with sqrt(t).
            // Number of stars above a fixed SNR threshold therefore scales roughly with sqrt(t).
            // Same setup, same field — a 10s scout vs. 120s baseline yields ~sqrt(10/120) ≈ 0.29× stars.
            var expScale = (float)Math.Sqrt(s.Exposure.TotalSeconds / b.Exposure.TotalSeconds);
            var expectedStars = b.StarCount * expScale;
            if (expectedStars <= 0) continue;

            var ratio = s.StarCount / expectedStars;
            if (ratio < worstRatio) worstRatio = ratio;
            anyComparable = true;
        }

        if (!anyComparable)
        {
            // Baseline existed but no OTA was comparable → no judgement
            return (ScoutClassification.Healthy, float.NaN);
        }

        if (worstRatio >= Configuration.ObstructionStarCountRatioHealthy)
        {
            return (ScoutClassification.Healthy, worstRatio);
        }

        // Borderline (severe..healthy) and severe (<severe) both go to nudge test —
        // we can't tell obstruction from transparency without it. Caller treats both
        // identically; we tag as Obstruction tentatively and let the nudge confirm.
        return (ScoutClassification.Obstruction, worstRatio);
    }

    /// <summary>
    /// Slew up by <c>ObstructionNudgeRadii × half-FOV</c>, take a second scout, then
    /// re-slew to the original target. Recovery (post stars &gt;&gt; pre stars) classifies
    /// as obstruction; otherwise as transparency.
    /// </summary>
    private async ValueTask<(FrameMetrics[] PostMetrics, ScoutClassification Classification)> NudgeTestAsync(
        ScheduledObservation observation, FrameMetrics[] preMetrics, TimeSpan scoutExposure, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var scopes = Setup.Telescopes.Length;

        var halfFovDeg = ComputeWidestHalfFovDeg();
        var nudgeDeg = Configuration.ObstructionNudgeRadii * halfFovDeg;

        if (nudgeDeg <= 0)
        {
            // No usable FOV info — can't run nudge test, treat as inconclusive (transparency)
            _logger.LogWarning("Scout: cannot compute FOV nudge (halfFov={HalfFov}°); skipping nudge test.", halfFovDeg);
            return (preMetrics, ScoutClassification.Transparency);
        }

        var nudgeTarget = observation.Target with { Dec = observation.Target.Dec + nudgeDeg };
        _logger.LogInformation(
            "Scout nudge: slewing +{NudgeDeg:F2}° in declination to {NudgeTarget}.",
            nudgeDeg, nudgeTarget);

        // Slew up
        var postMetrics = new FrameMetrics[scopes];
        try
        {
            var (postCondition, _) = await ResilientInvokeAsync(
                mount.Driver,
                ct => mount.Driver.BeginSlewToTargetAsync(nudgeTarget, Configuration.MinHeightAboveHorizon, ct),
                ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);

            if (postCondition is SlewPostCondition.Slewing)
            {
                await ResilientInvokeAsync(
                    mount.Driver,
                    ct => mount.Driver.WaitForSlewCompleteAsync(PollDeviceStatesAsync, ct),
                    ResilientCallOptions.IdempotentRead, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Nudge slew refused (e.g. above pole crossing) — bail out as inconclusive
                _logger.LogWarning("Scout nudge slew rejected ({PostCondition}); classifying as Transparency.", postCondition);
                return (preMetrics, ScoutClassification.Transparency);
            }

            for (var i = 0; i < scopes; i++)
            {
                postMetrics[i] = await TakeScoutFrameAsync(i, scoutExposure, cancellationToken);
            }
        }
        finally
        {
            // Always re-slew to the target's actual coordinates. Leaving the mount mispointed
            // after a nudge is bad hygiene even if we're about to advance.
            await ResilientInvokeAsync(
                mount.Driver,
                ct => mount.Driver.BeginSlewToTargetAsync(observation.Target, Configuration.MinHeightAboveHorizon, ct),
                ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);
            await ResilientInvokeAsync(
                mount.Driver,
                ct => mount.Driver.WaitForSlewCompleteAsync(PollDeviceStatesAsync, ct),
                ResilientCallOptions.IdempotentRead, cancellationToken).ConfigureAwait(false);
        }

        // Recovery decision: if any OTA's post-nudge star count clearly exceeded its pre-nudge
        // count, an obstruction is the most likely explanation (light unblocked at higher alt).
        // Threshold: post >= 2× max(pre, 5). The "max(pre, 5)" floor avoids classifying the noise
        // case 1 → 3 stars as "recovery". Bias is intentionally toward Transparency (safer to wait
        // out clouds via the existing recovery path than to skip a clear target by mistake).
        var recovered = false;
        for (var i = 0; i < scopes; i++)
        {
            var pre = preMetrics[i];
            var post = postMetrics[i];
            if (!post.IsValid) continue;

            var preFloor = Math.Max(pre.StarCount, 5);
            if (post.StarCount >= preFloor * 2)
            {
                recovered = true;
                _logger.LogInformation(
                    "Scout nudge: OTA #{Ota} stars {Pre} → {Post} (recovery), classifying as Obstruction.",
                    i + 1, pre.StarCount, post.StarCount);
                break;
            }
        }

        return recovered
            ? (postMetrics, ScoutClassification.Obstruction)
            : (postMetrics, ScoutClassification.Transparency);
    }

    /// <summary>
    /// Single-shot scout exposure helper. Mirrors the recipe used by
    /// <see cref="WaitForConditionRecoveryAsync"/>: abort any in-progress exposure,
    /// shoot at the configured scout duration, run star detection on the result,
    /// release the buffer. Returns <see cref="FrameMetrics"/>.<c>default</c> on any
    /// failure path so the caller's classifier sees an "invalid" metric.
    /// </summary>
    private async ValueTask<FrameMetrics> TakeScoutFrameAsync(
        int telescopeIndex, TimeSpan scoutExposure, CancellationToken cancellationToken)
    {
        var camera = Setup.Telescopes[telescopeIndex].Camera.Driver;

        // Cancel any in-progress exposure left over from prior phases
        if (await camera.GetCameraStateAsync(cancellationToken) is CameraState.Exposing)
        {
            if (camera.CanAbortExposure)
            {
                await camera.AbortExposureAsync(cancellationToken);
            }
            else if (camera.CanStopExposure)
            {
                await camera.StopExposureAsync(cancellationToken);
            }
            await _timeProvider.SleepAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }

        await ResilientInvokeAsync(
            camera,
            ct => camera.StartExposureAsync(scoutExposure, cancellationToken: ct),
            ResilientCallOptions.NonIdempotentAction, cancellationToken);
        await _timeProvider.SleepAsync(scoutExposure + TimeSpan.FromSeconds(2), cancellationToken);

        if (!await camera.GetImageReadyAsync(cancellationToken))
        {
            return default;
        }

        var image = await ResilientInvokeAsync(
            camera, ((ICameraDriver)camera).GetImageAsync,
            ResilientCallOptions.IdempotentRead, cancellationToken);
        if (image is null)
        {
            return default;
        }

        try
        {
            var stars = await image.FindStarsAsync(0, snrMin: 10, maxStars: 200, cancellationToken: cancellationToken);
            var gain = await camera.GetGainAsync(cancellationToken);
            return FrameMetrics.FromStarList(stars, scoutExposure, gain, image.Width, image.Height);
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>
    /// Looks up the baseline metrics from the previous observation index. Returns null when
    /// this is the first observation of the session or when the previous one never established
    /// a baseline (e.g. it was advanced before <c>BaselineHfdFrameCount</c> frames landed).
    /// </summary>
    private FrameMetrics[]? TryGetPreviousObservationBaseline()
    {
        var prev = _activeObservation - 1;
        if (prev < 0) return null;
        return _baselineByObservation.TryGetValue(prev, out var baseline) ? baseline : null;
    }

    /// <summary>
    /// Half-FOV (degrees) of the widest OTA in the rig — drives the nudge slew amount.
    /// Returns 0 when no OTA reports usable pixel scale + readout dimensions; the caller
    /// then bails out of the nudge test gracefully.
    /// </summary>
    internal double ComputeWidestHalfFovDeg()
    {
        var maxFovDeg = 0.0;
        for (var i = 0; i < Setup.Telescopes.Length; i++)
        {
            var t = Setup.Telescopes[i];
            var c = t.Camera.Driver;
            var pixelScaleArcsec = CoordinateUtils.PixelScaleArcsec(c.PixelSizeX, t.FocalLength);
            if (double.IsNaN(pixelScaleArcsec) || c.NumX <= 0) continue;
            var bin = Math.Max(1, c.BinX);
            var fovDeg = c.NumX * bin * pixelScaleArcsec / 3600.0;
            if (fovDeg > maxFovDeg) maxFovDeg = fovDeg;
        }
        return maxFovDeg / 2.0;
    }

    /// <summary>
    /// Estimates how long until the target's natural altitude reaches
    /// <c>currentAlt + nudgeDeg</c> — the altitude at which the scout reportedly cleared the
    /// obstruction. Returns null if the target is descending (will never clear at the current
    /// nudge geometry) or if it won't clear within the lookahead window (default 2 h).
    /// </summary>
    internal async ValueTask<TimeSpan?> EstimateObstructionClearTimeAsync(
        ScheduledObservation observation, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        if (await mount.Driver.TryGetTransformAsync(cancellationToken) is not { } transform)
        {
            return null;
        }

        var halfFovDeg = ComputeWidestHalfFovDeg();
        var nudgeDeg = Configuration.ObstructionNudgeRadii * halfFovDeg;
        if (nudgeDeg <= 0)
        {
            return null;
        }

        var now = await GetMountUtcNowAsync(cancellationToken);
        transform.DateTime = now;
        transform.SetJ2000(observation.Target.RA, observation.Target.Dec);
        transform.Refresh();
        var altNow = transform.ElevationTopocentric;
        var clearAlt = altNow + nudgeDeg;

        var step = TimeSpan.FromMinutes(2);
        var maxLookahead = TimeSpan.FromHours(2);

        // One step ahead → check if rising
        transform.DateTime = now.Add(step);
        transform.Refresh();
        var altNext = transform.ElevationTopocentric;
        if (altNext <= altNow)
        {
            // Target is at or past meridian — won't gain altitude
            return null;
        }

        var elapsed = step;
        var alt = altNext;
        while (elapsed < maxLookahead)
        {
            if (alt >= clearAlt)
            {
                return elapsed;
            }
            elapsed += step;
            transform.DateTime = now.Add(elapsed);
            transform.Refresh();
            alt = transform.ElevationTopocentric;
        }

        return null;
    }

    private static string FormatStarCounts(FrameMetrics[] metrics)
    {
        if (metrics.Length == 0) return "[]";
        if (metrics.Length == 1) return metrics[0].StarCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parts = new string[metrics.Length];
        for (var i = 0; i < metrics.Length; i++)
        {
            parts[i] = metrics[i].StarCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return "[" + string.Join("/", parts) + "]";
    }
}
