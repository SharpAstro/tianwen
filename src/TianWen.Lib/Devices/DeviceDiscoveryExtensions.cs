using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Web;

namespace TianWen.Lib.Devices;

public static class DeviceDiscoveryExtensions
{
    extension(IDeviceDiscovery discovery)
    {
        /// <summary>
        /// Cross-references a stored device URI against the current discovery cache.
        /// If a live candidate exists with the same identity (scheme + authority + path,
        /// i.e. matching <see cref="DeviceBase.DeviceId"/>), merges the two query strings:
        /// transport params (port / host / baud / deviceNumber) come from discovery so
        /// they track the OS / network's current assignment, while user-configured params
        /// (site coordinates, filter slot names, gain, etc.) are preserved from the stored
        /// URI so a device source that advertises default values can't clobber user edits.
        /// <para>
        /// Fixes two classes of bug:
        /// <list type="bullet">
        ///   <item>"I replugged USB to a different hub and now COM5 is COM6"; transport drift. Here only for a family
        ///   whose path keeps the port out (a camera's serial, Alpaca's unique id, an LX200/OnStep mount's UUID): one
        ///   with the port in its path (a Gemini or QHY serial device, a Skywatcher, a SkyGuider Pro, a mount without a
        ///   UUID) is a different key on its new port, which <see cref="FollowMovedPort"/> matches by model instead
        ///   (docs/architecture/device-architecture.md).</item>
        ///   <item>"Discovery published default latitude=48.2 and reset my site to it"; user-config clobber.</item>
        /// </list>
        /// </para>
        /// </summary>
        public Uri ReconcileUri(Uri storedUri)
        {
            var deviceType = DeviceTypeHelper.TryParseDeviceType(storedUri.Scheme);
            if (deviceType is DeviceType.Unknown)
            {
                return storedUri;
            }

            foreach (var candidate in discovery.RegisteredDevices(deviceType))
            {
                if (!DeviceBase.SameDevice(candidate.DeviceUri, storedUri))
                {
                    continue;
                }

                if (candidate.DeviceUri == storedUri)
                {
                    return storedUri;
                }

                return MergeDiscoveredIntoStored(storedUri, candidate.DeviceUri);
            }

            return storedUri;
        }

        /// <summary>
        /// The one rule a discovery applies to a STORED profile, for the GUI's reconcile-all and the node's alike (P3 part 2
        /// of docs/plans/hardware-in-the-server.md, #930): a site still on the mount's URI moved into the profile
        /// (<see cref="ProfileDataExtensions.MigrateSiteFromMountUri"/>), then every device URI reconciled with what was
        /// discovered (<see cref="ReconcileProfileData"/>). Changed when either did anything.
        /// </summary>
        public (ProfileData Reconciled, bool Changed) ReconcileStoredProfile(ProfileData data)
        {
            var (migrated, moved) = data.MigrateSiteFromMountUri();
            var (reconciled, drifted) = discovery.ReconcileProfileData(migrated);
            return (reconciled, moved || drifted);
        }

        /// <summary>
        /// Reconciles every device URI a <see cref="ProfileData"/> carries with what was discovered. <c>Changed</c> is true
        /// iff any URI actually drifted, so callers can skip the disk write when everything is already in sync.
        /// </summary>
        /// <remarks>
        /// Two passes. The first matches each slot by identity (<see cref="ReconcileUri"/>). The second gives a slot that
        /// matched nothing its device on another port (<see cref="FollowMovedPort"/>), once every identity match is known, so
        /// a device another slot already has is never taken twice.
        /// </remarks>
        public (ProfileData Reconciled, bool Changed) ReconcileProfileData(ProfileData data)
        {
            var (byIdentity, identityChanged) = data.MapDeviceUris(discovery.ReconcileUri);

            var taken = new HashSet<string>(byIdentity.DeviceUris().Select(u => u.DeviceKey), DeviceUriExtensions.DeviceKeyComparer);
            var (followed, followChanged) = byIdentity.MapDeviceUris(uri => discovery.FollowMovedPort(uri, taken));

            return (followed, identityChanged || followChanged);
        }

