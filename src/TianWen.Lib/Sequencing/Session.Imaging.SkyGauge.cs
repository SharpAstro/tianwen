using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Zenith-anchored night-sky gauge: the first-scout obstruction oracle (A) and the whole-sky
/// cloud gate (C). The rough-focus frames are taken near the zenith, which for any real setup is
/// unobstructed and at ~air mass 1, so the detected-vs-catalog-predicted ratio there is a clean,
/// obstruction-free read of transparency x detection efficiency (<see cref="NightSkyGauge"/>).
/// <list type="bullet">
/// <item><b>A (oracle)</b>: on the first observation of the night there is no same-night baseline,
/// so <c>ScoutAndProbeAsync</c> instead calibrates an expected star count for the target as
/// <c>catalog(target, scout limit, airmass-dimmed) x zenith efficiency</c> and routes a large
/// shortfall into the existing altitude-nudge disambiguation.</item>
/// <item><b>C (cloud gate)</b>: a crushed zenith efficiency cannot be obstruction (the zenith is
/// never blocked), so it means cloud; the session holds and re-gauges before sinking the night
/// into thick cloud.</item>
/// </list>
/// See <c>docs/plans/obstruction-first-light-oracle.md</c>.
/// </summary>
internal partial record Session
{
    /// <summary>Atmospheric extinction in magnitudes per unit air mass (broadband V, typical dark site).</summary>
    internal const double AtmosphericExtinctionMagPerAirmass = 0.2;

    /// <summary>
    /// Per-OTA zenith calibration captured during <c>InitialRoughFocusAsync</c>. Index matches
    /// <see cref="Setup"/>.<c>Telescopes</c>. Entries default to <see cref="NightSkyGauge.None"/>
    /// (invalid) until a rough-focus frame is gauged.
    /// </summary>
    private NightSkyGauge[] _nightSkyGauges = [];

    /// <summary>Sizes <see cref="_nightSkyGauges"/> to the OTA count and fills it with <see cref="NightSkyGauge.None"/>.</summary>
    private void EnsureNightSkyGaugeArray()
    {
        if (_nightSkyGauges.Length != Setup.Telescopes.Length)
        {
            _nightSkyGauges = new NightSkyGauge[Setup.Telescopes.Length];
            Array.Fill(_nightSkyGauges, NightSkyGauge.None);
        }
    }

    /// <summary>
    /// Field of view (degrees) of an OTA, width x height, from pixel scale x readout dimensions x binning.
    /// Returns <c>(0, 0)</c> when the OTA reports no usable pixel scale / dimensions.
    /// </summary>
    internal static (double Wdeg, double Hdeg) ComputeFovDegrees(OTA t)
    {
        var c = t.Camera.Driver;
        var pixelScaleX = CoordinateUtils.PixelScaleArcsec(c.PixelSizeX, t.FocalLength);
        if (double.IsNaN(pixelScaleX) || c.NumX <= 0 || c.NumY <= 0)
        {
            return (0.0, 0.0);
        }
        var pixelScaleY = CoordinateUtils.PixelScaleArcsec(c.PixelSizeY, t.FocalLength);
        if (double.IsNaN(pixelScaleY))
        {
            pixelScaleY = pixelScaleX; // square-pixel fallback
        }
        var binX = Math.Max(1, c.BinX);
        var binY = Math.Max(1, c.BinY);
        var wDeg = c.NumX * binX * pixelScaleX / 3600.0;
        var hDeg = c.NumY * binY * pixelScaleY / 3600.0;
        return (wDeg, hDeg);
    }

    /// <summary>
    /// Relative light grasp, <c>(apertureMm / 50)^2</c>, mirroring <c>FakeCameraDriver</c>'s synthetic
    /// render scale so the gauge's catalog prediction and the fake's rendered truth share one
    /// light-grasp model (feedback_one_path). When the OTA has no configured aperture this collapses
    /// to the 50 mm reference, which yields a bright theoretical limit and therefore few predicted
    /// stars; the oracle then auto-skips below <see cref="SessionConfiguration.MinOracleStarCount"/>,
    /// which is the correct conservative behaviour for an unconfigured rig (don't run a test you can't
    /// calibrate). Because the zenith prediction and the target prediction use this same derivation, a
    /// systematic aperture mis-estimate largely cancels in <c>efficiency x catalog(target)</c>.
    /// </summary>
    internal static double ComputeApertureScaleFactor(OTA t)
        => t.Aperture is int apertureMm and > 0
            ? Math.Pow(apertureMm / 50.0, 2.0)
            : 1.0;

