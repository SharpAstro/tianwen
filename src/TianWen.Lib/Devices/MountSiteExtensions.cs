using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Devices;

/// <summary>
/// A site on the Earth: latitude and longitude in degrees, elevation in metres when it is known. What a
/// profile stores and a mount reports; <see cref="MountSiteExtensions"/> reconciles the two.
/// </summary>
/// <remarks>A class rather than a struct so a session can publish the site it settled on with one
/// reference write, which its poll then reads from another thread.</remarks>
public sealed record SiteCoordinates(double Latitude, double Longitude, double? Elevation = null)
{
    /// <summary>
    /// The site made of stored values, or null when there is none: a latitude or a longitude that is
    /// missing or NaN. A NaN elevation is an unknown one.
    /// </summary>
    public static SiteCoordinates? From(double? latitude, double? longitude, double? elevation)
        => latitude is { } lat && longitude is { } lon && !double.IsNaN(lat) && !double.IsNaN(lon)
            ? new SiteCoordinates(lat, lon, elevation is { } e && !double.IsNaN(e) ? e : null)
            : null;
}

/// <summary>Whose site a reconcile settled on.</summary>
public enum SiteSource
{
    /// <summary>Neither the mount nor the profile has one.</summary>
    None,
    /// <summary>The mount's own site.</summary>
    Mount,
    /// <summary>The profile's site.</summary>
    Profile,
}

/// <summary>
/// The outcome of reconciling a mount's site with a profile's: the site both end up on, and which half of
/// the reconcile each side owes.
/// </summary>
/// <param name="Site">The site once reconciled; null when neither side has one.</param>
/// <param name="Source">Whose site it is.</param>
/// <param name="PushToMount">The mount is to be given <paramref name="Site"/>. The MOUNT-side half, which
/// <c>ReconcileSiteAsync</c> applies itself.</param>
/// <param name="AdoptIntoProfile">The profile is to store <paramref name="Site"/>. The PROFILE-side half,
/// which only the profile's writer may apply: the GUI today, the server once it is the profile's one writer
/// (P3 of docs/plans/hardware-in-the-server.md).</param>
public sealed record SiteReconcileDecision(SiteCoordinates? Site, SiteSource Source, bool PushToMount, bool AdoptIntoProfile)
{
    /// <summary>
    /// The rule, in one place for every host (#798): when only one side has a site the other takes it, and
    /// when both do and they differ, <paramref name="tieBreaker"/> picks the side the other takes it from.
    /// </summary>
    public static SiteReconcileDecision Decide(SiteCoordinates? mount, SiteCoordinates? profile, SiteTieBreaker tieBreaker)
    {
        switch (mount, profile)
        {
            case (null, null):
                return new SiteReconcileDecision(null, SiteSource.None, PushToMount: false, AdoptIntoProfile: false);

            case ({ } mountOnly, null):
                return new SiteReconcileDecision(mountOnly, SiteSource.Mount, PushToMount: false, AdoptIntoProfile: true);

            case (null, { } profileOnly):
                return new SiteReconcileDecision(profileOnly, SiteSource.Profile, PushToMount: true, AdoptIntoProfile: false);

            case ({ } m, { } p) when tieBreaker is SiteTieBreaker.Mount:
                // The profile takes the mount's site whole, elevation included: an elevation only the
                // profile had is dropped, since the mount won.
                return new SiteReconcileDecision(m, SiteSource.Mount, PushToMount: false, AdoptIntoProfile: m != p);

            case ({ } m, { } p):
                // An elevation disagrees only when the mount reports one: a mount that keeps none has
                // nothing to correct.
                var differs = m.Latitude != p.Latitude || m.Longitude != p.Longitude
                    || (p.Elevation is { } profileElevation && m.Elevation is { } mountElevation && profileElevation != mountElevation);
                return new SiteReconcileDecision(p, SiteSource.Profile, PushToMount: differs, AdoptIntoProfile: false);
        }
    }
}

/// <summary>
/// A mount's site as a device-model operation, for every host: read it, write it, and reconcile it with a
/// profile's. Lived in the GUI's <c>EquipmentActions</c> until #798, so a session started on the server never
/// ran it, and a mount with no site of its own under the mount-wins default was never given the profile's.
/// </summary>
public static class MountSiteExtensions
{
    extension(IMountDriver mount)
    {
        /// <summary>
        /// The site the mount reports, or null when it reports none: NaN, what a mount never given a site
        /// answers (SkyWatcher, iOptron), or 0, 0, what an ASCOM driver typically answers instead.
        /// </summary>
        public async ValueTask<SiteCoordinates?> GetSiteAsync(CancellationToken cancellationToken)
        {
            var latitude = await mount.GetSiteLatitudeAsync(cancellationToken).ConfigureAwait(false);
            var longitude = await mount.GetSiteLongitudeAsync(cancellationToken).ConfigureAwait(false);
            if (double.IsNaN(latitude) || double.IsNaN(longitude) || (latitude == 0 && longitude == 0))
            {
                return null;
            }

            var elevation = await mount.GetSiteElevationAsync(cancellationToken).ConfigureAwait(false);
            return new SiteCoordinates(latitude, longitude, double.IsNaN(elevation) ? null : elevation);
        }

        /// <summary>Gives the mount <paramref name="site"/>; the elevation only when the site has one.</summary>
        public async ValueTask SetSiteAsync(SiteCoordinates site, CancellationToken cancellationToken)
        {
            await mount.SetSiteLatitudeAsync(site.Latitude, cancellationToken).ConfigureAwait(false);
            await mount.SetSiteLongitudeAsync(site.Longitude, cancellationToken).ConfigureAwait(false);
            if (site.Elevation is { } elevation)
            {
                await mount.SetSiteElevationAsync(elevation, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reconciles the mount's site with <paramref name="profileSite"/> (<see cref="SiteReconcileDecision.Decide"/>)
        /// and applies the MOUNT-side half. The profile-side half, <see cref="SiteReconcileDecision.AdoptIntoProfile"/>,
        /// is returned for the profile's writer to apply.
        /// </summary>
        public async ValueTask<SiteReconcileDecision> ReconcileSiteAsync(
            SiteCoordinates? profileSite, SiteTieBreaker tieBreaker, ILogger? logger, CancellationToken cancellationToken)
        {
            var mountSite = await mount.GetSiteAsync(cancellationToken).ConfigureAwait(false);
            var decision = SiteReconcileDecision.Decide(mountSite, profileSite, tieBreaker);

            if (decision.PushToMount && decision.Site is { } push)
            {
                logger?.LogInformation("Site reconcile (tie={TieBreaker}): the profile's {Lat}/{Lon} goes to the mount, which had {MountSite}.",
                    tieBreaker, push.Latitude, push.Longitude, mountSite?.ToString() ?? "none");
                await mount.SetSiteAsync(push, cancellationToken).ConfigureAwait(false);
            }
            else if (decision.AdoptIntoProfile && decision.Site is { } adopt)
            {
                logger?.LogInformation("Site reconcile (tie={TieBreaker}): the mount's {Lat}/{Lon} is the site, and the profile, which had {ProfileSite}, is to adopt it.",
                    tieBreaker, adopt.Latitude, adopt.Longitude, profileSite?.ToString() ?? "none");
            }

            return decision;
        }
    }
}
