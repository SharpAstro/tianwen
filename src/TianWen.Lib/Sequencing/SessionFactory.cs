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

    /// <remarks>
    /// The site is not settled here, since the factory never sees the mount's. A request that names none is
    /// reconciled with the profile's site, which rides on the <see cref="Setup"/>, once the run connects its
    /// mount (<c>Session.SettleSiteAsync</c>, #798). The profile-wins case used to be settled here, into the
    /// configuration, which could never see the mount-wins case of a mount with no site at all.
    /// </remarks>
    public ISession Create(Guid profileId, in SessionConfiguration configuration, ReadOnlySpan<ScheduledObservation> observations)
    {
        return new Session(CreateSetup(profileId), configuration, plateSolverFactory, external, serviceProvider, new ScheduledObservationTree(observations));
    }

    /// <summary>
    /// Builds the wrapper of one profile device, whose constructor builds the device's driver. A driver can refuse its own
    /// configuration (OpenWeatherMap with no key in the credential store throws <see cref="InvalidOperationException"/>
    /// saying what to do) or be one this computer has none of (an <see cref="ArgumentException"/>), and both are the
    /// PROFILE's fault, so they are refused in words naming the role and the device (#1076). Left to escape, the first
    /// answered a start with a bodiless 500 and the second with a 404 that reads as a missing profile.
    /// </summary>
    private static TWrapper Wrapped<TWrapper>(string role, DeviceBase device, Func<DeviceBase, TWrapper> build)
    {
        try
        {
            return build(device);
        }
        catch (InvalidOperationException ex)
        {
            throw new SessionRefusedException($"{role} {device.DisplayName} cannot be used: {ex.Message}");
        }
        catch (ArgumentException ex) when (ex is not SessionRefusedException)
        {
            throw new SessionRefusedException($"{role} {device.DisplayName} has no driver this computer can start.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The wrappers are IAsyncDisposable-only and this factory is synchronous, so disposing on "
            + "a mid-assembly throw would mean sync-over-async -- and there is nothing to dispose: a wrapper "
            + "holds no resource until its driver connects (see ControllableDeviceBase.DisposeAsync), a fresh "
            + "driver has not connected yet, and a borrowed driver stays the hub's, deliberately. Ownership "
            + "transfers to the returned Setup, which Session disposes.")]
    private Setup CreateSetup(Guid profileId)
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

        // A session always has a guider (#989), and a profile without one is refused here, the one point every
        // start goes through, before any wrapper is built: the Guider wrapper used to throw on a NoneDevice with
        // a message naming a blank device.
        var guiderDevice = HasGuider(profileData.Guider) ? DeviceFromUri(profileData.Guider) : null;
        if (guiderDevice is null or NoneDevice)
        {
            throw new SessionRefusedException(SessionRefusedException.NoGuiderMessage);
        }

        OTA? guiderIsOAGOfOTA = null;

        var telescopeCount = profileData.OTAs.Length;
        var otas = new List<OTA>(telescopeCount);
        for (var i = 0; i < telescopeCount; i++)
        {
            var otaData = profileData.OTAs[i];

            var camera = Wrapped("Camera", DeviceFromUri(otaData.Camera, i), d => new Camera(d, serviceProvider));
            var cover = otaData.Cover is { } coverUri ? Wrapped("Cover", DeviceFromUri(coverUri, i), d => new Cover(d, serviceProvider)) : null;
            var focuser = otaData.Focuser is { } focuserUri ? Wrapped("Focuser", DeviceFromUri(focuserUri, i), d => new Focuser(d, serviceProvider)) : null;
            var filterWheel = otaData.FilterWheel is { } filterWheelUri ? Wrapped("Filter wheel", DeviceFromUri(filterWheelUri, i), d => new FilterWheel(d, serviceProvider)) : null;

            var focusDirection = new FocusDirection(otaData.PreferOutwardFocus ?? true, otaData.OutwardIsPositive ?? true);
            var ota = new OTA(otaData.Name, otaData.FocalLength, camera, cover, focuser, focusDirection, filterWheel, Switches: null, otaData.Aperture, otaData.OpticalDesign);
            otas.Add(ota);

            if (profileData.OAG_OTA_Index == i)
            {
                guiderIsOAGOfOTA = ota;
            }
        }

        var mount = Wrapped("Mount", DeviceFromUri(profileData.Mount), d => new Mount(d, serviceProvider));
        var guider = Wrapped("Guider", guiderDevice, d => new Guider(d, serviceProvider));
        var guiderCamera = profileData.GuiderCamera is { } guiderCameraUri ? Wrapped("Guide camera", DeviceFromUri(guiderCameraUri), d => new Camera(d, serviceProvider)) : null;
        var guiderFocuser = profileData.GuiderFocuser is { } guiderFocuserUri ? Wrapped("Guide focuser", DeviceFromUri(guiderFocuserUri), d => new Focuser(d, serviceProvider)) : null;

        // Wire mount and camera into guiders that need device access.
        if (guider.Driver is IDeviceDependentGuider deviceDependentGuider)
        {
            deviceDependentGuider.LinkDevices(mount.Driver, guiderCamera?.Driver);
        }

        var guiderSetup = new GuiderSetup(guiderCamera, guiderFocuser, guiderIsOAGOfOTA, profileData.GuiderFocalLength);

        var weather = profileData.Weather is { } weatherUri ? Wrapped("Weather", DeviceFromUri(weatherUri), d => new Weather(d, serviceProvider)) : null;

        var setup = new Setup(mount, guider, guiderSetup, [.. otas], weather, profileData.MountLimits, profileData.Site, profileData.SiteTieBreaker);

        // Diagnostic: did the session reuse already-connected hub driver instances, or create fresh
        // ones? A fresh instance re-runs DoConnectDeviceAsync at InitialisationAsync, which for the fake
        // SkyWatcher re-homes the encoder ("mount reset to 0,0 at session start") and could equally
        // reset any device (camera cooling, focuser position, ...). Checked for EVERY device so a
        // borrow miss (URI-key mismatch with what the manual connect cached) is visible per-device.
        // borrowed=false while the user was already connected => the hub borrow missed for that device.
        void LogBorrow(string role, DeviceBase dev, bool borrowed) =>
            _logger.LogDebug("Session setup: {Role} {Uri} borrowed-from-hub={Borrowed}", role, dev.DeviceUri, borrowed);

        LogBorrow("mount", mount.Device, mount.Borrowed);
        LogBorrow("guider", guider.Device, guider.Borrowed);
        if (guiderCamera is not null) LogBorrow("guiderCamera", guiderCamera.Device, guiderCamera.Borrowed);
        if (guiderFocuser is not null) LogBorrow("guiderFocuser", guiderFocuser.Device, guiderFocuser.Borrowed);
        if (weather is not null) LogBorrow("weather", weather.Device, weather.Borrowed);
        for (var i = 0; i < otas.Count; i++)
        {
            var o = otas[i];
            LogBorrow($"ota{i}.camera", o.Camera.Device, o.Camera.Borrowed);
            if (o.Cover is { } cov) LogBorrow($"ota{i}.cover", cov.Device, cov.Borrowed);
            if (o.Focuser is { } foc) LogBorrow($"ota{i}.focuser", foc.Device, foc.Borrowed);
            if (o.FilterWheel is { } fw) LogBorrow($"ota{i}.filterWheel", fw.Device, fw.Borrowed);
        }

        return setup;

        // Absent (a profile file missing the slot deserialises it as null) or NoneDevice under any scheme,
        // Guider://NoneDevice/none as much as none://NoneDevice/None.
        static bool HasGuider([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Uri? uri)
            => uri is not null && !uri.Host.Equals(nameof(NoneDevice), StringComparison.OrdinalIgnoreCase);

        DeviceBase DeviceFromUri(Uri deviceUri, int? otaIdx = null)
        {
            // Reconcile stored URI with discovery: if the same device (by its DeviceKey) has been
            // discovered with a different query, e.g. DHCP reassigned an Alpaca host's IP, or a
            // mount whose id keeps the port out of its path (an LX200/OnStep UUID) moved COM5 → COM6,
            // adopt the discovered URI so transport state tracks hardware reality. A port-qualified
            // id (Skywatcher, SkyGuider Pro) is a different key on a new port and is not matched.
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
