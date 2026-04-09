using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
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
    {
        var query = HttpUtility.ParseQueryString(data.Mount.Query);
        query[DeviceQueryKey.Latitude.Key] = lat.ToString(CultureInfo.InvariantCulture);
        query[DeviceQueryKey.Longitude.Key] = lon.ToString(CultureInfo.InvariantCulture);
        if (elevation.HasValue)
        {
            query[DeviceQueryKey.Elevation.Key] = elevation.Value.ToString(CultureInfo.InvariantCulture);
        }
        var builder = new UriBuilder(data.Mount) { Query = query.ToString() };
        return data with { Mount = builder.Uri };
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
    /// Extracts site coordinates from a mount URI, if present.
    /// </summary>
    public static (double Lat, double Lon, double? Elev)? GetSiteFromMount(Uri mountUri)
    {
        if (mountUri == NoneDevice.Instance.DeviceUri)
        {
            return null;
        }

        var query = HttpUtility.ParseQueryString(mountUri.Query);
        var latStr = query[DeviceQueryKey.Latitude.Key];
        var lonStr = query[DeviceQueryKey.Longitude.Key];
        var elevStr = query[DeviceQueryKey.Elevation.Key];

        if (latStr is not null && lonStr is not null &&
            double.TryParse(latStr, CultureInfo.InvariantCulture, out var lat) &&
            double.TryParse(lonStr, CultureInfo.InvariantCulture, out var lon))
        {
            double? elev = elevStr is not null && double.TryParse(elevStr, CultureInfo.InvariantCulture, out var e) ? e : null;
            return (lat, lon, elev);
        }

        return null;
    }
}
