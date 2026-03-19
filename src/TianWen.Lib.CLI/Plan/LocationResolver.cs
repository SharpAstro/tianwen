using System;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI.Plan;

/// <summary>
/// Resolves site latitude/longitude from CLI options or the active profile's mount URI.
/// </summary>
internal static class LocationResolver
{
    /// <summary>
    /// Creates a <see cref="Transform"/> configured with site location.
    /// Resolution order: explicit CLI args → profile mount URI query params → error.
    /// </summary>
    public static Transform? Resolve(
        IConsoleHost consoleHost,
        Profile? profile,
        double? cliLatitude,
        double? cliLongitude,
        TimeProvider timeProvider)
    {
        double lat, lon;

        if (cliLatitude.HasValue && cliLongitude.HasValue)
        {
            lat = cliLatitude.Value;
            lon = cliLongitude.Value;
        }
        else if (profile?.Data is { } data && data.Mount != NoneDevice.Instance.DeviceUri)
        {
            var query = System.Web.HttpUtility.ParseQueryString(data.Mount.Query);
            var latStr = query["latitude"];
            var lonStr = query["longitude"];

            if (latStr is not null && lonStr is not null &&
                double.TryParse(latStr, System.Globalization.CultureInfo.InvariantCulture, out lat) &&
                double.TryParse(lonStr, System.Globalization.CultureInfo.InvariantCulture, out lon))
            {
                // parsed from mount URI
            }
            else
            {
                consoleHost.WriteError("Mount URI does not contain latitude/longitude. Use --lat and --lon.");
                return null;
            }
        }
        else
        {
            consoleHost.WriteError("No location available. Use --lat and --lon, or select a profile with a configured mount.");
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