        /// <summary>
        /// The device a slot's <paramref name="storedUri"/> names, on the port it has moved to, for a family whose ID ends in
        /// its port (<see cref="DeviceUriExtensions.ModelKey"/>): Windows numbers a CH340's COM port by the USB SOCKET, so the
        /// same Gemini focuser is <c>GeminiFocuser_COM3</c> in one socket and <c>GeminiFocuser_COM4</c> in the next, a different
        /// key that <see cref="ReconcileUri"/> never matches (#1744). Only when the stored device was not discovered, and
        /// exactly ONE discovered device of its model is in no other slot (<paramref name="taken"/>, which the one taken joins):
        /// none or several is left as stored, never guessed. The user's keys (backlash, filter names, site) are kept, as
        /// <see cref="MergeDiscoveredIntoStored"/> keeps them. Two identical units on swapped sockets are #783's case.
        /// </summary>
        public Uri FollowMovedPort(Uri storedUri, ISet<string> taken)
        {
            var deviceType = DeviceTypeHelper.TryParseDeviceType(storedUri.Scheme);
            if (deviceType is DeviceType.Unknown || storedUri.ModelKey is not { } model)
            {
                return storedUri;
            }

            var discovered = discovery.RegisteredDevices(deviceType).ToList();
            if (discovered.Exists(c => DeviceBase.SameDevice(c.DeviceUri, storedUri)))
            {
                return storedUri;
            }

            var candidates = discovered
                .Where(c => c.DeviceUri.ModelKey is { } m && DeviceUriExtensions.DeviceKeyComparer.Equals(m, model) && !taken.Contains(c.DeviceUri.DeviceKey))
                .ToList();
            if (candidates is not [var moved])
            {
                return storedUri;
            }

            taken.Add(moved.DeviceUri.DeviceKey);
            return MergeDiscoveredIntoStored(storedUri, moved.DeviceUri, identityFromDiscovered: true);
        }
    }

    extension(ProfileData data)
    {
        /// <summary>Every device URI the profile carries, in <see cref="MapDeviceUris"/>'s order.</summary>
        internal IEnumerable<Uri> DeviceUris()
        {
            yield return data.Mount;
            yield return data.Guider;
            if (data.GuiderCamera is { } gc) yield return gc;
            if (data.GuiderFocuser is { } gf) yield return gf;
            if (data.Weather is { } w) yield return w;
            foreach (var ota in data.OTAs)
            {
                yield return ota.Camera;
                if (ota.Cover is { } c) yield return c;
                if (ota.Focuser is { } f) yield return f;
                if (ota.FilterWheel is { } fw) yield return fw;
            }
        }

