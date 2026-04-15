using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;

namespace TianWen.Lib.Sequencing;

internal class SessionFactory(
    IDeviceHub deviceHub,
    IDeviceDiscovery deviceDiscovery,
    IExternal external,
    IPlateSolverFactory plateSolverFactory,
    IServiceProvider serviceProvider
) : ISessionFactory
{
    private readonly ILogger _logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SessionFactory>();

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!await plateSolverFactory.CheckSupportAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Failed to initalize due to plate solver factory error.");
        }
        await deviceDiscovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
    }

    public ISession Create(Guid profileId, in SessionConfiguration configuration, ReadOnlySpan<ScheduledObservation> observations)
    {
        var (setup, _) = CreateSetup(profileId);

        return new Session(setup, configuration, plateSolverFactory, external, serviceProvider, new ScheduledObservationTree(observations));
    }



    private (Setup Setup, ProfileData ProfileData) CreateSetup(Guid profileId)
    {
        var profileDeviceId = Profile.DeviceIdFromUUID(profileId);
        if (deviceDiscovery.RegisteredDevices(DeviceType.Profile).FirstOrDefault(p => p.DeviceId == profileDeviceId) is not Profile profile)
        {
            throw new ArgumentException($"Cannot find a profile with id {profileId}", nameof(profileId));
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

            var camera = new Camera(DeviceFromUri(otaData.Camera, i), serviceProvider);
            var cover = otaData.Cover is { } coverUri ? new Cover(DeviceFromUri(coverUri, i), serviceProvider) : null;
            var focuser = otaData.Focuser is { } focuserUri ? new Focuser(DeviceFromUri(focuserUri, i), serviceProvider) : null;
            var filterWheel = otaData.FilterWheel is { } filterWheelUri ? new FilterWheel(DeviceFromUri(filterWheelUri, i), serviceProvider) : null;

            var focusDirection = new FocusDirection(otaData.PreferOutwardFocus ?? true, otaData.OutwardIsPositive ?? true);
            var ota = new OTA(otaData.Name, otaData.FocalLength, camera, cover, focuser, focusDirection, filterWheel, Switches: null, otaData.Aperture, otaData.OpticalDesign);
            otas.Add(ota);

            if (profileData.OAG_OTA_Index == i)
            {
                guiderIsOAGOfOTA = ota;
            }
        }

        var mount = new Mount(DeviceFromUri(profileData.Mount), serviceProvider);
        var guider = new Guider(DeviceFromUri(profileData.Guider), serviceProvider);
        var guiderCamera = profileData.GuiderCamera is { } guiderCameraUri ? new Camera(DeviceFromUri(guiderCameraUri), serviceProvider) : null;
        var guiderFocuser = profileData.GuiderFocuser is { } guiderFocuserUri ? new Focuser(DeviceFromUri(guiderFocuserUri), serviceProvider) : null;

        // Wire mount and camera into guiders that need device access.
        if (guider.Driver is IDeviceDependentGuider deviceDependentGuider)
        {
            deviceDependentGuider.LinkDevices(mount.Driver, guiderCamera?.Driver);
        }

        var guiderSetup = new GuiderSetup(guiderCamera, guiderFocuser, guiderIsOAGOfOTA, profileData.GuiderFocalLength);

        var weather = profileData.Weather is { } weatherUri ? new Weather(DeviceFromUri(weatherUri), serviceProvider) : null;

        var setup = new Setup(mount, guider, guiderSetup, [.. otas], weather);

        return (setup, profileData);

        DeviceBase DeviceFromUri(Uri deviceUri, int? otaIdx = null)
        {
            // Reconcile stored URI with discovery: if the same device (by scheme+authority+path,
            // i.e. matching stable deviceId) has been discovered with a different query — e.g.
            // the mount was re-plugged and moved from COM5 → COM6, or DHCP reassigned the WiFi IP —
            // adopt the discovered URI so transport state tracks hardware reality.
            var resolvedUri = deviceDiscovery.ReconcileUri(deviceUri);
            if (resolvedUri != deviceUri)
            {
                _logger.LogInformation(
                    "Auto-adopting rediscovered URI for profile device: {StoredUri} -> {LiveUri}",
                    deviceUri, resolvedUri);
            }

            if (deviceHub.TryGetDeviceFromUri(resolvedUri, out var device))
            {
                return device;
            }
            else
            {
                throw new ArgumentException($"Profile {profileId}{(otaIdx.HasValue ? $" OTA #{otaIdx + 1}" : "")} device failed to instantiate from {resolvedUri}", nameof(profileId));
            }
        }
    }
}
