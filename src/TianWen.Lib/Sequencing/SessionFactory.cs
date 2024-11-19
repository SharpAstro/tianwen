using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;

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

    public Session Create(Guid profileId, in SessionConfiguration configuration, IReadOnlyList<Observation> observations)
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
            otas[i] = new OTA(otaData.Name, otaData.FocalLength, camera, cover, focuser, focusDirection, filterWheel, null);   

            if (profileData.OAG_OTA_Index == i)
            {
                guiderIsOAGOfOTA = otas[i];
            }
        }

        var mount = new Mount(DeviceFromUri(profileData.Mount), external);
        var guider = new Guider(DeviceFromUri(profileData.Guider), external);
        var guiderFocuser = profileData.GuiderFocuser is { } guiderFocuserUri ? new Focuser(DeviceFromUri(guiderFocuserUri), external) : null;

        var guiderSetup = new GuiderSetup(guiderFocuser, guiderIsOAGOfOTA);

        var setup = new Setup(mount, guider, guiderSetup, otas);

        return new Session(setup, configuration, plateSolverFactory, external, observations);

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