        /// <summary>
        /// The profile with <paramref name="map"/> applied to every device URI it carries: top-level mount, guider, optional
        /// guider-camera / guider-focuser / weather, plus each OTA's camera / cover / focuser / filter-wheel.
        /// Returns <c>Changed = true</c> iff any URI actually changed, so callers can skip the disk write when everything is
        /// already in sync. Adding a new URI field to <see cref="ProfileData"/> means updating this one helper and
        /// <see cref="DeviceUris"/>, not every call site.
        /// </summary>
        internal (ProfileData Mapped, bool Changed) MapDeviceUris(Func<Uri, Uri> map)
        {
            var mount = map(data.Mount);
            var guider = map(data.Guider);
            var guiderCam = data.GuiderCamera is { } gc ? map(gc) : null;
            var guiderFoc = data.GuiderFocuser is { } gf ? map(gf) : null;
            var weather = data.Weather is { } w ? map(w) : null;

            var otasChanged = false;
            var reconciledOtas = new OTAData[data.OTAs.Length];
            for (var i = 0; i < data.OTAs.Length; i++)
            {
                var ota = data.OTAs[i];
                var cam = map(ota.Camera);
                var cover = ota.Cover is { } c ? map(c) : null;
                var foc = ota.Focuser is { } f ? map(f) : null;
                var fw = ota.FilterWheel is { } w2 ? map(w2) : null;

                if (cam != ota.Camera || cover != ota.Cover || foc != ota.Focuser || fw != ota.FilterWheel)
                {
                    otasChanged = true;
                    reconciledOtas[i] = ota with { Camera = cam, Cover = cover, Focuser = foc, FilterWheel = fw };
                }
                else
                {
                    reconciledOtas[i] = ota;
                }
            }

            var topLevelChanged = mount != data.Mount
                || guider != data.Guider
                || guiderCam != data.GuiderCamera
                || guiderFoc != data.GuiderFocuser
                || weather != data.Weather;

            if (!topLevelChanged && !otasChanged)
            {
                return (data, false);
            }

            return (data with
            {
                Mount = mount,
                Guider = guider,
                GuiderCamera = guiderCam,
                GuiderFocuser = guiderFoc,
                Weather = weather,
                OTAs = otasChanged ? [.. reconciledOtas] : data.OTAs,
            }, true);
        }
    }

    /// <summary>
    /// Merges the query strings of <paramref name="storedUri"/> and <paramref name="discoveredUri"/>
    /// into a new URI. Both URIs share the same scheme + authority + path, unless <paramref name="identityFromDiscovered"/>:
    /// a device found on another port (<c>FollowMovedPort</c>) then takes the discovered path and display name, since its
    /// ID and name carry the new port.
    /// <para>
    /// Rules per query key:
    /// <list type="bullet">
    ///   <item>Present only in stored, or present in both as user-config (per <see cref="DeviceQueryKeyExtensions.IsTransportKey"/>):
    ///     keep the stored value, so user edits (site coordinates, filter slot names, etc.) survive reconcile.</item>
    ///   <item>Present in discovered as transport (port, host, baud, deviceNumber):
    ///     use the discovered value, so OS-assigned transport state refreshes.</item>
    ///   <item>Present only in discovered (regardless of classification):
    ///     use the discovered value: nothing to preserve in stored.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static Uri MergeDiscoveredIntoStored(Uri storedUri, Uri discoveredUri, bool identityFromDiscovered = false)
    {
        var storedQuery = HttpUtility.ParseQueryString(storedUri.Query);
        var discoveredQuery = HttpUtility.ParseQueryString(discoveredUri.Query);

        // If discovered advertises any transport state at all, the stored transport
        // snapshot is considered stale (e.g. serial -> WiFi flip, or port reassignment),
        // and ALL of stored's transport keys are dropped so discovered can fill cleanly.
        // Otherwise we keep stored's transport as a best-effort fallback.
        var discoveredHasTransport = false;
        foreach (string? k in discoveredQuery.AllKeys)
        {
            if (k is not null && DeviceQueryKeyExtensions.IsTransportKey(k))
            {
                discoveredHasTransport = true;
                break;
            }
        }

        var merged = new NameValueCollection();

        // 1. Copy stored keys, but skip stored transport when discovered has fresher transport.
        foreach (string? key in storedQuery.AllKeys)
        {
            if (key is null) continue;
            if (discoveredHasTransport && DeviceQueryKeyExtensions.IsTransportKey(key))
            {
                continue;
            }
            merged[key] = storedQuery[key];
        }

        // 2. Overlay discovered: transport keys always win; non-transport only fills gaps
        //    (never overwrites user-set values).
        foreach (string? key in discoveredQuery.AllKeys)
        {
            if (key is null) continue;
            if (DeviceQueryKeyExtensions.IsTransportKey(key) || merged[key] is null)
            {
                merged[key] = discoveredQuery[key];
            }
        }

        var builder = new UriBuilder(identityFromDiscovered ? discoveredUri : storedUri) { Query = merged.ToQueryString() };
        return builder.Uri;
    }
}
