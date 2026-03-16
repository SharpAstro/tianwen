using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.SOFA;
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

    public ISession Create(Guid profileId, in SessionConfiguration configuration, ReadOnlySpan<ProposedObservation> proposals)
    {
        var (setup, profileData) = CreateSetup(profileId);

        // Construct Transform from mount URI query params
        var mountQuery = setup.Mount.Device.Query;
        if (!double.TryParse(mountQuery.QueryValue(DeviceQueryKey.Latitude), CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(mountQuery.QueryValue(DeviceQueryKey.Longitude), CultureInfo.InvariantCulture, out var longitude))
        {
            throw new InvalidOperationException("Mount device URI must contain latitude and longitude query parameters for scheduling.");
        }

        var now = external.TimeProvider.GetUtcNow();
        var transform = new Transform(external.TimeProvider)
        {
            SiteLatitude = latitude,
            SiteLongitude = longitude,
            SiteElevation = 0,
            SiteTemperature = 15,
            DateTimeOffset = now
        };

        // Resolve default gain/offset from first OTA camera URI
        var defaultGain = 0;
        var defaultOffset = 0;
        if (profileData.OTAs.Length > 0)
        {
            var cameraUri = profileData.OTAs[0].Camera;
            defaultGain = ObservationScheduler.ResolveGain(null, cameraUri, 0, 0, false);
            defaultOffset = ObservationScheduler.ResolveOffset(null, cameraUri);
        }

        var defaultSubExposure = configuration.DefaultSubExposure ?? TimeSpan.FromSeconds(120);
        var defaultObservationTime = TimeSpan.FromMinutes(30);

        var tree = ObservationScheduler.Schedule(
            proposals,
            transform,
            configuration.MinHeightAboveHorizon,
            defaultGain,
            defaultOffset,
            defaultSubExposure,
            defaultObservationTime
        );

        return new Session(setup, configuration, plateSolverFactory, external, tree);
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
            var ota = new OTA(otaData.Name, otaData.FocalLength, camera, cover, focuser, focusDirection, filterWheel, null);
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
