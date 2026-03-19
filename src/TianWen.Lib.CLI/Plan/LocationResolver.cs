using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI.Plan;

/// <summary>
/// Resolves site latitude/longitude from the active profile's mount URI.
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

        var query = System.Web.HttpUtility.ParseQueryString(data.Value.Mount.Query);
        var latStr = query["latitude"];
        var lonStr = query["longitude"];

        if (latStr is null || lonStr is null ||
            !double.TryParse(latStr, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(lonStr, System.Globalization.CultureInfo.InvariantCulture, out var lon))
        {
            consoleHost.WriteError("Mount URI does not contain latitude/longitude. Reconfigure the mount with location.");
            return null;
        }

        return new Transform(timeProvider)
        {
            SiteLatitude = lat,
            SiteLongitude = lon,
            SiteElevation = 200,
            SiteTemperature = 15,
            DateTimeOffset = timeProvider.GetLocalNow()
        };
    }
}
