using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    /// <summary>Why a per-filter sky-flat capture loop returned.</summary>
    private enum SkyFlatFilterOutcome
    {
        /// <summary>Captured the requested number of frames for the filter.</summary>
        Completed,
        /// <summary>The twilight window closed for this filter (pinned at an exposure bound, sky ramping away); other filters may still be usable.</summary>
        FilterWindowClosed,
        /// <summary>A metering exposure returned no image.</summary>
        NoImage,
        /// <summary>The whole run's wall-clock deadline was reached.</summary>
        RunExpired,
        /// <summary>Cancellation was requested.</summary>
        Cancelled
    }

    /// <summary>
    /// Automated <em>twilight sky-flat</em> acquisition. Points near the anti-solar zenith with tracking
    /// off (so the field drifts and stars average out of the flat master), then per OTA / installed filter
    /// re-meters the exposure every frame as the sky brightness ramps (via the pure
    /// <see cref="SkyFlatExposureSolver"/>) and keeps the in-tolerance frames as <see cref="FrameType.Flat"/>.
    /// Frames carry the same denormalised FITS metadata the lights do, so the stacker's
    /// <c>MasterFrameBuilder</c> groups + matches them with no extra wiring (identical output contract to the
    /// panel path in <see cref="Session.TakeFlatsAsync"/>).
    /// </summary>
    /// <param name="period">
    /// <see cref="TwilightPeriod.Dawn"/> (morning, sky brightening -> exposures shorten -> stop when too
    /// bright at min) or <see cref="TwilightPeriod.Dusk"/> (evening, sky darkening -> exposures lengthen ->
    /// stop when too dim at max). Dawn runs at the end-of-session flat hook; dusk runs at session start.
    /// </param>
    /// <remarks>
    /// Covers are opened (opposite of the panel path). A coarse solar-altitude gate skips the run outright
    /// when the window has clearly already passed in the terminal direction; the per-frame solver does the
    /// fine convergence and waits (bounded by <see cref="SessionConfiguration.FlatSkyMaxDuration"/>) while the
    /// sky ramps into band. Tracking is left off; <c>Finalise</c> (dawn) or the subsequent slew (dusk) handles
    /// the mount afterwards.
    /// </remarks>
    internal async ValueTask TakeSkyFlatsAsync(TwilightPeriod period, CancellationToken cancellationToken)
    {
        var cfg = Configuration;
        var target = cfg.FlatTargetAduFraction;
        var tolerance = cfg.FlatAduTolerance;
        var flatsPerFilter = Math.Max(1, cfg.FlatsPerFilter);
        var initialExposure = cfg.FlatInitialExposure ?? SessionConfiguration.DefaultFlatInitialExposure;
        var minExposure = cfg.FlatMinExposure ?? SessionConfiguration.DefaultFlatMinExposure;
        var maxExposure = cfg.FlatMaxExposure ?? SessionConfiguration.DefaultFlatMaxExposure;
        var tilt = cfg.FlatSkyMeridianTilt ?? SessionConfiguration.DefaultFlatSkyMeridianTilt;
        var maxDuration = cfg.FlatSkyMaxDuration ?? SessionConfiguration.DefaultFlatSkyMaxDuration;
        var settleInterval = cfg.FlatSkySettleInterval ?? SessionConfiguration.DefaultFlatSkySettleInterval;

        _currentActivity = $"Taking {period} sky flats\u2026";
        _logger.LogInformation(
            "Sky-flat acquisition starting ({Period}): target {Target:P0} +/- {Tolerance:P0}, {Count} frame(s)/filter, exposure [{Min:F3}s, {Max:F3}s], window <= {Duration}.",
            period, target, tolerance, flatsPerFilter, minExposure.TotalSeconds, maxExposure.TotalSeconds, maxDuration);

        var mount = Setup.Mount;

        // Sky flats image the open sky -> covers OPEN (opposite of the panel path).
        await MoveTelescopeCoversToStateAsync(CoverStatus.Open, cancellationToken).ConfigureAwait(false);

        // Coarse window gate: skip immediately if the sun has already passed the usable band in the
        // terminal direction (dawn already past the bright edge / dusk already past the dark edge).
        if (await IsSkyFlatWindowPastAsync(period, cfg.FlatSkySunAltitudeBrightDeg, cfg.FlatSkySunAltitudeDarkDeg, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Sky-flat window for {Period} has already passed; skipping sky flats.", period);
            return;
        }

        // Point near the zenith, tilted toward the anti-solar sky (west at dawn, east at dusk) to minimise
        // the twilight gradient across the frame.
        var haTilt = period == TwilightPeriod.Dawn ? tilt : tilt.Negate();
        _logger.LogInformation("Slewing near zenith (HA {Ha:F2}h, Dec = site latitude) for {Period} sky flats.", haTilt.TotalHours, period);
        await mount.Driver.BeginSlewToZenithAsync(haTilt, cancellationToken).ConfigureAwait(false);
        if (!await mount.Driver.WaitForSlewCompleteAsync(PollDeviceStatesAsync, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Slew to the sky-flat zenith position did not complete cleanly; proceeding with current pointing.");
        }

        // Tracking OFF: the field drifts between frames so stars smear/move and the flat master's rejection
        // averages them out (no dither slews needed).
        if (mount.Driver.CanSetTracking)
        {
            await mount.Driver.SetTrackingAsync(false, cancellationToken).ConfigureAwait(false);
        }

        var deadlineUtc = _timeProvider.GetUtcNow().UtcDateTime + maxDuration;

        var runEnded = false;
        for (var i = 0; i < Setup.Telescopes.Length && !runEnded && !cancellationToken.IsCancellationRequested; i++)
        {
            var telescope = Setup.Telescopes[i];
            var camDriver = telescope.Camera.Driver;
            var (filterWheel, positions) = ResolveFilterPositions(telescope);

            // Warm-start the exposure at the initial value; carry the converged value across filters since
            // adjacent filters usually land close (and the sky keeps drifting between them).
            var exposure = Clamp(initialExposure, minExposure, maxExposure);

            foreach (var position in positions)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    runEnded = true;
                    break;
                }

                var filterName = await PrepareFilterForFlatsAsync(i, telescope, camDriver, filterWheel, position, cancellationToken).ConfigureAwait(false);
                var (nextExposure, outcome) = await CaptureSkyFlatsForFilterAsync(
                    period, i, camDriver, filterName, target, tolerance, minExposure, maxExposure,
                    exposure, flatsPerFilter, settleInterval, deadlineUtc, cancellationToken).ConfigureAwait(false);

                exposure = Clamp(nextExposure, minExposure, maxExposure);

                if (outcome is SkyFlatFilterOutcome.RunExpired or SkyFlatFilterOutcome.Cancelled)
                {
                    runEnded = true;
                    break;
                }
            }
        }

        _logger.LogInformation("Sky-flat acquisition complete ({Period}).", period);
    }

    /// <summary>
    /// Captures twilight sky-flats for a single filter: meter -> keep-if-in-tolerance / adjust / wait / stop,
    /// re-centring the exposure against the drifting sky after each kept frame. Returns the exposure to
    /// warm-start the next filter with and why the loop ended.
    /// </summary>
    private async ValueTask<(TimeSpan Exposure, SkyFlatFilterOutcome Outcome)> CaptureSkyFlatsForFilterAsync(
        TwilightPeriod period,
        int otaIndex,
        ICameraDriver camDriver,
        string filterName,
        double target,
        double tolerance,
        TimeSpan minExposure,
        TimeSpan maxExposure,
        TimeSpan exposure,
        int flatsPerFilter,
        TimeSpan settleInterval,
        DateTime deadlineUtc,
        CancellationToken cancellationToken)
    {
        var kept = 0;
        while (kept < flatsPerFilter)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return (exposure, SkyFlatFilterOutcome.Cancelled);
            }

            if (_timeProvider.GetUtcNow().UtcDateTime >= deadlineUtc)
            {
                _logger.LogWarning("Telescope #{TelescopeNumber} filter '{Filter}': sky-flat window deadline reached after {Kept}/{Total} frame(s).",
                    otaIndex + 1, filterName, kept, flatsPerFilter);
                return (exposure, SkyFlatFilterOutcome.RunExpired);
            }

            var frame = await CaptureFlatFrameAsync(camDriver, exposure, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                _logger.LogWarning("Telescope #{TelescopeNumber} filter '{Filter}': sky metering exposure returned no image; stopping this filter.", otaIndex + 1, filterName);
                return (exposure, SkyFlatFilterOutcome.NoImage);
            }

            SkyFlatDecision decision;
            try
            {
                var level = MeasureFlatLevel(frame);
                decision = SkyFlatExposureSolver.Decide(period, level, exposure, target, tolerance, minExposure, maxExposure);
                _logger.LogInformation(
                    "Telescope #{TelescopeNumber} filter '{Filter}': sky level {Level:P1} at {Exposure:F3}s -> {Action}.",
                    otaIndex + 1, filterName, level, exposure.TotalSeconds, decision.Action);

                if (decision.Action is SkyFlatAction.Capture)
                {
                    await WriteFlatToFitsFileAsync(frame, otaIndex, frame.ImageMeta.ExposureStartTime, kept + 1).ConfigureAwait(false);
                }
            }
            finally
            {
                frame.Release();
            }

            switch (decision.Action)
            {
                case SkyFlatAction.Capture:
                    kept++;
                    exposure = decision.NextExposure; // re-centre against the drifting sky
                    break;

                case SkyFlatAction.Adjust:
                    exposure = decision.NextExposure;
                    break;

                case SkyFlatAction.Wait:
                    _logger.LogInformation("Telescope #{TelescopeNumber} filter '{Filter}': {Reason} Waiting {Settle} for the sky.", otaIndex + 1, filterName, decision.Reason, settleInterval);
                    await _timeProvider.SleepAsync(settleInterval, cancellationToken).ConfigureAwait(false);
                    break;

                case SkyFlatAction.Stop:
                    _logger.LogInformation("Telescope #{TelescopeNumber} filter '{Filter}': {Reason} No more sky flats for this filter ({Kept} captured).", otaIndex + 1, filterName, decision.Reason, kept);
                    return (exposure, SkyFlatFilterOutcome.FilterWindowClosed);
            }
        }

        _logger.LogInformation("Telescope #{TelescopeNumber} filter '{Filter}': captured {Kept} sky flat(s).", otaIndex + 1, filterName, kept);
        return (exposure, SkyFlatFilterOutcome.Completed);
    }

    /// <summary>
    /// Coarse solar-altitude gate for a sky-flat run. Returns <c>true</c> when the twilight window has clearly
    /// already passed in the terminal ramp direction (dawn: sun risen above the bright edge; dusk: sun sunk
    /// below the dark edge) so the run should be skipped rather than wait out the whole deadline. When the sun
    /// altitude cannot be computed (site unknown, ephemeris failure) it returns <c>false</c> (proceed).
    /// </summary>
    private async ValueTask<bool> IsSkyFlatWindowPastAsync(TwilightPeriod period, double brightDeg, double darkDeg, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount.Driver;
        var latitude = Configuration.SiteLatitude;
        var longitude = Configuration.SiteLongitude;
        if (double.IsNaN(latitude) || double.IsNaN(longitude))
        {
            latitude = await mount.GetSiteLatitudeAsync(cancellationToken).ConfigureAwait(false);
            longitude = await mount.GetSiteLongitudeAsync(cancellationToken).ConfigureAwait(false);
        }

        if (double.IsNaN(latitude) || double.IsNaN(longitude)
            || !VSOP87a.Reduce(CatalogIndex.Sol, _timeProvider.GetUtcNow(), latitude, longitude, out _, out _, out _, out var sunAltDeg, out _))
        {
            _logger.LogWarning("Could not compute solar altitude for the sky-flat window gate; proceeding without it.");
            return false;
        }

        _logger.LogInformation("Sky-flat window gate ({Period}): sun altitude {Alt:F1} deg (usable band [{Dark:F1}, {Bright:F1}] deg).", period, sunAltDeg, darkDeg, brightDeg);
        return period switch
        {
            TwilightPeriod.Dawn => sunAltDeg > brightDeg, // already daylight -> missed the morning window
            TwilightPeriod.Dusk => sunAltDeg < darkDeg,   // already dark -> missed the evening window
            _ => false
        };
    }
}
