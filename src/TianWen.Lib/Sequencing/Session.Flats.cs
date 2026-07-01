using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    /// <summary>
    /// Automated end-of-session flat acquisition. Dispatches on <see cref="SessionConfiguration.FlatSource"/>:
    /// <see cref="FlatIlluminationSource.TwilightSky"/> runs <em>dawn</em> sky-flats
    /// (<see cref="TakeSkyFlatsAsync"/>); otherwise (the default) it runs panel/calibrator flats. Per OTA the
    /// panel path closes the cover (flip-flat panels illuminate the closed cover), turns the calibrator on at
    /// a coarse brightness, then for every installed filter auto-exposes to
    /// <see cref="SessionConfiguration.FlatTargetAduFraction"/> (via the pure <see cref="FlatExposureSolver"/>)
    /// and writes <see cref="FrameType.Flat"/> frames. The frames carry the same denormalised FITS metadata
    /// (filter, temperature, gain, binning, sensor) the lights do, so the stacker's <c>MasterFrameBuilder</c>
    /// groups + matches them with no extra wiring. Runs at the imaging setpoint temperature (before
    /// <c>Finalise</c> warms the cameras).
    /// </summary>
    /// <remarks>
    /// OTAs without a calibrator panel are skipped with a warning on the panel path. The mount sign and
    /// pointing are untouched on the panel path; the cover is left closed for <c>Finalise</c> to handle.
    /// </remarks>
    internal async ValueTask TakeFlatsAsync(CancellationToken cancellationToken)
    {
        if (Configuration.FlatSource is FlatIlluminationSource.TwilightSky)
        {
            await TakeSkyFlatsAsync(TwilightPeriod.Dawn, cancellationToken).ConfigureAwait(false);
            return;
        }

        var cfg = Configuration;
        var target = cfg.FlatTargetAduFraction;
        var tolerance = cfg.FlatAduTolerance;
        var maxBrackets = Math.Max(1, cfg.FlatMaxBrackets);
        var flatsPerFilter = Math.Max(1, cfg.FlatsPerFilter);
        var initialExposure = cfg.FlatInitialExposure ?? SessionConfiguration.DefaultFlatInitialExposure;
        var minExposure = cfg.FlatMinExposure ?? SessionConfiguration.DefaultFlatMinExposure;
        var maxExposure = cfg.FlatMaxExposure ?? SessionConfiguration.DefaultFlatMaxExposure;

        _logger.LogInformation(
            "Flat acquisition starting: target {Target:P0} +/- {Tolerance:P0}, {Count} frame(s)/filter, up to {Brackets} bracket(s), exposure [{Min:F3}s, {Max:F3}s].",
            target, tolerance, flatsPerFilter, maxBrackets, minExposure.TotalSeconds, maxExposure.TotalSeconds);

        // Close any covers up-front. NotPresent covers are handled gracefully; a pure calibrator
        // panel without a cover is unaffected.
        await MoveTelescopeCoversToStateAsync(CoverStatus.Closed, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < Setup.Telescopes.Length && !cancellationToken.IsCancellationRequested; i++)
        {
            var telescope = Setup.Telescopes[i];
            var camDriver = telescope.Camera.Driver;

            if (telescope.Cover?.Driver is not { } cover)
            {
                _logger.LogWarning("Telescope #{TelescopeNumber} '{Name}': no cover/calibrator device; skipping panel flats (set FlatSource=TwilightSky for sky-flats instead).",
                    i + 1, telescope.Name);
                continue;
            }

            await cover.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (await cover.GetCalibratorStateAsync(cancellationToken) is CalibratorStatus.NotPresent)
            {
                _logger.LogWarning("Telescope #{TelescopeNumber} '{Name}': cover has no calibrator panel; skipping panel flats.", i + 1, telescope.Name);
                continue;
            }

            var brightness = ResolveCalibratorBrightness(cover.MaxBrightness, cfg.FlatCalibratorBrightnessPercent);
            if (!await TurnCalibratorOnAndWaitAsync(cover, brightness, i, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogError("Telescope #{TelescopeNumber} '{Name}': calibrator did not become ready; skipping flats for this OTA.", i + 1, telescope.Name);
                continue;
            }

            try
            {
                await TakeFlatsForOtaAsync(i, telescope, camDriver, target, tolerance, maxBrackets, flatsPerFilter,
                    initialExposure, minExposure, maxExposure, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Always turn the panel off, even if a filter failed mid-way.
                await CatchAsync(cover.TurnOffCalibratorAndWaitAsync, cancellationToken, false).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Flat acquisition complete.");
    }

    /// <summary>Captures flats for every installed filter on one OTA (or a single no-filter pass).</summary>
    private async ValueTask TakeFlatsForOtaAsync(
        int otaIndex,
        OTA telescope,
        ICameraDriver camDriver,
        double target,
        double tolerance,
        int maxBrackets,
        int flatsPerFilter,
        TimeSpan initialExposure,
        TimeSpan minExposure,
        TimeSpan maxExposure,
        CancellationToken cancellationToken)
    {
        var (filterWheel, positions) = ResolveFilterPositions(telescope);

        foreach (var position in positions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var filterName = await PrepareFilterForFlatsAsync(otaIndex, telescope, camDriver, filterWheel, position, cancellationToken).ConfigureAwait(false);

            // Auto-exposure: bracket toward the target level, discarding the metering frames.
            var exposure = Clamp(initialExposure, minExposure, maxExposure);
            TimeSpan? convergedExposure = null;
            for (var attempt = 0; attempt < maxBrackets && !cancellationToken.IsCancellationRequested; attempt++)
            {
                var meteringFrame = await CaptureFlatFrameAsync(camDriver, exposure, cancellationToken).ConfigureAwait(false);
                if (meteringFrame is null)
                {
                    _logger.LogWarning("Telescope #{TelescopeNumber} filter '{Filter}': metering exposure returned no image; skipping filter.", otaIndex + 1, filterName);
                    break;
                }

                double level;
                try
                {
                    level = MeasureFlatLevel(meteringFrame);
                }
                finally
                {
                    meteringFrame.Release();
                }

                var decision = FlatExposureSolver.Solve(level, exposure, target, tolerance, minExposure, maxExposure, attempt, maxBrackets);
                _logger.LogInformation(
                    "Telescope #{TelescopeNumber} filter '{Filter}': metering {Attempt}/{Max}: level {Level:P1} at {Exposure:F3}s -> {Action}{Next}.",
                    otaIndex + 1, filterName, attempt + 1, maxBrackets, level, exposure.TotalSeconds, decision.Action,
                    decision.Action is FlatExposureAction.Adjust ? $" (next {decision.NextExposure.TotalSeconds:F3}s)" : "");

                if (decision.Action is FlatExposureAction.Capture)
                {
                    convergedExposure = decision.NextExposure;
                    break;
                }

                if (decision.Action is FlatExposureAction.Fail)
                {
                    _logger.LogWarning("Telescope #{TelescopeNumber} filter '{Filter}': {Reason}", otaIndex + 1, filterName, decision.Reason);
                    break;
                }

                exposure = decision.NextExposure;
            }

            if (convergedExposure is not { } converged)
            {
                _logger.LogWarning("Telescope #{TelescopeNumber} filter '{Filter}': no converged flat exposure; no flats written for this filter.", otaIndex + 1, filterName);
                continue;
            }

            _logger.LogInformation("Telescope #{TelescopeNumber} filter '{Filter}': converged at {Exposure:F3}s; capturing {Count} flat(s).",
                otaIndex + 1, filterName, converged.TotalSeconds, flatsPerFilter);

            for (var frame = 0; frame < flatsPerFilter && !cancellationToken.IsCancellationRequested; frame++)
            {
                var flat = await CaptureFlatFrameAsync(camDriver, converged, cancellationToken).ConfigureAwait(false);
                if (flat is null)
                {
                    _logger.LogWarning("Telescope #{TelescopeNumber} filter '{Filter}': flat frame #{Frame} returned no image.", otaIndex + 1, filterName, frame + 1);
                    continue;
                }

                try
                {
                    await WriteFlatToFitsFileAsync(flat, otaIndex, flat.ImageMeta.ExposureStartTime, frame + 1).ConfigureAwait(false);
                }
                finally
                {
                    flat.Release();
                }
            }
        }
    }

    /// <summary>Starts a <see cref="FrameType.Flat"/> exposure, waits it out, and polls for the frame.</summary>
    private async ValueTask<Image?> CaptureFlatFrameAsync(ICameraDriver camera, TimeSpan exposure, CancellationToken cancellationToken)
    {
        // Wait for the camera to be idle before arming the next exposure.
        var idleGuard = 0;
        while (await camera.GetCameraStateAsync(cancellationToken) is not CameraState.Idle and not CameraState.Error and not CameraState.NotConnected
            && ++idleGuard < IDeviceDriver.MAX_FAILSAFE
            && !cancellationToken.IsCancellationRequested)
        {
            await _timeProvider.SleepAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        await ResilientInvokeAsync(
            camera,
            ct => camera.StartExposureAsync(exposure, FrameType.Flat, ct),
            ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);

        // Sleep the exposure, then poll until the frame is ready (with a generous download margin).
        await _timeProvider.SleepAsync(exposure, cancellationToken).ConfigureAwait(false);

        var polled = TimeSpan.Zero;
        var pollTimeout = exposure + TimeSpan.FromSeconds(10);
        do
        {
            if (await ResilientInvokeAsync(
                    camera, camera.GetImageAsync,
                    ResilientCallOptions.IdempotentRead, cancellationToken).ConfigureAwait(false) is { Width: > 0, Height: > 0 } image)
            {
                return image;
            }

            await _timeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            polled += TimeSpan.FromMilliseconds(100);
        }
        while (polled < pollTimeout
            && await camera.GetCameraStateAsync(cancellationToken) is not CameraState.Error and not CameraState.NotConnected
            && !cancellationToken.IsCancellationRequested);

        return null;
    }

    /// <summary>Robust flat level as a fraction of the sensor ceiling (whole-frame median ADU / max ADU).</summary>
    private static double MeasureFlatLevel(Image image)
    {
        var stats = image.Statistics(0);
        var level = stats.Median ?? stats.Mean;
        var ceiling = stats.RescaledMaxValue ?? image.MaxValue;
        return ceiling > 0f ? level / ceiling : 0.0;
    }

    /// <summary>Maps a brightness percentage onto the driver's <c>MaxBrightness</c> (or passes it through when unknown).</summary>
    private static int ResolveCalibratorBrightness(int maxBrightness, int percent)
    {
        var pct = Math.Clamp(percent, 0, 100);
        return maxBrightness > 0
            ? Math.Max(1, (int)Math.Round(maxBrightness * (pct / 100.0)))
            : Math.Max(1, pct);
    }

    /// <summary>Turns the calibrator on and polls until it reports <see cref="CalibratorStatus.Ready"/>.</summary>
    private async ValueTask<bool> TurnCalibratorOnAndWaitAsync(ICoverDriver cover, int brightness, int otaIndex, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Telescope #{TelescopeNumber}: turning calibrator on at brightness {Brightness}.", otaIndex + 1, brightness);
        await cover.BeginCalibratorOn(brightness, cancellationToken).ConfigureAwait(false);

        var guard = 0;
        while (!cancellationToken.IsCancellationRequested && ++guard < IDeviceDriver.MAX_FAILSAFE)
        {
            var state = await cover.GetCalibratorStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is CalibratorStatus.Ready)
            {
                return true;
            }

            if (state is CalibratorStatus.Error or CalibratorStatus.NotPresent)
            {
                return false;
            }

            await _timeProvider.SleepAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Writes a flat frame under <c>&lt;output&gt;/Flats/&lt;date&gt;/&lt;filter&gt;/Flat/</c>. The
    /// path is cosmetic -- the stacker groups by FITS headers (IMAGETYP/FILTER/temperature/geometry),
    /// not folder layout -- so the frames are discovered and matched wherever they live under DataRoot.
    /// </summary>
    internal async ValueTask<string> WriteFlatToFitsFileAsync(Image image, int otaIndex, DateTimeOffset expStart, int frameNumber)
    {
        var meta = image.ImageMeta;
        var dateFolderUtc = expStart.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);
        var frameFolder = Path.Combine(
            External.ImageOutputFolder.FullName,
            "Flats",
            dateFolderUtc,
            External.GetSafeFileName(meta.Filter.Name),
            meta.FrameType.ToString());
        Directory.CreateDirectory(frameFolder);

        var fitsFileName = External.GetSafeFileName($"flat_ota{otaIndex + 1}_{expStart:yyyy-MM-ddTHH_mm_ss}_{frameNumber:0000}.fits");
        var fitsFilePath = Path.Combine(frameFolder, fitsFileName);

        _logger.LogInformation("Writing flat FITS file {FitsFilePath}", fitsFilePath);
        await External.WriteFitsFileAsync(image, fitsFilePath);

        _lastFramePath = fitsFilePath;
        return fitsFilePath;
    }

    /// <summary>
    /// Resolves the connected filter wheel (if any) and the list of filter positions to iterate for flats.
    /// Returns a single <c>-1</c> "no filter" pass when there is no connected wheel. Shared by the panel and
    /// twilight-sky flat paths.
    /// </summary>
    private static (IFilterWheelDriver? FilterWheel, int[] Positions) ResolveFilterPositions(OTA telescope)
    {
        var filterWheel = telescope.FilterWheel?.Driver is { Connected: true } fw ? fw : null;
        var filterCount = filterWheel?.Filters.Count ?? 0;
        return (filterWheel, filterCount > 0 ? Enumerable.Range(0, filterCount).ToArray() : [-1]);
    }

    /// <summary>
    /// Switches to <paramref name="position"/> (when a wheel is present) and stamps the filter / focuser
    /// denorm onto the camera so the FITS headers + output folder are correct -- the same helper the imaging
    /// loop, polar alignment and the preview button use. Returns the resulting filter name. Shared by the
    /// panel and twilight-sky flat paths.
    /// </summary>
    private async ValueTask<string> PrepareFilterForFlatsAsync(int otaIndex, OTA telescope, ICameraDriver camDriver, IFilterWheelDriver? filterWheel, int position, CancellationToken cancellationToken)
    {
        if (position >= 0 && filterWheel is not null)
        {
            await SwitchFilterIfNeededAsync(otaIndex, filterWheel, position, cancellationToken).ConfigureAwait(false);
        }

        await CameraExposureActions.StampDenormAsync(
            camDriver,
            otaName: telescope.Name,
            focalLengthMm: telescope.FocalLength,
            apertureMm: telescope.Aperture,
            focuser: telescope.Focuser?.Driver,
            filterWheel: filterWheel,
            logger: _logger,
            ct: cancellationToken).ConfigureAwait(false);

        return camDriver.Filter.Name;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : value > max ? max : value;
}
