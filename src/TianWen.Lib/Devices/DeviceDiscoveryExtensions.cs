using System;
using System.Collections.Specialized;
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
        ///   <item>"I replugged USB to a different hub and now COM5 is COM6" — transport drift.</item>
        ///   <item>"Discovery published default latitude=48.2 and reset my site to it" — user-config clobber.</item>
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
        /// Applies <see cref="ReconcileUri"/> to every device URI carried by a
        /// <see cref="ProfileData"/> — top-level mount, guider, optional guider-camera /
        /// guider-focuser / weather, plus each OTA's camera / cover / focuser / filter-wheel.
        /// Returns <c>Changed = true</c> iff any URI actually drifted, so callers can
        /// skip the disk write when everything is already in sync. Adding a new URI field
        /// to <see cref="ProfileData"/> means updating this one helper, not every call site.
        /// </summary>
        public (ProfileData Reconciled, bool Changed) ReconcileProfileData(ProfileData data)
        {
            var mount = discovery.ReconcileUri(data.Mount);
            var guider = discovery.ReconcileUri(data.Guider);
            var guiderCam = data.GuiderCamera is { } gc ? discovery.ReconcileUri(gc) : null;
            var guiderFoc = data.GuiderFocuser is { } gf ? discovery.ReconcileUri(gf) : null;
            var weather = data.Weather is { } w ? discovery.ReconcileUri(w) : null;

            var otasChanged = false;
            var reconciledOtas = new OTAData[data.OTAs.Length];
            for (var i = 0; i < data.OTAs.Length; i++)
            {
                var ota = data.OTAs[i];
                var cam = discovery.ReconcileUri(ota.Camera);
                var cover = ota.Cover is { } c ? discovery.ReconcileUri(c) : null;
                var foc = ota.Focuser is { } f ? discovery.ReconcileUri(f) : null;
                var fw = ota.FilterWheel is { } w2 ? discovery.ReconcileUri(w2) : null;

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
    /// into a new URI. Both URIs MUST share the same scheme + authority + path.
    /// <para>
    /// Rules per query key:
    /// <list type="bullet">
    ///   <item>Present only in stored, or present in both as user-config (per <see cref="DeviceQueryKeyExtensions.IsTransportKey"/>):
    ///     keep the stored value, so user edits (site coordinates, filter slot names, etc.) survive reconcile.</item>
    ///   <item>Present in discovered as transport (port, host, baud, deviceNumber):
    ///     use the discovered value, so OS-assigned transport state refreshes.</item>
    ///   <item>Present only in discovered (regardless of classification):
    ///     use the discovered value — nothing to preserve in stored.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static Uri MergeDiscoveredIntoStored(Uri storedUri, Uri discoveredUri)
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

        var builder = new UriBuilder(storedUri) { Query = merged.ToQueryString() };
        return builder.Uri;
    }
}
