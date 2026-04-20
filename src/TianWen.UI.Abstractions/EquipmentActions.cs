using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Canon;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Devices.Weather;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Pure functions for profile/equipment manipulation. Shared between CLI and GUI.
/// All methods return new ProfileData (immutable record with-expressions).
/// </summary>
public static class EquipmentActions
{
    /// <summary>
    /// Common filter names for the equipment tab dropdown and CLI.
    /// </summary>
    public static readonly ImmutableArray<string> CommonFilterNames =
    [
        "Luminance", "Red", "Green", "Blue",
        "H-Alpha", "OIII", "SII", "H-Beta",
        "H-Alpha + OIII"
    ];

    /// <summary>
    /// Returns the display-friendly name for a filter.
    /// </summary>
    public static string FilterDisplayName(InstalledFilter filter) => filter.DisplayName;

    public static async Task<Profile> CreateProfileAsync(string name, IExternal external, CancellationToken ct)
    {
        var profile = new Profile(Guid.NewGuid(), name, ProfileData.Empty);
        await profile.SaveAsync(external, ct);
        return profile;
    }

    /// <summary>
    /// Reconciles every registered profile against the current discovery cache and
    /// persists the ones whose device URIs drifted (COM5 -> COM6, new DHCP IP, etc.).
    /// Returns the (original, updated) pairs for each profile that actually changed,
    /// so the caller can decide which to reflect into UI state without having to
    /// re-run the comparison.
    /// </summary>
    public static async Task<IReadOnlyList<(Profile Original, Profile Updated)>> ReconcileAllProfilesAsync(
        IDeviceDiscovery discovery, IExternal external, CancellationToken ct)
    {
        var changes = new List<(Profile, Profile)>();
        foreach (var p in discovery.RegisteredDevices(DeviceType.Profile).OfType<Profile>())
        {
            if (p.Data is not { } data) continue;

            var (reconciled, changed) = discovery.ReconcileProfileData(data);
            if (!changed) continue;

            var updated = p.WithData(reconciled);
            await updated.SaveAsync(external, ct);
            changes.Add((p, updated));
        }
        return changes;
    }

    public static ProfileData AssignMount(ProfileData data, Uri mountUri)
        => data with { Mount = mountUri };

    public static ProfileData AssignGuider(ProfileData data, Uri guiderUri)
        => data with { Guider = guiderUri };

    public static ProfileData AssignGuiderCamera(ProfileData data, Uri cameraUri)
        => data with { GuiderCamera = cameraUri };

    public static ProfileData AssignGuiderFocuser(ProfileData data, Uri focuserUri)
        => data with { GuiderFocuser = focuserUri };

    public static ProfileData AssignWeather(ProfileData data, Uri weatherUri)
        => data with { Weather = weatherUri };

    public static ProfileData SetOagOtaIndex(ProfileData data, int otaIndex)
        => data with { OAG_OTA_Index = otaIndex };

    public static ProfileData SetSite(ProfileData data, double lat, double lon, double? elevation = null)
        => data with { SiteLatitude = lat, SiteLongitude = lon, SiteElevation = elevation };

    public static ProfileData SetSiteTieBreaker(ProfileData data, SiteTieBreaker tieBreaker)
        => data with { SiteTieBreaker = tieBreaker };

    /// <summary>
    /// Outcome of <see cref="ReconcileSiteOnMountConnectAsync"/>.
    /// <paramref name="Data"/> is the (possibly updated) <see cref="ProfileData"/>.
    /// <paramref name="ProfileChanged"/> is true when the caller should persist the profile.
    /// <paramref name="MountPushed"/> is true when the mount hardware was updated from the profile.
    /// <paramref name="WinnerSource"/> describes where the effective site came from
    /// for logging purposes; null when no site was available anywhere.
    /// </summary>
    public readonly record struct SiteReconcileResult(
        ProfileData Data,
        bool ProfileChanged,
        bool MountPushed,
        string? WinnerSource);

