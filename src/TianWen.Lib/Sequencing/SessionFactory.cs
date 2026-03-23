using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;

namespace TianWen.Lib.Sequencing;

internal class SessionFactory(
    IDeviceUriRegistry deviceUriRegistry,
    ICombinedDeviceManager deviceManager,
    IExternal external,
    IPlateSolverFactory plateSolverFactory
) : ISessionFactory
{
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!await plateSolverFactory.CheckSupportAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Failed to initalize due to plate solver factory error.");
        }
        await deviceManager.DiscoverAsync(cancellationToken).ConfigureAwait(false);
    }

    public ISession Create(Guid profileId, in SessionConfiguration configuration, ReadOnlySpan<ScheduledObservation> observations)
    {
        var (setup, _) = CreateSetup(profileId);

        return new Session(setup, configuration, plateSolverFactory, external, new ScheduledObservationTree(observations));
    }



    private (Setup Setup, ProfileData ProfileData) CreateSetup(Guid profileId)
    {
        if (!deviceManager.TryFindByDeviceId(Profile.DeviceIdFromUUID(profileId), out var profileDevice))
        {
            throw new ArgumentException($"Cannot find a profile with id {profileId}", nameof(profileId));
        }

        if (profileDevice is not Profile profile)
        {
            throw new ArgumentException($"Device {profileDevice} is not a {DeviceType.Profile}", nameof(profileId));
        }

        var profileData = profile.Data ?? throw new ArgumentException($"Profile {profileId} contains no devices", nameof(profileId));

        if (profileData.OTAs.IsDefaultOrEmpty)
        {
            throw new ArgumentException($"Profile {profileId} must contain at least one OTA", nameof(profileId));
        }

        OTA? guiderIsOAGOfOTA = null;

        var telescopeCount = profileData.OTAs.Length;
        var otas = new List<OTA>(telescopeCount);
        for (var i = 0; i < telescopeCount; i++)
        {
            var otaData = profileData.OTAs[i];

            var camera = new Camera(DeviceFromUri(otaData.Camera, i), external);
            var cover = otaData.Cover is { } coverUri ? new Cover(DeviceFromUri(coverUri, i), external) : null;
            var focuser = otaData.Focuser is { } focuserUri ? new Focuser(DeviceFromUri(focuserUri, i), external) : null;
            var filterWheel = otaData.FilterWheel is { } filterWheelUri ? new FilterWheel(DeviceFromUri(filterWheelUri, i), external) : null;

            var focusDirection = new FocusDirection(otaData.PreferOutwardFocus ?? true, otaData.OutwardIsPositive ?? true);
            var ota = new OTA(otaData.Name, otaData.FocalLength, camera, cover, focuser, focusDirection, filterWheel, Switches: null, otaData.Aperture, otaData.OpticalDesign);
            otas.Add(ota);

            if (profileData.OAG_OTA_Index == i)
            {
                guiderIsOAGOfOTA = ota;
            }
        }

        var mount = new Mount(DeviceFromUri(profileData.Mount), external);
        var guider = new Guider(DeviceFromUri(profileData.Guider), external);
        var guiderCamera = profileData.GuiderCamera is { } guiderCameraUri ? new Camera(DeviceFromUri(guiderCameraUri), external) : null;
        var guiderFocuser = profileData.GuiderFocuser is { } guiderFocuserUri ? new Focuser(DeviceFromUri(guiderFocuserUri), external) : null;

        // Wire mount and camera into built-in guiders that need them for pulse guide corrections.
        if (guider.Driver is IDeviceDependentGuider deviceDependentGuider)
        {
            var guideCamera = guiderCamera?.Driver
                ?? throw new InvalidOperationException("Built-in guider requires a dedicated guider camera.");
            deviceDependentGuider.LinkDevices(mount.Driver, guideCamera);
        }

        var guiderSetup = new GuiderSetup(guiderCamera, guiderFocuser, guiderIsOAGOfOTA);

        var setup = new Setup(mount, guider, guiderSetup, [.. otas]);

        return (setup, profileData);

        DeviceBase DeviceFromUri(Uri deviceUri, int? otaIdx = null)
        {
            if (deviceUriRegistry.TryGetDeviceFromUri(deviceUri, out var device))
            {
                return device;
            }
            else
            {
                throw new ArgumentException($"Profile {profileId}{(otaIdx.HasValue ? $" OTA #{otaIdx + 1}" : "")} device failed to instantiate from {deviceUri}", nameof(profileId));
            }
        }
    }
}
