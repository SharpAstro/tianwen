using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Creates a <see cref="Transform"/> from a profile's stored site coordinates.
/// Shared between CLI and GUI.
/// </summary>
public static class TransformFactory
{
    /// <summary>
    /// Creates a Transform from <see cref="ProfileData.SiteLatitude"/>, <see cref="ProfileData.SiteLongitude"/>,
    /// and <see cref="ProfileData.SiteElevation"/>. Returns null with an error message if the
    /// profile has no mount or no site coordinates configured.
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

        var d = data.Value;
        if (d.SiteLatitude is not { } lat || d.SiteLongitude is not { } lon)
        {
            error = "Profile has no site coordinates. Connect a mount to auto-seed, or set them in the Equipment tab.";
            return null;
        }

        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            error = $"Invalid coordinates in profile: lat={lat}, lon={lon}. Use Equipment tab to fix.";
            return null;
        }

        var transform = new Transform(timeProvider)
        {
            SiteLatitude = lat,
            SiteLongitude = lon,
            SiteElevation = d.SiteElevation ?? 0.0,
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