    /// <summary>
    /// Reconcile site coordinates between the connected mount hardware and the
    /// stored profile. The tie-breaker (<see cref="ProfileData.SiteTieBreaker"/>)
    /// only matters when both sides report a value and they differ:
    /// the winner's site is written to the loser (profile → persisted,
    /// mount → pushed via SetSite*Async). When only one side has a value the
    /// other side is populated unconditionally.
    /// </summary>
    public static async ValueTask<SiteReconcileResult> ReconcileSiteOnMountConnectAsync(
        ProfileData data,
        TianWen.Lib.Devices.IMountDriver mount,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        // Mount returns NaN (Skywatcher/iOptron default) when site has never been
        // pushed and the mount doesn't report one. ASCOM drivers typically return 0.
        var mountLatRaw = await mount.GetSiteLatitudeAsync(cancellationToken);
        var mountLonRaw = await mount.GetSiteLongitudeAsync(cancellationToken);
        var mountElevRaw = await mount.GetSiteElevationAsync(cancellationToken);
        var mountHas = !double.IsNaN(mountLatRaw) && !double.IsNaN(mountLonRaw)
                       && !(mountLatRaw == 0 && mountLonRaw == 0);
        var profileHas = data.SiteLatitude is not null && data.SiteLongitude is not null;

        if (!mountHas && !profileHas)
        {
            return new SiteReconcileResult(data, false, false, null);
        }

        if (mountHas && !profileHas)
        {
            logger?.LogInformation("Site reconcile: mount reports {Lat}/{Lon} (profile empty) — adopting into profile.",
                mountLatRaw, mountLonRaw);
            double? elev = double.IsNaN(mountElevRaw) ? null : mountElevRaw;
            var updated = data with { SiteLatitude = mountLatRaw, SiteLongitude = mountLonRaw, SiteElevation = elev };
            return new SiteReconcileResult(updated, ProfileChanged: true, MountPushed: false, WinnerSource: "mount");
        }

        if (profileHas && !mountHas)
        {
            logger?.LogInformation("Site reconcile: profile has {Lat}/{Lon} (mount empty) — pushing to mount.",
                data.SiteLatitude, data.SiteLongitude);
            await mount.SetSiteLatitudeAsync(data.SiteLatitude!.Value, cancellationToken);
            await mount.SetSiteLongitudeAsync(data.SiteLongitude!.Value, cancellationToken);
            if (data.SiteElevation is { } elevation)
            {
                await mount.SetSiteElevationAsync(elevation, cancellationToken);
            }
            return new SiteReconcileResult(data, ProfileChanged: false, MountPushed: true, WinnerSource: "profile");
        }

        // Both sides have a site. Apply the tie-breaker.
        var winner = data.SiteTieBreaker;
        if (winner == SiteTieBreaker.Mount)
        {
            double? mountElev = double.IsNaN(mountElevRaw) ? null : mountElevRaw;
            if (data.SiteLatitude != mountLatRaw || data.SiteLongitude != mountLonRaw || data.SiteElevation != mountElev)
            {
                logger?.LogInformation("Site reconcile (tie=Mount): mount {MLat}/{MLon} replaces profile {PLat}/{PLon}.",
                    mountLatRaw, mountLonRaw, data.SiteLatitude, data.SiteLongitude);
                var updated = data with { SiteLatitude = mountLatRaw, SiteLongitude = mountLonRaw, SiteElevation = mountElev };
                return new SiteReconcileResult(updated, ProfileChanged: true, MountPushed: false, WinnerSource: "mount");
            }
            return new SiteReconcileResult(data, false, false, WinnerSource: "mount");
        }
        else
        {
            if (data.SiteLatitude != mountLatRaw || data.SiteLongitude != mountLonRaw
                || (data.SiteElevation is { } pe && !double.IsNaN(mountElevRaw) && pe != mountElevRaw))
            {
                logger?.LogInformation("Site reconcile (tie=Profile): profile {PLat}/{PLon} pushed to mount (was {MLat}/{MLon}).",
                    data.SiteLatitude, data.SiteLongitude, mountLatRaw, mountLonRaw);
                await mount.SetSiteLatitudeAsync(data.SiteLatitude!.Value, cancellationToken);
                await mount.SetSiteLongitudeAsync(data.SiteLongitude!.Value, cancellationToken);
                if (data.SiteElevation is { } elevation)
                {
                    await mount.SetSiteElevationAsync(elevation, cancellationToken);
                }
                return new SiteReconcileResult(data, ProfileChanged: false, MountPushed: true, WinnerSource: "profile");
            }
            return new SiteReconcileResult(data, false, false, WinnerSource: "profile");
        }
    }