    /// <summary>Plane-parallel air mass (sec z) from topocentric altitude, capped below ~5 deg where it diverges.</summary>
    internal static double AirmassFromAltitude(double altitudeDeg)
    {
        var alt = Math.Clamp(altitudeDeg, 5.0, 90.0);
        return 1.0 / Math.Sin(alt * Math.PI / 180.0);
    }

    /// <summary>
    /// Builds a <see cref="NightSkyGauge"/> from an already-detected star count plus the field's
    /// catalog magnitude histogram. Shared by the zenith capture (rough-focus count) and the cloud-gate
    /// live re-gauge (fresh scout count) so the two cannot drift.
    /// </summary>
    /// <param name="otaIndex">OTA whose optics define FOV + light grasp.</param>
    /// <param name="detectedStars">Stars detected in the frame.</param>
    /// <param name="exposureSeconds">Exposure length of the frame.</param>
    /// <param name="snrThreshold">SNR floor the detection ran at (rough focus = 15, scout = 10).</param>
    /// <param name="raHours">Field centre RA (J2000 hours).</param>
    /// <param name="decDeg">Field centre Dec (J2000 degrees).</param>
    internal async ValueTask<NightSkyGauge> ComputeSkyGaugeAsync(
        int otaIndex, int detectedStars, double exposureSeconds, double snrThreshold,
        double raHours, double decDeg, CancellationToken cancellationToken)
    {
        var t = Setup.Telescopes[otaIndex];
        var (fovW, fovH) = ComputeFovDegrees(t);
        if (fovW <= 0 || fovH <= 0)
        {
            return NightSkyGauge.None;
        }

        var apertureScale = ComputeApertureScaleFactor(t);
        var theoreticalLimit = StarDetectionModel.DetectabilityMagCutoff(apertureScale, exposureSeconds, snrThreshold: snrThreshold);
        var db = await External.GetCelestialObjectDBAsync(cancellationToken);
        var magBins = CatalogStarCounter.CountStarsByMagnitude(db, raHours, decDeg, fovW, fovH);
        return NightSkyGauge.FromCounts(detectedStars, magBins, theoreticalLimit, Configuration.MinOracleStarCount);
    }

    /// <summary>
    /// Captures the per-OTA zenith gauge from a successful rough-focus frame (detection ran at
    /// <c>FindStarsAsync</c> snrMin 15). Best-effort: any failure leaves the gauge as
    /// <see cref="NightSkyGauge.None"/> and the first-scout oracle falls back to a pure catalog floor.
    /// </summary>
    internal async ValueTask CaptureZenithGaugeAsync(
        int otaIndex, int detectedStars, int exposureSec, double zenithRaHours, double zenithDecDeg, CancellationToken cancellationToken)
    {
        if (!Configuration.FirstScoutOracleEnabled)
        {
            return;
        }
        EnsureNightSkyGaugeArray();
        try
        {
            var gauge = await ComputeSkyGaugeAsync(otaIndex, detectedStars, exposureSec, 15.0, zenithRaHours, zenithDecDeg, cancellationToken);
            _nightSkyGauges[otaIndex] = gauge;
            _logger.LogInformation(
                "Zenith gauge OTA #{Ota}: detected {Detected} vs predicted {Predicted} (V<={TheoLimit:F1}) "
                + "-> efficiency {Eff:P0}, effective limit V<={EffLimit:F1}, valid={Valid}.",
                otaIndex + 1, gauge.DetectedAtZenith, gauge.CatalogPredictedAtZenith,
                gauge.TheoreticalLimitMag, gauge.Efficiency, gauge.EffectiveLimitMag, gauge.Valid);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Zenith gauge: capture for OTA #{Ota} failed; first-scout oracle will fall back to catalog floor.", otaIndex + 1);
        }
    }

    /// <summary>Highest valid zenith efficiency across all OTAs, or null when no gauge is valid.</summary>
    private double? BestValidZenithEfficiency()
    {
        double? best = null;
        for (var i = 0; i < _nightSkyGauges.Length; i++)
        {
            var g = _nightSkyGauges[i];
            if (g.Valid && (best is null || g.Efficiency > best.Value))
            {
                best = g.Efficiency;
            }
        }
        return best;
    }

