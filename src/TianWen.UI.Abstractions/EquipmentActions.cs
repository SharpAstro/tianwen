using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Pure functions for profile/equipment manipulation. Shared between CLI and GUI.
/// All methods return new ProfileData (immutable record with-expressions).
/// </summary>
public static class EquipmentActions
{
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
        if (data.Mount == deviceUri || data.Guider == deviceUri)
        {
            return true;
        }
        if (data.GuiderCamera == deviceUri || data.GuiderFocuser == deviceUri)
        {
            return true;
        }

        foreach (var ota in data.OTAs)
        {
            if (ota.Camera == deviceUri || ota.Focuser == deviceUri ||
                ota.FilterWheel == deviceUri || ota.Cover == deviceUri)
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

        if (data.Mount == deviceUri)
        {
            // Preserve site query params when clearing mount
            var builder = new UriBuilder(none) { Query = data.Mount.Query };
            data = data with { Mount = builder.Uri };
        }
        if (data.Guider == deviceUri)
        {
            data = data with { Guider = none };
        }
        if (data.GuiderCamera == deviceUri)
        {
            data = data with { GuiderCamera = null };
        }
        if (data.GuiderFocuser == deviceUri)
        {
            data = data with { GuiderFocuser = null };
        }

        for (var i = 0; i < data.OTAs.Length; i++)
        {
            var ota = data.OTAs[i];
            var changed = false;

            if (ota.Camera == deviceUri) { ota = ota with { Camera = none }; changed = true; }
            if (ota.Focuser == deviceUri) { ota = ota with { Focuser = null }; changed = true; }
            if (ota.FilterWheel == deviceUri) { ota = ota with { FilterWheel = null }; changed = true; }
            if (ota.Cover == deviceUri) { ota = ota with { Cover = null }; changed = true; }

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
    public static string DeviceLabel(Uri? uri, IDeviceUriRegistry? registry)
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
