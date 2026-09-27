using TianWen.Lib.Sequencing;
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

    /// <summary>
    /// Visibility + interactivity + label for the shared "Connect All" action, computed once
    /// and consumed by both the app chrome (top bar) and any panel that wants to surface it.
    /// </summary>
    public readonly record struct ConnectAllStatus(bool Visible, bool Enabled, string Label);

    /// <summary>
    /// Computes the <see cref="ConnectAllStatus"/> for the active profile. The button is:
    /// <list type="bullet">
    ///   <item>hidden when the profile has no assigned devices;</item>
    ///   <item>enabled only once discovery has <b>finished</b> (<paramref name="isDiscovering"/> is
    ///   false), every assigned URI is resolvable (hub-connected, hub-known, or freshly discovered),
    ///   at least one assigned URI is not yet connected, and no connect/disconnect transition is in
    ///   flight;</item>
    ///   <item>otherwise shown disabled with a status label ("Discovering...", "All Connected",
    ///   "Connecting...", "Discover first").</item>
    /// </list>
    /// </summary>
    public static ConnectAllStatus ComputeConnectAllStatus(
        ProfileData pd, NodeConnection? node,
        IReadOnlyList<DeviceBase> discoveredDevices,
        IReadOnlyDictionary<Uri, byte> pendingTransitions,
        bool isDiscovering)
    {
        var anyAssigned = false;
        var allDiscoverable = true;
        var anyNotConnected = false;
        var anyPending = false;
        foreach (var u in pd.AssignedDeviceUris)
        {
            anyAssigned = true;
            var connected = node?.IsConnected(u) == true;
            if (!connected) anyNotConnected = true;
            if (pendingTransitions.ContainsKey(u)) anyPending = true;

            var resolvable = node?.Device(u) is not null
                || connected
                || discoveredDevices.Any(d => DeviceBase.SameDevice(d.DeviceUri, u));
            if (!resolvable) allDiscoverable = false;
        }

        if (!anyAssigned)
        {
            return new ConnectAllStatus(false, false, "Connect All");
        }

        // Only actionable once discovery has finished and there is something to connect.
        var enabled = !isDiscovering && allDiscoverable && anyNotConnected && !anyPending;
        string label;
        if (isDiscovering) label = "Discovering…";
        else if (!anyNotConnected) label = "All Connected";
        else if (anyPending) label = "Connecting…";
        else if (!allDiscoverable) label = "Discover first";
        else label = "Connect All";

        return new ConnectAllStatus(true, enabled, label);
    }

    /// <summary>
    /// The devices the Equipment tab lists, of what the node listed: every device but a profile (the node lists those
    /// too) and the empty slot, a fake device only when asked for (Shift+Discover, the TUI's <c>--fake</c>; every node
    /// registers the fake source, so the choice is the client's), by type and then by name.
    /// </summary>
    public static IReadOnlyList<DeviceBase> ForTheDeviceList(ImmutableArray<NodeDevice> listed, bool includeFake) =>
        [.. listed
            .Where(d => d.DeviceType is not DeviceType.Profile and not DeviceType.None)
            .Where(d => includeFake || !string.Equals(d.DeviceClass, nameof(FakeDevice), StringComparison.OrdinalIgnoreCase))
            .OrderBy(static d => d.DeviceType).ThenBy(static d => d.DisplayName, StringComparer.OrdinalIgnoreCase)];

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

    /// <summary>
    /// Replaces the profile's mount safety limits (docs/plans/mount-safety-limits.md, P1). Lives on the
    /// PROFILE, beside the site, because where the tube meets the pier is a fact about the rig, not about a
    /// night; a <c>with</c>, so every other field round-trips.
    /// </summary>
    public static ProfileData SetMountLimits(ProfileData data, MountLimitConfiguration? limits)
        => data with { MountLimits = limits };

    /// <summary>
    /// Parses + range-validates the four numeric limit fields (invariant culture; meridian minutes
    /// 0..360, horizon degrees 0..60) onto <paramref name="current"/>, whose switch and responses are kept
    /// -- those are buttons that save on their own. Pure, extracted from the commit callback so the
    /// callback routes only, exactly like <see cref="TryParseSite"/>.
    /// </summary>
    public static bool TryParseMountLimits(
        string meridianWarnMinutes, string meridianActionExtraMinutes,
        string horizonActionDeg, string horizonWarnExtraDeg,
        MountLimitConfiguration current, out MountLimitConfiguration parsed)
    {
        parsed = current;
        if (double.TryParse(meridianWarnMinutes, CultureInfo.InvariantCulture, out var warn)
            && double.TryParse(meridianActionExtraMinutes, CultureInfo.InvariantCulture, out var extra)
            && double.TryParse(horizonActionDeg, CultureInfo.InvariantCulture, out var floor)
            && double.TryParse(horizonWarnExtraDeg, CultureInfo.InvariantCulture, out var above)
            && warn is >= 0 and <= 360 && extra is >= 0 and <= 360
            && floor is >= 0 and <= 60 && above is >= 0 and <= 60)
        {
            parsed = current with
            {
                MeridianWarnMinutes = warn,
                MeridianActionExtraMinutes = extra,
                HorizonActionDeg = floor,
                HorizonWarnExtraDeg = above,
            };
            return true;
        }
        return false;
    }

    /// <summary>
    /// One line for the profile panel's display row. Absolute thresholds, not the stored extras: the user
    /// reads "stop at 40 min", and the warn-before-act invariant is the record's business.
    /// </summary>
    public static string DescribeMountLimits(MountLimitConfiguration? limits)
    {
        if (limits is not { Enabled: true } l)
        {
            return "Limits: off";
        }
        return $"Limits: meridian {Verb(l.MeridianResponse)} at {l.MeridianActionMinutes:F0} min (warn {l.MeridianWarnMinutes:F0}), "
             + $"horizon {Verb(l.HorizonResponse)} at {l.HorizonActionDeg:F0}\u00b0 (warn {l.HorizonWarnDeg:F0}\u00b0)";

        static string Verb(MountLimitResponse response) => response switch
        {
            MountLimitResponse.Park => "park",
            MountLimitResponse.StopTracking => "stop",
            _ => "warn",
        };
    }

    /// <summary>
    /// Parses + range-validates the three site text-input fields (invariant culture,
    /// lat -90..90 / lon -180..180; elevation optional). Pure -- extracted from the site
    /// text-input commit callback so the callback routes only. Returns false (and leaves
    /// outputs unspecified) when latitude/longitude don't parse or fall out of range.
    /// </summary>
    public static bool TryParseSite(string latText, string lonText, string elevText,
        out double lat, out double lon, out double? elev)
    {
        elev = null;
        if (double.TryParse(latText, CultureInfo.InvariantCulture, out lat)
            && double.TryParse(lonText, CultureInfo.InvariantCulture, out lon)
            && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
        {
            elev = double.TryParse(elevText, CultureInfo.InvariantCulture, out var e) ? e : null;
            return true;
        }
        lon = 0;
        return false;
    }

    /// <summary>
    /// Commits a device string setting. A masked setting (a secret, e.g. an API key) is
    /// persisted to the OS credential store keyed by device -- NEVER onto the device URI /
    /// profile JSON, which would leak it into plaintext config AND lose it when the URI is
    /// replaced on a provider switch; the credential key derives from the device id so it stays
    /// stable across URI changes and shared across profiles. A non-secret setting is returned as
    /// a new URI with the value as a query param. Extracted from the string-setting commit
    /// callback so the callback routes only (it applies the returned URI / re-fetches weather).
    /// </summary>
    public static DeviceSettingCommitResult CommitDeviceSetting(
        Uri editUri, string key, string value, ICredentialStore credentialStore)
        => DeviceSettingHelper.Commit(TryDeviceFromUri(editUri), editUri, key, value, credentialStore);

    public static ProfileData SetSiteTieBreaker(ProfileData data, SiteTieBreaker tieBreaker)
        => data with { SiteTieBreaker = tieBreaker };

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
    /// Applies a discovered device to the slot described by <paramref name="target"/> --
    /// the single dispatch for profile-level fields (mount / guider / guider camera /
    /// guider focuser / weather) and per-OTA slots. Pure transformation; unknown targets
    /// return <paramref name="data"/> unchanged. Extracted from AssignDeviceSignal so the
    /// handler routes only.
    /// </summary>
    public static ProfileData ApplyAssignment(ProfileData data, AssignTarget target, DeviceType deviceType, Uri deviceUri)
        => target switch
        {
            AssignTarget.ProfileLevel { Field: "Mount" } => AssignMount(data, deviceUri),
            AssignTarget.ProfileLevel { Field: "Guider" } => AssignGuider(data, deviceUri),
            AssignTarget.ProfileLevel { Field: "GuiderCamera" } => AssignGuiderCamera(data, deviceUri),
            AssignTarget.ProfileLevel { Field: "GuiderFocuser" } => AssignGuiderFocuser(data, deviceUri),
            AssignTarget.ProfileLevel { Field: "Weather" } => AssignWeather(data, deviceUri),
            AssignTarget.OTALevel otaTarget => AssignDeviceToOTA(data, otaTarget.OtaIndex, deviceType, deviceUri),
            _ => data
        };

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
            var builder = new UriBuilder(none) { Query = data.Mount.Query };
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
    /// query params (API keys, ports, etc.): these are stripped from discovered URIs,
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

    /// <summary>Result of <see cref="AutoDisconnectOrphanAsync"/>.</summary>
    public enum OrphanDisconnectOutcome
    {
        /// <summary>No orphan to handle: no previous URI, same device re-assigned, or not connected.</summary>
        NotApplicable,
        /// <summary>Orphan was safe and has been disconnected.</summary>
        Disconnected,
        /// <summary>Disconnect threw (already logged); the orphan may still be connected.</summary>
        DisconnectFailed,
        /// <summary>Orphan is cool/busy -- left connected so the warm-up ramp can run under user control.</summary>
        LeftConnected
    }

    /// <summary>
    /// Reachability of a device as displayed in the Equipment tab. Combines profile
    /// assignment, current discovery state, and live connection state from the hub.
    /// </summary>
    public enum DeviceReachability
    {
        /// <summary>URI is not assigned to any slot in the active profile.</summary>
        NotAssigned,
        /// <summary>Assigned and currently connected on the node.</summary>
        Connected,
        /// <summary>Assigned, present in the latest discovery results, but not connected; connectable.</summary>
        Disconnected,
        /// <summary>Assigned but not present in the latest discovery results; hardware unreachable.</summary>
        Offline
    }

    /// <summary>
    /// Computes the four-state reachability for a device URI by combining profile assignment,
    /// the latest discovery snapshot, and live hub connection state.
    /// </summary>
    public static DeviceReachability GetReachability(
        ProfileData? data,
        NodeConnection? node,
        IReadOnlyCollection<DeviceBase> discoveredDevices,
        Uri deviceUri)
    {
        // Live hub connection wins over assignment: a connected-but-unassigned device
        // (e.g. one the user just reassigned the slot away from) still needs an On|Off
        // toggle so they can disconnect it. Without this gate it would silently linger.
        if (node is not null && node.IsConnected(deviceUri))
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
    /// Returns a human-readable label for a device URI: the name the node holds it under, else the URI's own.
    /// </summary>
    public static string DeviceLabel(Uri? uri, NodeConnection? node = null)
    {
        if (uri is null || uri == NoneDevice.Instance.DeviceUri)
        {
            return "(none)";
        }

        if (node?.Device(uri) is { DisplayName: { Length: > 0 } held })
        {
            return held;
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