    /// <summary>
    /// First-scout obstruction oracle (A). Calibrates an expected star count for the target from the
    /// zenith gauge and the catalog, then compares the scout's detected count to
    /// <c>expected x <see cref="SessionConfiguration.OracleFactor"/></c>.
    /// <list type="bullet">
    /// <item>Returns <see cref="ScoutClassification.Healthy"/> when the worst OTA meets the threshold.</item>
    /// <item>Returns <see cref="ScoutClassification.Obstruction"/> (tentative) when an OTA falls short,
    /// so the caller runs the altitude-nudge disambiguation.</item>
    /// <item>Returns <c>null</c> when the oracle is disabled or no OTA could be judged (tiny FOV, too few
    /// predicted stars, narrowband filter, scout frame never ran) -- the caller then proceeds.</item>
    /// </list>
    /// </summary>
    internal async ValueTask<ScoutClassification?> ClassifyFirstScoutAgainstZenithAsync(
        ScheduledObservation observation, FrameMetrics[] preMetrics, TimeSpan scoutExposure, CancellationToken cancellationToken)
    {
        if (!Configuration.FirstScoutOracleEnabled)
        {
            return null;
        }
        EnsureNightSkyGaugeArray();

        // Defensive: the catalog DB resolve can fail (not configured, transient load error). Mirror the
        // fake camera's catch-and-fall-back behaviour -- an unavailable catalog means the oracle simply
        // can't judge (return null -> caller proceeds), never an exception that aborts the scout.
        ICelestialObjectDB db;
        try
        {
            db = await External.GetCelestialObjectDBAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "First-scout oracle: catalog DB unavailable for {Target}; proceeding without obstruction judgement.", observation.Target);
            return null;
        }

        // Target air mass dims the clear-sky limiting magnitude relative to the zenith gauge.
        var airmass = 1.0;
        var mount = Setup.Mount;
        if (await mount.Driver.TryGetTransformAsync(ResolveSiteConditions(), cancellationToken) is { } transform)
        {
            transform.DateTime = await GetMountUtcNowAsync(cancellationToken);
            transform.SetJ2000(observation.Target.RA, observation.Target.Dec);
            transform.Refresh();
            airmass = AirmassFromAltitude(transform.ElevationTopocentric);
        }

        var worstRatio = double.MaxValue;
        var anyJudged = false;

        for (var i = 0; i < Setup.Telescopes.Length; i++)
        {
            var scout = i < preMetrics.Length ? preMetrics[i] : default;
            if (scout.Exposure <= TimeSpan.Zero)
            {
                continue; // scout exposure never ran -> nothing to compare (matches the baseline path)
            }

            var t = Setup.Telescopes[i];

            // Narrowband filters cut star counts to near zero independent of sky conditions; the oracle's
            // broadband catalog prediction does not apply. Skip the OTA (the nudge test remains the backstop
            // if every OTA is narrowband and the field is genuinely obstructed -- it classifies "no recovery
            // on nudge" as Transparency -> Proceed, which is the safe outcome).
            if (FilterPlanBuilder.IsNarrowband(t.Camera.Driver.Filter))
            {
                _logger.LogDebug("First-scout oracle OTA #{Ota}: narrowband filter active; skipping.", i + 1);
                continue;
            }

            var (fovW, fovH) = ComputeFovDegrees(t);
            if (fovW <= 0 || fovH <= 0)
            {
                continue;
            }

            var apertureScale = ComputeApertureScaleFactor(t);
            // Scout detection used FindStarsAsync snrMin 10.
            var scoutLimit = StarDetectionModel.DetectabilityMagCutoff(apertureScale, scoutExposure.TotalSeconds, snrThreshold: 10.0);
            var effectiveLimit = scoutLimit - AtmosphericExtinctionMagPerAirmass * (airmass - 1.0);

            var catalogAtTarget = CatalogStarCounter.CountStarsInField(
                db, observation.Target.RA, observation.Target.Dec, fovW, fovH, effectiveLimit);

            // Too few catalog stars even before haze -> can't support the test for this OTA.
            if (catalogAtTarget < Configuration.MinOracleStarCount)
            {
                continue;
            }

            var gauge = i < _nightSkyGauges.Length ? _nightSkyGauges[i] : NightSkyGauge.None;
            var efficiency = gauge.Valid ? gauge.Efficiency : 1.0; // no valid gauge -> pure catalog floor
            var expected = catalogAtTarget * efficiency;
            var ratio = expected > 0 ? scout.StarCount / expected : double.MaxValue;
            if (ratio < worstRatio)
            {
                worstRatio = ratio;
            }
            anyJudged = true;

            _logger.LogInformation(
                "First-scout oracle OTA #{Ota}: detected {Detected} vs expected {Expected:F0} "
                + "(catalog {Catalog} @ V<={Limit:F1}, airmass {Airmass:F2}, zenith efficiency {Eff:P0}) -> ratio {Ratio:P0}.",
                i + 1, scout.StarCount, expected, catalogAtTarget, effectiveLimit, airmass, efficiency, ratio);
        }

        if (!anyJudged)
        {
            return null;
        }

