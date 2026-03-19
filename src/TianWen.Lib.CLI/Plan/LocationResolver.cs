using System.Globalization;
using System.Web;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI.Plan;

/// <summary>
/// Resolves site latitude/longitude/elevation from the active profile's mount URI.
/// </summary>
internal static class LocationResolver
{
    /// <summary>
    /// Creates a <see cref="Transform"/> configured with the site location from the profile's mount URI.
    /// </summary>
    public static Transform? ResolveFromProfile(
        IConsoleHost consoleHost,
        Profile profile,
        TimeProvider timeProvider)
    {
        var data = profile.Data;
        if (data is null || data.Value.Mount == NoneDevice.Instance.DeviceUri)
        {
            consoleHost.WriteError("Profile has no mount configured. Use 'tianwen profile set-mount' first.");
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
            consoleHost.WriteError("Mount URI does not contain latitude/longitude. Use 'tianwen profile set-site' to configure.");
            return null;
        }

        var elevation = elevStr is not null && double.TryParse(elevStr, CultureInfo.InvariantCulture, out var elev)
            ? elev
            : 0.0;

        return new Transform(timeProvider)
        {
            SiteLatitude = lat,
            SiteLongitude = lon,
            SiteElevation = elevation,
            SiteTemperature = 15,
            DateTimeOffset = timeProvider.GetLocalNow()
        };
    }
}
