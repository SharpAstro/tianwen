using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    /// <summary>
    /// On-demand flat-frame run (see <see cref="ISession.RunFlatsOnlyAsync"/>). Runs a self-contained
    /// connect -> cool -> capture -> finalise cycle against the flat-relevant device subset, without the
    /// wait-for-dark / focus / guider-calibration / observation-loop stages of <see cref="RunAsync"/>.
    /// Dispatches capture on <see cref="SessionConfiguration.FlatSource"/> exactly like the end-of-session
    /// hook (calibrator / manual panel / twilight sky), so the output contract is identical.
    /// </summary>
    public async Task RunFlatsOnlyAsync(TwilightPeriod skyFlatPeriod, CancellationToken cancellationToken)
    {
        // Sky-flats need the mount (slew to the anti-solar zenith, tracking off). Panel / manual flats do
        // not touch the mount and never connect the guider.
        var needMount = Configuration.FlatSource is FlatIlluminationSource.TwilightSky;
        var mountConnected = false;

        try
        {
            AllocateObservableState();

            SetPhase(SessionPhase.Initialising);
            if (!await ConnectForFlatsAsync(needMount, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogError("On-demand flat run: device connect failed, aborting.");
                SetPhase(SessionPhase.Failed);
                return;
            }
            mountConnected = needMount;

            await PollDeviceStatesAsync(cancellationToken).ConfigureAwait(false);

            // Cool to the imaging setpoint so on-demand flats match the light-frame temperature (a no-op on
            // cameras without a cooler). The finaliser warms back to ambient afterwards.
            SetPhase(SessionPhase.Cooling);
            await CoolCamerasToSetpointAsync(Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, SetupointDirection.Down, cancellationToken).ConfigureAwait(false);

            SetPhase(SessionPhase.Flats);
            if (Configuration.FlatSource is FlatIlluminationSource.TwilightSky)
            {
                // Direct call so the caller-chosen period is honoured (TakeFlatsAsync would default to Dawn).
                await TakeSkyFlatsAsync(skyFlatPeriod, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Calibrator + ManualPanel both flow through TakeFlatsAsync's dispatch.
                await TakeFlatsAsync(cancellationToken).ConfigureAwait(false);
            }

            SetPhase(SessionPhase.Complete);
        }
        catch (OperationCanceledException)
        {
            SetPhase(SessionPhase.Aborted);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception during on-demand flat run, aborting.");
            SetPhase(SessionPhase.Failed);
        }
        finally
        {
            // Finalise must complete even on abort -- CancellationToken.None so warming/closing isn't cut short.
            var terminalPhase = _phase;
            SetPhase(SessionPhase.Finalising);
            await FinaliseFlatsAsync(mountConnected, CancellationToken.None).ConfigureAwait(false);
            SetPhase(terminalPhase);
        }
    }

    /// <summary>
    /// Connects only the devices an on-demand flat run needs: every OTA's camera / focuser / filter wheel /
    /// cover (via the shared <see cref="ConnectTelescopeAsync"/>), plus the mount when
    /// <paramref name="needMount"/> (sky-flats). The guider is never connected. Confirms the cameras are at
    /// their sensor temperature before the caller ramps to setpoint. Returns false if a required device
    /// cannot be brought up.
    /// </summary>
    private async ValueTask<bool> ConnectForFlatsAsync(bool needMount, CancellationToken cancellationToken)
    {
        // Configured site drives the mount sync + per-camera denorm stamp; fall back to the mount's own site
        // when unset (the sky-flat solar-altitude gate + zenith slew also fall back to the mount).
        var siteLatitude = Configuration.SiteLatitude;
        var siteLongitude = Configuration.SiteLongitude;

        if (needMount)
        {
            var mount = Setup.Mount;
            _currentActivity = "Connecting mount\u2026";
            _logger.LogDebug("Flats: connecting mount {Mount}", mount);
            await mount.Driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await mount.Driver.SetUTCDateAsync(_timeProvider.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);

            if (!double.IsNaN(siteLatitude) && !double.IsNaN(siteLongitude))
            {
                await mount.Driver.SetSiteLatitudeAsync(siteLatitude, cancellationToken).ConfigureAwait(false);
                await mount.Driver.SetSiteLongitudeAsync(siteLongitude, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                siteLatitude = await _logger.CatchAsync(mount.Driver.GetSiteLatitudeAsync, cancellationToken, double.NaN).ConfigureAwait(false);
                siteLongitude = await _logger.CatchAsync(mount.Driver.GetSiteLongitudeAsync, cancellationToken, double.NaN).ConfigureAwait(false);
            }

            if (await mount.Driver.AtParkAsync(cancellationToken).ConfigureAwait(false)
                && (!mount.Driver.CanUnpark || !await CatchAsync(mount.Driver.UnparkAsync, cancellationToken).ConfigureAwait(false)))
            {
                _logger.LogError("Mount is parked and cannot be unparked; cannot take sky-flats.");
                return false;
            }
        }

        for (var i = 0; i < Setup.Telescopes.Length; i++)
        {
            await ConnectTelescopeAsync(Setup.Telescopes[i], i, siteLatitude, siteLongitude, cancellationToken).ConfigureAwait(false);
        }

        _currentActivity = "Checking cooler sensor temp\u2026";
        _logger.LogDebug("Flats: confirming camera cooler sensor temp");
        if (!await CoolCamerasToSensorTempAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
        {
            _logger.LogError("Failed to confirm camera cooler sensor temperature; aborting flat run.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Finaliser for the on-demand flat run: aborts any in-progress exposure, closes covers, warms the
    /// cameras back to ambient (when <see cref="SessionConfiguration.WarmCamerasOnSessionEnd"/>), parks +
    /// disconnects the mount when it was connected, and disconnects the OTA devices. A focused counterpart
    /// to <see cref="Finalise"/> -- it does not touch the guider (never connected on this path), so it
    /// avoids the full finaliser's spurious "partial shutdown" report for the devices a flat run never used.
    /// </summary>
    private async ValueTask FinaliseFlatsAsync(bool mountConnected, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Finalising flat run: abort exposures, close covers, warm cameras{Mount}, disconnect.",
            mountConnected ? ", park mount" : "");

        _currentActivity = "Aborting exposures\u2026";
        for (var i = 0; i < Setup.Telescopes.Length; i++)
        {
            var cam = Setup.Telescopes[i].Camera.Driver;
            if (await CatchAsync(cam.GetCameraStateAsync, cancellationToken) is CameraState.Exposing)
            {
                if (cam.CanAbortExposure)
                {
                    await CatchAsync(cam.AbortExposureAsync, cancellationToken).ConfigureAwait(false);
                }
                else if (cam.CanStopExposure)
                {
                    await CatchAsync(cam.StopExposureAsync, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        _currentActivity = "Closing covers\u2026";
        await CatchAsync(ct => MoveTelescopeCoversToStateAsync(CoverStatus.Closed, ct), cancellationToken, false).ConfigureAwait(false);

        if (Configuration.WarmCamerasOnSessionEnd)
        {
            _currentActivity = "Warming cameras\u2026";
            await CatchAsync(_ => CoolCamerasToAmbientAsync(Configuration.WarmupRampInterval), cancellationToken, false).ConfigureAwait(false);
        }

        if (mountConnected)
        {
            var mount = Setup.Mount;
            _currentActivity = "Parking mount\u2026";
            if (Catch(() => mount.Driver.CanPark) && await CatchAsync(mount.Driver.ParkAsync, cancellationToken).ConfigureAwait(false))
            {
                var guard = 0;
                while (!await CatchAsync(mount.Driver.AtParkAsync, cancellationToken).ConfigureAwait(false) && guard++ < IDeviceDriver.MAX_FAILSAFE)
                {
                    await _timeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }
            }
            await CatchAsync(mount.Driver.DisconnectAsync, cancellationToken).ConfigureAwait(false);
        }

        _currentActivity = "Disconnecting devices\u2026";
        for (var i = 0; i < Setup.Telescopes.Length; i++)
        {
            var telescope = Setup.Telescopes[i];
            await CatchAsync(telescope.Camera.Driver.DisconnectAsync, cancellationToken).ConfigureAwait(false);
            if (telescope.Cover?.Driver is { } cover)
            {
                await CatchAsync(cover.DisconnectAsync, cancellationToken).ConfigureAwait(false);
            }
            if (telescope.FilterWheel?.Driver is { } filterWheel)
            {
                await CatchAsync(filterWheel.DisconnectAsync, cancellationToken).ConfigureAwait(false);
            }
            if (telescope.Focuser?.Driver is { } focuser)
            {
                await CatchAsync(focuser.DisconnectAsync, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