        return worstRatio >= Configuration.OracleFactor
            ? ScoutClassification.Healthy
            : ScoutClassification.Obstruction;
    }

    /// <summary>
    /// Cloud gate (C). Runs once after rough focus: if the best valid zenith efficiency is below
    /// <see cref="SessionConfiguration.CloudGateEfficiencyFloor"/>, the whole sky is clouded (the zenith
    /// cannot be obstructed), so hold up to <see cref="SessionConfiguration.ConditionRecoveryTimeout"/>,
    /// re-gauging with a short frame at the current pointing until efficiency recovers above the floor.
    /// On timeout the session proceeds anyway -- the in-loop condition-deterioration check remains the
    /// backstop and aborting the whole night on a transient is too drastic. No-op when the oracle is
    /// disabled or no valid gauge exists to judge.
    /// </summary>
    internal async ValueTask WaitForCloudGateAsync(CancellationToken cancellationToken)
    {
        if (!Configuration.FirstScoutOracleEnabled)
        {
            return;
        }
        EnsureNightSkyGaugeArray();

        if (BestValidZenithEfficiency() is not { } eff || eff >= Configuration.CloudGateEfficiencyFloor)
        {
            return; // clear sky, or no valid gauge to judge
        }

        var timeout = Configuration.ConditionRecoveryTimeout ?? TimeSpan.FromMinutes(10);
        var deadline = await GetMountUtcNowAsync(cancellationToken) + timeout;
        _currentActivity = "Cloud gate: holding for transparency…";
        _logger.LogWarning(
            "Cloud gate: zenith detection efficiency {Eff:P0} < floor {Floor:P0} -- the whole sky appears clouded over. "
            + "Holding up to {Timeout} and re-gauging.", eff, Configuration.CloudGateEfficiencyFloor, timeout);

        var reGaugeInterval = TimeSpan.FromMinutes(1);
        while (await GetMountUtcNowAsync(cancellationToken) < deadline)
        {
            await _timeProvider.SleepAsync(reGaugeInterval, cancellationToken);

            var live = await ReGaugeSkyAsync(0, cancellationToken);
            if (!live.Valid)
            {
                continue;
            }
            _nightSkyGauges[0] = live;
            _logger.LogInformation("Cloud gate: re-gauge efficiency {Eff:P0} (effective limit V<={EffLimit:F1}).", live.Efficiency, live.EffectiveLimitMag);
            if (live.Efficiency >= Configuration.CloudGateEfficiencyFloor)
            {
                _logger.LogInformation("Cloud gate: conditions recovered (efficiency {Eff:P0}); proceeding.", live.Efficiency);
                return;
            }
        }

        _logger.LogWarning(
            "Cloud gate: still clouded after {Timeout}; proceeding anyway (in-loop deterioration check remains the backstop).", timeout);
    }

    /// <summary>
    /// Takes a fresh short scout frame on the OTA at the current mount pointing and gauges it (scout
    /// detection runs at snrMin 10). Returns <see cref="NightSkyGauge.None"/> when the frame or the
    /// pointing read fails.
    /// </summary>
    internal async ValueTask<NightSkyGauge> ReGaugeSkyAsync(int otaIndex, CancellationToken cancellationToken)
    {
        var exposure = Configuration.ScoutExposure ?? TimeSpan.FromSeconds(10);
        var metrics = await TakeScoutFrameAsync(otaIndex, exposure, cancellationToken);
        if (metrics.Exposure <= TimeSpan.Zero)
        {
            return NightSkyGauge.None;
        }

        var mount = Setup.Mount;
        if (await ResilientInvokeAsync(
                mount.Driver, ct => mount.Driver.GetRaDecJ2000Async(ct),
                ResilientCallOptions.IdempotentRead, cancellationToken) is not { } j2000)
        {
            return NightSkyGauge.None;
        }

        try
        {
            return await ComputeSkyGaugeAsync(
                otaIndex, metrics.StarCount, exposure.TotalSeconds, 10.0, j2000.RaJ2000, j2000.DecJ2000, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud gate: live re-gauge of OTA #{Ota} failed; treating as no reading.", otaIndex + 1);
            return NightSkyGauge.None;
        }
    }

    /// <summary>Test seam: seeds a zenith gauge for an OTA without running rough focus (no reflection).</summary>
    internal void SetZenithGaugeForTest(int otaIndex, NightSkyGauge gauge)
    {
        EnsureNightSkyGaugeArray();
        _nightSkyGauges[otaIndex] = gauge;
    }

    /// <summary>Test seam: reads back the captured zenith gauge for an OTA.</summary>
    internal NightSkyGauge GetZenithGaugeForTest(int otaIndex)
    {
        EnsureNightSkyGaugeArray();
        return _nightSkyGauges[otaIndex];
    }
}