    /// <summary>
    /// One-shot migration of site coordinates from the legacy Mount URI query
    /// string (<c>?latitude=…&amp;longitude=…&amp;elevation=…</c>) into
    /// <see cref="ProfileData.SiteLatitude"/> etc. Returns the updated
    /// <see cref="ProfileData"/> and a flag indicating whether anything changed.
    /// When the profile already has <see cref="ProfileData.SiteLatitude"/> set
    /// the URI query is ignored — profile wins for migration.
    /// </summary>
    public static (ProfileData Data, bool Changed) MigrateSiteFromMountUri(ProfileData data)
    {
        if (data.SiteLatitude is not null || data.SiteLongitude is not null) return (data, false);
        if (data.Mount == NoneDevice.Instance.DeviceUri) return (data, false);

        var query = HttpUtility.ParseQueryString(data.Mount.Query);
        var latStr = query[DeviceQueryKey.Latitude.Key];
        var lonStr = query[DeviceQueryKey.Longitude.Key];
        var elevStr = query[DeviceQueryKey.Elevation.Key];

        if (latStr is null || lonStr is null
            || !double.TryParse(latStr, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(lonStr, CultureInfo.InvariantCulture, out var lon))
        {
            return (data, false);
        }

        double? elev = elevStr is not null && double.TryParse(elevStr, CultureInfo.InvariantCulture, out var e) ? e : null;
        return (data with { SiteLatitude = lat, SiteLongitude = lon, SiteElevation = elev }, true);
    }

    /// <summary>
    /// Produce a human-readable diff of two <see cref="ProfileData"/> values,
    /// yielding one tuple per field that changed. Used for logging post-discovery
    /// reconciles so transport refreshes and user-config clobbers are both visible.
    /// Returns (field label, before-value, after-value) as strings; null URIs
    /// render as "<none>".
    /// </summary>
    public static IEnumerable<(string Field, string Before, string After)> DiffProfileData(ProfileData before, ProfileData after)
    {
        static string F(Uri? u) => u?.ToString() ?? "<none>";

        if (before.Mount != after.Mount)
            yield return ("Mount", F(before.Mount), F(after.Mount));
        if (before.Guider != after.Guider)
            yield return ("Guider", F(before.Guider), F(after.Guider));
        if (before.GuiderCamera != after.GuiderCamera)
            yield return ("GuiderCamera", F(before.GuiderCamera), F(after.GuiderCamera));
        if (before.GuiderFocuser != after.GuiderFocuser)
            yield return ("GuiderFocuser", F(before.GuiderFocuser), F(after.GuiderFocuser));
        if (before.Weather != after.Weather)
            yield return ("Weather", F(before.Weather), F(after.Weather));

        var maxOtas = Math.Max(before.OTAs.Length, after.OTAs.Length);
        for (int i = 0; i < maxOtas; i++)
        {
            var b = i < before.OTAs.Length ? (OTAData?)before.OTAs[i] : null;
            var a = i < after.OTAs.Length ? (OTAData?)after.OTAs[i] : null;
            if (b is null && a is not null) yield return ($"OTA[{i}]", "<none>", "<added>");
            else if (a is null && b is not null) yield return ($"OTA[{i}]", "<present>", "<removed>");
            else if (b is { } bb && a is { } aa)
            {
                if (bb.Camera != aa.Camera) yield return ($"OTA[{i}].Camera", F(bb.Camera), F(aa.Camera));
                if (bb.Cover != aa.Cover) yield return ($"OTA[{i}].Cover", F(bb.Cover), F(aa.Cover));
                if (bb.Focuser != aa.Focuser) yield return ($"OTA[{i}].Focuser", F(bb.Focuser), F(aa.Focuser));
                if (bb.FilterWheel != aa.FilterWheel) yield return ($"OTA[{i}].FilterWheel", F(bb.FilterWheel), F(aa.FilterWheel));
            }
        }
    }

    public static ProfileData AddOTA(ProfileData data, OTAData ota)
        => data with { OTAs = data.OTAs.Add(ota) };

    public static ProfileData RemoveOTA(ProfileData data, int index)
        => index >= 0 && index < data.OTAs.Length
            ? data with { OTAs = data.OTAs.RemoveAt(index) }
            : data;

    public static ProfileData AssignDeviceToOTA(ProfileData data, int otaIndex, DeviceType deviceType, Uri deviceUri)
    {
        if (otaIndex < 0 || otaIndex >= data.OTAs.Length)
        {
            return data;
        }

        var ota = data.OTAs[otaIndex];
        var updated = deviceType switch
        {
            DeviceType.Camera => ota with { Camera = deviceUri },
            DeviceType.Focuser => ota with { Focuser = deviceUri },
            DeviceType.FilterWheel => ota with { FilterWheel = deviceUri },
            DeviceType.CoverCalibrator => ota with { Cover = deviceUri },
            _ => ota
        };

        return data with { OTAs = data.OTAs.SetItem(otaIndex, updated) };
    }

    /// <summary>
    /// Checks if a device URI is assigned anywhere in the profile.
    /// </summary>
    public static bool IsDeviceAssigned(ProfileData data, Uri deviceUri)
    {
        if (DeviceBase.SameDevice(data.Mount, deviceUri) || DeviceBase.SameDevice(data.Guider, deviceUri))
        {
            return true;
        }
        if (DeviceBase.SameDevice(data.GuiderCamera, deviceUri) || DeviceBase.SameDevice(data.GuiderFocuser, deviceUri))
        {
            return true;
        }
        if (DeviceBase.SameDevice(data.Weather, deviceUri))
        {
            return true;
        }

        foreach (var ota in data.OTAs)
        {
            if (DeviceBase.SameDevice(ota.Camera, deviceUri) || DeviceBase.SameDevice(ota.Focuser, deviceUri) ||
                DeviceBase.SameDevice(ota.FilterWheel, deviceUri) || DeviceBase.SameDevice(ota.Cover, deviceUri))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes a device URI from all slots in the profile (mount, guider, OTAs, etc.).
    /// Call before assigning the device to a new slot to prevent duplicates.
    /// </summary>
    public static ProfileData UnassignDevice(ProfileData data, Uri deviceUri)
    {
        var none = NoneDevice.Instance.DeviceUri;

        if (DeviceBase.SameDevice(data.Mount, deviceUri))
        {
            // Preserve site query params when clearing mount
            var builder = new UriBuilder(none) { Query = data.Mount!.Query };
            data = data with { Mount = builder.Uri };
        }
        if (DeviceBase.SameDevice(data.Guider, deviceUri))
        {
            data = data with { Guider = none };
        }
        if (DeviceBase.SameDevice(data.GuiderCamera, deviceUri))
        {
            data = data with { GuiderCamera = null };
        }
        if (DeviceBase.SameDevice(data.GuiderFocuser, deviceUri))
        {
            data = data with { GuiderFocuser = null };
        }
        if (DeviceBase.SameDevice(data.Weather, deviceUri))
        {
            data = data with { Weather = null };
        }

        for (var i = 0; i < data.OTAs.Length; i++)
        {
            var ota = data.OTAs[i];
            var changed = false;

            if (DeviceBase.SameDevice(ota.Camera, deviceUri)) { ota = ota with { Camera = none }; changed = true; }
            if (DeviceBase.SameDevice(ota.Focuser, deviceUri)) { ota = ota with { Focuser = null }; changed = true; }
            if (DeviceBase.SameDevice(ota.FilterWheel, deviceUri)) { ota = ota with { FilterWheel = null }; changed = true; }
            if (DeviceBase.SameDevice(ota.Cover, deviceUri)) { ota = ota with { Cover = null }; changed = true; }

            if (changed)
            {
                data = data with { OTAs = data.OTAs.SetItem(i, ota) };
            }
        }

        return data;
    }

    /// <summary>
    /// Returns the device URI currently assigned to the given slot, or null.
    /// </summary>
    public static Uri? GetAssignedDevice(ProfileData data, AssignTarget slot)
    {
        var uri = slot switch
        {
            AssignTarget.ProfileLevel { Field: "Mount" } => data.Mount,
            AssignTarget.ProfileLevel { Field: "Guider" } => data.Guider,
            AssignTarget.ProfileLevel { Field: "GuiderCamera" } => data.GuiderCamera,
            AssignTarget.ProfileLevel { Field: "GuiderFocuser" } => data.GuiderFocuser,
            AssignTarget.ProfileLevel { Field: "Weather" } => data.Weather,
            AssignTarget.OTALevel { OtaIndex: var idx, Field: "Camera" } when idx >= 0 && idx < data.OTAs.Length
                => data.OTAs[idx].Camera,
            AssignTarget.OTALevel { OtaIndex: var idx, Field: "Focuser" } when idx >= 0 && idx < data.OTAs.Length
                => data.OTAs[idx].Focuser,
            AssignTarget.OTALevel { OtaIndex: var idx, Field: "FilterWheel" } when idx >= 0 && idx < data.OTAs.Length
                => data.OTAs[idx].FilterWheel,
            AssignTarget.OTALevel { OtaIndex: var idx, Field: "Cover" } when idx >= 0 && idx < data.OTAs.Length
                => data.OTAs[idx].Cover,
            _ => null
        };

        // NoneDevice means empty slot
        return uri == NoneDevice.Instance.DeviceUri ? null : uri;
    }

    /// <summary>
    /// Finds the profile slot URI matching the given device URI (path-equality via
    /// <see cref="DeviceBase.SameDevice"/>). Returns the profile URI which carries
    /// query params (API keys, ports, etc.) — these are stripped from discovered URIs,
    /// so the profile copy is what should be passed to <see cref="IDeviceHub.ConnectAsync"/>.
    /// </summary>
    public static Uri? FindAssignedUri(ProfileData? data, Uri deviceUri)
    {
        if (data is not { } d) return null;
        if (DeviceBase.SameDevice(d.Mount, deviceUri)) return d.Mount;
        if (DeviceBase.SameDevice(d.Guider, deviceUri)) return d.Guider;
        if (DeviceBase.SameDevice(d.GuiderCamera, deviceUri)) return d.GuiderCamera;
        if (DeviceBase.SameDevice(d.GuiderFocuser, deviceUri)) return d.GuiderFocuser;
        if (DeviceBase.SameDevice(d.Weather, deviceUri)) return d.Weather;
        foreach (var ota in d.OTAs)
        {
            if (DeviceBase.SameDevice(ota.Camera, deviceUri)) return ota.Camera;
            if (DeviceBase.SameDevice(ota.Focuser, deviceUri)) return ota.Focuser;
            if (DeviceBase.SameDevice(ota.FilterWheel, deviceUri)) return ota.FilterWheel;
            if (DeviceBase.SameDevice(ota.Cover, deviceUri)) return ota.Cover;
        }
        return null;
    }

    /// <summary>
    /// Safety classification for an out-of-session disconnect of a connected device.
    /// Cameras are the primary concern: cold disconnect risks thermal shock; busy
    /// disconnect interrupts an exposure.
    /// </summary>
    public enum DisconnectSafety
    {
        /// <summary>Safe to disconnect immediately (not a camera, or cooler off and idle).</summary>
        Safe,
        /// <summary>Camera cooler is on — needs warm-up ramp before disconnect.</summary>
        CoolerOn,
        /// <summary>Camera is mid-exposure / downloading — should finish before disconnect.</summary>
        Busy,
        /// <summary>Both cooler on and camera busy.</summary>
        BusyAndCool,
        /// <summary>State could not be read (driver error) — caller should treat as unsafe.</summary>
        Unknown
    }

    /// <summary>
    /// Out-of-session warm-up + disconnect for a single camera. Ramps the setpoint
    /// toward the heat-sink (or +25°C fallback) in 2°C steps every 30s, then turns
    /// the cooler off and disconnects. Non-camera devices disconnect directly.
    /// Mirrors the spirit of <c>Session.Cooling.CoolCamerasToAmbientAsync</c> but
    /// without the multi-camera orchestration / telemetry collection.
    /// </summary>
    public static ValueTask WarmAndDisconnectAsync(
        IDeviceHub hub, Uri deviceUri,
        Microsoft.Extensions.Logging.ILogger logger,
        System.Threading.CancellationToken cancellationToken)
        => WarmCameraAsync(hub, deviceUri, logger, disconnectAfter: true, cancellationToken);

    /// <summary>
    /// Warm-up ramp + cooler-off without disconnecting (camera stays available for
    /// re-cooling). Same condensation-mitigation rationale as
    /// <see cref="WarmAndDisconnectAsync"/>.
    /// </summary>
    public static ValueTask WarmAndCoolerOffAsync(
        IDeviceHub hub, Uri deviceUri,
        Microsoft.Extensions.Logging.ILogger logger,
        System.Threading.CancellationToken cancellationToken)
        => WarmCameraAsync(hub, deviceUri, logger, disconnectAfter: false, cancellationToken);

    /// <summary>
    /// Shared warm-up ramp implementation. Steps the setpoint toward the heat-sink (or
    /// +25°C fallback) in 2°C / 30s increments capped at 15 min, then turns the cooler
    /// off and (optionally) disconnects.
    /// </summary>
    private static async ValueTask WarmCameraAsync(
        IDeviceHub hub, Uri deviceUri,
        Microsoft.Extensions.Logging.ILogger logger,
        bool disconnectAfter,
        System.Threading.CancellationToken cancellationToken)
    {
        if (!hub.TryGetConnectedDriver<TianWen.Lib.Devices.ICameraDriver>(deviceUri, out var camera))
        {
            if (disconnectAfter) await hub.DisconnectAsync(deviceUri, cancellationToken);
            return;
        }

        // Determine target temperature: heat-sink if available, else +25°C.
        double target = 25.0;
        if (camera.CanGetHeatsinkTemperature)
        {
            try { target = await camera.GetHeatSinkTemperatureAsync(cancellationToken); }
            catch (Exception ex) { logger.LogWarning(ex, "GetHeatSinkTemperatureAsync failed for {Uri}", deviceUri); }
        }

        var stepInterval = TimeSpan.FromSeconds(30);
        var stepSize = 2.0;
        var stallThreshold = 1.0;
        var maxSteps = 30;

        for (var i = 0; i < maxSteps && !cancellationToken.IsCancellationRequested; i++)
        {
            double current;
            try { current = await camera.GetCCDTemperatureAsync(cancellationToken); }
            catch { break; }

            if (current >= target - stallThreshold) break;

            var nextSetpoint = Math.Min(current + stepSize, target);
            try { await camera.SetSetCCDTemperatureAsync(nextSetpoint, cancellationToken); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SetSetCCDTemperatureAsync failed mid-ramp for {Uri}", deviceUri);
                break;
            }

            try { await Task.Delay(stepInterval, cancellationToken); }
            catch (OperationCanceledException) { throw; }
        }

        try { await camera.SetCoolerOnAsync(false, cancellationToken); }
        catch (Exception ex) { logger.LogWarning(ex, "SetCoolerOnAsync(false) failed for {Uri}", deviceUri); }

        try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
        catch (OperationCanceledException) { throw; }

        if (disconnectAfter)
        {
            await hub.DisconnectAsync(deviceUri, cancellationToken);
        }
    }

    /// <summary>
    /// Reads camera state and cooler status to determine whether the device can be
    /// safely disconnected without warm-up or interrupting work in flight.
    /// Returns <see cref="DisconnectSafety.Safe"/> for non-camera devices.
    /// </summary>
    public static async ValueTask<DisconnectSafety> GetDisconnectSafetyAsync(
        IDeviceHub hub, Uri deviceUri, System.Threading.CancellationToken cancellationToken = default)
    {
        if (!hub.TryGetConnectedDriver<TianWen.Lib.Devices.ICameraDriver>(deviceUri, out var camera))
        {
            return DisconnectSafety.Safe;
        }

        bool busy = false, cool = false;
        try
        {
            var state = await camera.GetCameraStateAsync(cancellationToken);
            busy = state is not (TianWen.Lib.Devices.CameraState.Idle or TianWen.Lib.Devices.CameraState.NotConnected);
        }
        catch
        {
            return DisconnectSafety.Unknown;
        }

        try
        {
            if (camera.CanGetCoolerOn)
            {
                cool = await camera.GetCoolerOnAsync(cancellationToken);
            }
        }
        catch
        {
            return DisconnectSafety.Unknown;
        }

        return (busy, cool) switch
        {
            (false, false) => DisconnectSafety.Safe,
            (false, true)  => DisconnectSafety.CoolerOn,
            (true, false)  => DisconnectSafety.Busy,
            (true, true)   => DisconnectSafety.BusyAndCool
        };
    }

    /// <summary>
    /// Reachability of a device as displayed in the Equipment tab. Combines profile
    /// assignment, current discovery state, and live connection state from the hub.
    /// </summary>
    public enum DeviceReachability
    {
        /// <summary>URI is not assigned to any slot in the active profile.</summary>
        NotAssigned,
        /// <summary>Assigned and currently connected via <see cref="IDeviceHub"/>.</summary>
        Connected,
        /// <summary>Assigned, present in the latest discovery results, but not connected — connectable.</summary>
        Disconnected,
        /// <summary>Assigned but not present in the latest discovery results — hardware unreachable.</summary>
        Offline
    }

    /// <summary>
    /// Computes the four-state reachability for a device URI by combining profile assignment,
    /// the latest discovery snapshot, and live hub connection state.
    /// </summary>
    public static DeviceReachability GetReachability(
        ProfileData? data,
        IDeviceHub? hub,
        IReadOnlyCollection<DeviceBase> discoveredDevices,
        Uri deviceUri)
    {
        // Live hub connection wins over assignment: a connected-but-unassigned device
        // (e.g. one the user just reassigned the slot away from) still needs an On|Off
        // toggle so they can disconnect it. Without this gate it would silently linger.
        if (hub is not null && hub.IsConnected(deviceUri))
        {
            return DeviceReachability.Connected;
        }

        if (data is not { } pdata || !IsDeviceAssigned(pdata, deviceUri))
        {
            return DeviceReachability.NotAssigned;
        }

        foreach (var d in discoveredDevices)
        {
            if (DeviceBase.SameDevice(d.DeviceUri, deviceUri))
            {
                return DeviceReachability.Disconnected;
            }
        }

        return DeviceReachability.Offline;
    }

    /// <summary>
    /// Returns a human-readable label for a device URI, using the registry if available.
    /// </summary>
    public static string DeviceLabel(Uri? uri, IDeviceHub? registry = null)
    {
        if (uri is null || uri == NoneDevice.Instance.DeviceUri)
        {
            return "(none)";
        }

        if (registry is not null && registry.TryGetDeviceFromUri(uri, out var device))
        {
            return device.DisplayName;
        }

        // Fallback: use URI fragment (display name) if available, else path
        var fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        if (fragment.Length > 0)
        {
            return fragment;
        }

        var path = uri.AbsolutePath.TrimStart('/');
        return path.Length > 0 ? path : uri.ToString();
    }

    /// <summary>
    /// Reads filter config from a filter wheel URI's query params.
    /// Returns the list of installed filters (may be empty if no filter{N} params present).
    /// </summary>
    public static IReadOnlyList<InstalledFilter> GetFilterConfig(ProfileData data, int otaIndex)
    {
        if (otaIndex < 0 || otaIndex >= data.OTAs.Length)
        {
            return [];
        }

        var fwUri = data.OTAs[otaIndex].FilterWheel;
        if (fwUri is null || fwUri == NoneDevice.Instance.DeviceUri)
        {
            return [];
        }

        var query = HttpUtility.ParseQueryString(fwUri.Query);
        var filters = new List<InstalledFilter>();

        for (var i = 1; ; i++)
        {
            var name = query[DeviceQueryKeyExtensions.FilterKey(i)];
            if (name is null)
            {
                break;
            }

            var offset = int.TryParse(query[DeviceQueryKeyExtensions.FilterOffsetKey(i)], out var o) ? o : 0;
            filters.Add(new InstalledFilter(name, offset));
        }

        return filters;
    }

    /// <summary>
    /// Returns new ProfileData with the filter wheel URI's query params updated to reflect the given filters.
    /// Preserves other query params on the URI.
    /// </summary>
    public static ProfileData SetFilterConfig(ProfileData data, int otaIndex, IReadOnlyList<InstalledFilter> filters)
    {
        if (otaIndex < 0 || otaIndex >= data.OTAs.Length)
        {
            return data;
        }

        var ota = data.OTAs[otaIndex];
        var fwUri = ota.FilterWheel;
        if (fwUri is null || fwUri == NoneDevice.Instance.DeviceUri)
        {
            return data;
        }

        var query = HttpUtility.ParseQueryString(fwUri.Query);

        // Remove existing filter/offset params
        for (var i = 1; ; i++)
        {
            var key = DeviceQueryKeyExtensions.FilterKey(i);
            if (query[key] is null)
            {
                break;
            }
            query.Remove(key);
            query.Remove(DeviceQueryKeyExtensions.FilterOffsetKey(i));
        }

        // Write new filter/offset params
        for (var i = 0; i < filters.Count; i++)
        {
            query[DeviceQueryKeyExtensions.FilterKey(i + 1)] = filters[i].DisplayName;
            query[DeviceQueryKeyExtensions.FilterOffsetKey(i + 1)] = filters[i].Position.ToString(CultureInfo.InvariantCulture);
        }

        var builder = new UriBuilder(fwUri) { Query = query.ToString() };
        var updatedOta = ota with { FilterWheel = builder.Uri };
        return data with { OTAs = data.OTAs.SetItem(otaIndex, updatedOta) };
    }

    /// <summary>
    /// Returns new ProfileData with the OTA at the given index updated with the provided properties.
    /// Only non-null parameters are applied.
    /// </summary>
    public static ProfileData UpdateOTA(
        ProfileData data,
        int otaIndex,
        string? name = null,
        int? focalLength = null,
        int? aperture = null,
        OpticalDesign? opticalDesign = null)
    {
        if (otaIndex < 0 || otaIndex >= data.OTAs.Length)
        {
            return data;
        }

        var ota = data.OTAs[otaIndex];

        if (name is not null)
        {
            ota = ota with { Name = name };
        }
        if (focalLength is not null)
        {
            ota = ota with { FocalLength = focalLength.Value };
        }
        if (aperture is not null)
        {
            ota = ota with { Aperture = aperture.Value > 0 ? aperture.Value : null };
        }
        if (opticalDesign is not null)
        {
            ota = ota with { OpticalDesign = opticalDesign.Value };
        }

        return data with { OTAs = data.OTAs.SetItem(otaIndex, ota) };
    }

    /// <summary>
    /// Returns new <see cref="ProfileData"/> with <paramref name="oldUri"/> replaced by <paramref name="newUri"/>
    /// in whichever slot it occupies (mount, guider, guider camera/focuser, or OTA sub-slots).
    /// </summary>
    public static ProfileData UpdateDeviceUri(ProfileData data, Uri oldUri, Uri newUri)
    {
        if (DeviceBase.SameDevice(data.Mount, oldUri))
        {
            // Preserve mount's existing non-device query params (site coords, etc.) by
            // merging newUri's query on top.
            var baseQuery = HttpUtility.ParseQueryString(data.Mount.Query);
            var newQuery = HttpUtility.ParseQueryString(newUri.Query);
            foreach (string? key in newQuery)
            {
                if (key is not null)
                {
                    baseQuery[key] = newQuery[key];
                }
            }
            var builder = new UriBuilder(newUri) { Query = baseQuery.ToString() };
            data = data with { Mount = builder.Uri };
        }
        if (DeviceBase.SameDevice(data.Guider, oldUri))
        {
            data = data with { Guider = newUri };
        }
        if (DeviceBase.SameDevice(data.GuiderCamera, oldUri))
        {
            data = data with { GuiderCamera = newUri };
        }
        if (DeviceBase.SameDevice(data.GuiderFocuser, oldUri))
        {
            data = data with { GuiderFocuser = newUri };
        }
        if (DeviceBase.SameDevice(data.Weather, oldUri))
        {
            data = data with { Weather = newUri };
        }

        for (var i = 0; i < data.OTAs.Length; i++)
        {
            var ota = data.OTAs[i];
            var changed = false;

            if (DeviceBase.SameDevice(ota.Camera, oldUri)) { ota = ota with { Camera = newUri }; changed = true; }
            if (DeviceBase.SameDevice(ota.Focuser, oldUri)) { ota = ota with { Focuser = newUri }; changed = true; }
            if (DeviceBase.SameDevice(ota.FilterWheel, oldUri)) { ota = ota with { FilterWheel = newUri }; changed = true; }
            if (DeviceBase.SameDevice(ota.Cover, oldUri)) { ota = ota with { Cover = newUri }; changed = true; }

            if (changed)
            {
                data = data with { OTAs = data.OTAs.SetItem(i, ota) };
            }
        }

        return data;
    }

    /// <summary>
    /// Instantiates a <see cref="DeviceBase"/> subclass from a URI using the host name
    /// to select the correct type. Returns null if the host is not recognised.
    /// </summary>
    public static DeviceBase? TryDeviceFromUri(Uri? uri)
    {
        if (uri is null || uri == NoneDevice.Instance.DeviceUri)
        {
            return null;
        }

        return uri.Host.ToLowerInvariant() switch
        {
            "builtinguiderdevice" => new BuiltInGuiderDevice(uri),
            "openmeteodevice" => new OpenMeteoDevice(uri),
            "openweathermapdevice" => new OpenWeatherMapDevice(uri),
            "canondevice" => new CanonDevice(uri),
            "fakedevice" => new FakeDevice(uri),
            _ => null
        };
    }

    /// <summary>
    /// Extracts site coordinates from <see cref="ProfileData"/>, if present.
    /// </summary>
    public static (double Lat, double Lon, double? Elev)? GetSiteFromProfile(ProfileData data)
        => data.SiteLatitude is { } lat && data.SiteLongitude is { } lon
            ? (lat, lon, data.SiteElevation)
            : null;
}
