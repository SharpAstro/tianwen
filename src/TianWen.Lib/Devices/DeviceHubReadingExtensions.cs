using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Extensions;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Devices;

/// <summary>
/// Reading a connected device's state: the ONE sampler for every host that shows it, the GUI's Equipment and Live
/// Session tabs and a node's device plane (P2 of docs/plans/hardware-in-the-server.md, #929), so the two can never
/// disagree about what a camera or a mount reads.
/// </summary>
/// <remarks>
/// Each read is best-effort: a member the driver cannot answer, or answers with an error, reads as not known (NaN)
/// rather than failing the whole reading, since a camera whose heat-sink sensor faults still has a cooler worth
/// showing. A device that is not connected reads as <see langword="null"/>. These read the driver and nothing
/// else, so they take no lease: reads are never leased, and watching a rig must cost it nothing.
/// </remarks>
public static class DeviceHubReadingExtensions
{
    extension(IDeviceHub hub)
    {
        /// <summary>The camera at <paramref name="cameraUri"/> as it reads now, or null when it is not connected.</summary>
        public async ValueTask<CameraReading?> ReadCameraAsync(Uri cameraUri, ILogger logger, CancellationToken cancellationToken)
        {
            if (!hub.TryGetConnectedDriver<ICameraDriver>(cameraUri, out var camera))
            {
                return null;
            }

            var ccd = await logger.CatchAsyncIf(camera.CanGetCCDTemperature, camera.GetCCDTemperatureAsync, cancellationToken, double.NaN);
            var heatsink = await logger.CatchAsyncIf(camera.CanGetHeatsinkTemperature, camera.GetHeatSinkTemperatureAsync, cancellationToken, double.NaN);
            var setpoint = await logger.CatchAsyncIf(camera.CanSetCCDTemperature, camera.GetSetCCDTemperatureAsync, cancellationToken, double.NaN);
            var power = await logger.CatchAsyncIf(camera.CanGetCoolerPower, camera.GetCoolerPowerAsync, cancellationToken, double.NaN);
            var coolerOn = await logger.CatchAsyncIf(camera.CanGetCoolerOn, camera.GetCoolerOnAsync, cancellationToken);
            var state = await logger.CatchAsync(camera.GetCameraStateAsync, cancellationToken, CameraState.NotConnected);
            var gain = await logger.CatchAsync(camera.GetGainAsync, cancellationToken);
            var gainModes = camera.UsesGainMode && camera.Gains is { Count: > 0 } gains ? [.. gains] : ImmutableArray<string>.Empty;

            return new CameraReading(
                ccd, heatsink, setpoint, power, coolerOn, state,
                camera.UsesGainValue, camera.UsesGainMode, camera.GainMin, camera.GainMax, gain, gainModes,
                camera.CameraXSize, camera.CameraYSize, camera.RoiConstraints);
        }

        /// <summary>The focuser at <paramref name="focuserUri"/> as it reads now, or null when it is not connected.</summary>
        public async ValueTask<FocuserReading?> ReadFocuserAsync(Uri focuserUri, ILogger logger, CancellationToken cancellationToken)
        {
            if (!hub.TryGetConnectedDriver<IFocuserDriver>(focuserUri, out var focuser))
            {
                return null;
            }

            var position = await logger.CatchAsync(focuser.GetPositionAsync, cancellationToken);
            var temperature = await logger.CatchAsync(focuser.GetTemperatureAsync, cancellationToken, double.NaN);
            var moving = await logger.CatchAsync(focuser.GetIsMovingAsync, cancellationToken);
            return new FocuserReading(position, temperature, moving);
        }

        /// <summary>The filter wheel at <paramref name="filterWheelUri"/> as it reads now, or null when it is not connected.</summary>
        public async ValueTask<FilterWheelReading?> ReadFilterWheelAsync(Uri filterWheelUri, ILogger logger, CancellationToken cancellationToken)
        {
            if (!hub.TryGetConnectedDriver<IFilterWheelDriver>(filterWheelUri, out var filterWheel))
            {
                return null;
            }

            var filter = await logger.CatchAsync(filterWheel.GetCurrentFilterAsync, cancellationToken);
            return new FilterWheelReading(filter.Position, filter.DisplayName);
        }

        /// <summary>
        /// The cover or flat panel at <paramref name="coverUri"/> as it reads now, or null when it is not connected. A
        /// device with no light is not asked its brightness, which it could only answer with an error.
        /// </summary>
        public async ValueTask<CoverReading?> ReadCoverAsync(Uri coverUri, ILogger logger, CancellationToken cancellationToken)
        {
            if (!hub.TryGetConnectedDriver<ICoverDriver>(coverUri, out var cover))
            {
                return null;
            }

            var coverState = await logger.CatchAsync(cover.GetCoverStateAsync, cancellationToken, CoverStatus.Unknown);
            var calibratorState = await logger.CatchAsync(cover.GetCalibratorStateAsync, cancellationToken, CalibratorStatus.Unknown);
            var brightness = await logger.CatchAsyncIf(calibratorState is not CalibratorStatus.NotPresent, cover.GetBrightnessAsync, cancellationToken, -1);
            return new CoverReading(coverState, calibratorState, brightness, cover.MaxBrightness, cover.CanControlBrightness);
        }

        /// <summary>
        /// The mount at <paramref name="mountUri"/> as it reads now, or null when it is not connected. A failed read
        /// is NaN, never 0: a mount at RA 0, Dec 0 is a real pointing, and painting a failed read there is how the
        /// reticle once jumped to the celestial origin.
        /// </summary>
        /// <param name="toJ2000">
        /// For a mount that reports TOPOCENTRIC coordinates, makes a transform at the mount's site to take them to
        /// J2000 by, or null when it cannot (the J2000 pair is then unknown). Asked only for such a mount: a J2000
        /// mount needs none, and a caller that cannot make one should not be warning about it for a mount that never
        /// asked.
        /// </param>
        public async ValueTask<MountState?> ReadMountAsync(Uri mountUri, Func<Transform?>? toJ2000, ILogger logger, CancellationToken cancellationToken)
        {
            if (!hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mount))
            {
                return null;
            }

            var ra = await logger.CatchAsync(mount.GetRightAscensionAsync, cancellationToken, double.NaN);
            var dec = await logger.CatchAsync(mount.GetDeclinationAsync, cancellationToken, double.NaN);
            var ha = await logger.CatchAsync(mount.GetHourAngleAsync, cancellationToken, double.NaN);
            var slewing = await logger.CatchAsync(mount.IsSlewingAsync, cancellationToken);
            var tracking = await logger.CatchAsync(mount.IsTrackingAsync, cancellationToken);
            var pier = await logger.CatchAsync(mount.GetSideOfPierAsync, cancellationToken);

            var (raJ2000, decJ2000) = (double.NaN, double.NaN);
            if (!double.IsNaN(ra) && !double.IsNaN(dec))
            {
                if (mount.EquatorialSystem == EquatorialCoordinateType.J2000)
                {
                    (raJ2000, decJ2000) = (ra, dec);
                }
                else if (mount.EquatorialSystem == EquatorialCoordinateType.Topocentric && toJ2000?.Invoke() is { } transform)
                {
                    transform.SetTopocentric(ra, dec);
                    transform.Refresh();
                    (raJ2000, decJ2000) = (transform.RAJ2000, transform.DecJ2000);
                }
            }

            return new MountState(ra, dec, ha, pier, slewing, tracking, raJ2000, decJ2000);
        }
    }
}
