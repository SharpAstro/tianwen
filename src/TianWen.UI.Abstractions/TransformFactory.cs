using System;
using System.Globalization;
using System.Web;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Creates a <see cref="Transform"/> from a profile's mount URI (latitude, longitude, elevation).
/// Shared between CLI and GUI.
/// </summary>
public static class TransformFactory
{
    /// <summary>
    /// Creates a Transform from the profile's mount URI query params.
    /// Returns null with an error message if the profile has no mount or no lat/lon.
    /// </summary>
    public static Transform? FromProfile(Profile profile, ITimeProvider timeProvider, out string? error)
    {
        error = null;
        var data = profile.Data;
        if (data is null || data.Value.Mount == NoneDevice.Instance.DeviceUri)
        {
            error = "Profile has no mount configured. Use 'tianwen profile set-mount' first.";
            return null;
        }

        var query = HttpUtility.ParseQueryString(data.Value.Mount.Query);
        var latStr = query[DeviceQueryKey.Latitude.Key];
        var lonStr = query[DeviceQueryKey.Longitude.Key];
        var elevStr = query[DeviceQueryKey.Elevation.Key];

        if (latStr is null || lonStr is null ||
            !double.TryParse(latStr, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(lonStr, CultureInfo.InvariantCulture, out var lon))
        {
            error = "Mount URI does not contain latitude/longitude. Use 'tianwen profile set-site' to configure.";
            return null;
        }

        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            error = $"Invalid coordinates in profile: lat={lat}, lon={lon}. Use Equipment tab to fix.";
            return null;
        }

        var elevation = elevStr is not null && double.TryParse(elevStr, CultureInfo.InvariantCulture, out var elev)
            ? elev
            : 0.0;

        var transform = new Transform(timeProvider)
        {
            SiteLatitude = lat,
            SiteLongitude = lon,
            SiteElevation = elevation,
            SiteTemperature = 15,
            DateTimeOffset = timeProvider.System.GetLocalNow()
        };

        // Re-express "now" in the site's timezone so CalculateNightWindow computes
        // the correct evening for the site, not for the machine's local timezone.
        if (transform.TryGetSiteTimeZone(out var siteOffset, out _))
        {
            transform.DateTimeOffset = timeProvider.GetUtcNow().ToOffset(siteOffset);
        }

        return transform;
    }
}
