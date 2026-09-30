using System;

namespace TianWen.Lib.Devices;

/// <summary>
/// The ONE definition of a device's identity key, and the one comparer for it. Nothing else computes the key
/// (<c>DeviceKeyHasOneDefinitionTests</c> fails on a second <c>GetLeftPart(UriPartial.Path)</c>), and every map
/// keyed by it, and every comparison of two, goes through <see cref="DeviceKeyComparer"/>.
/// </summary>
/// <remarks>
/// Three notions of a device, for three jobs:
/// <list type="bullet">
///   <item><see cref="DeviceKey"/> (scheme, host and path): WHICH DEVICE, for matching a connected driver, a lease, a
///   job or a reading against a profile's URI. <see cref="DeviceBase.SameDevice"/> compares by it.</item>
///   <item><see cref="DeviceBase.DeviceId"/> (the path alone, no scheme or host): a file or store NAME for the
///   device, keying the credential store, <c>Profiles/BacklashHistory/&lt;id&gt;.json</c> and profile lookup. Never
///   compare two devices by it; two device types can share one.</item>
///   <item>The whole URI, query included: the device AS CONFIGURED (port, site, gain), which is what a driver is
///   built from, and what the hub compares to decide a down driver can be reconnected rather than rebuilt.</item>
/// </list>
/// The key is only as stable as the path a device source writes. For most families it names the hardware, but the
/// Skywatcher and SkyGuider Pro probes, an LX200/OnStep mount without a UUID, and a Canon over WPD put the PORT (or USB
/// path) in it, so their key
/// changes when the port does: <c>docs/architecture/device-architecture.md</c>, "Device keys by family".
/// </remarks>
public static class DeviceUriExtensions
{
    /// <summary>The one comparer for <see cref="DeviceKey"/>s: ordinal, case-insensitive.</summary>
    public static StringComparer DeviceKeyComparer => StringComparer.OrdinalIgnoreCase;

    extension(Uri uri)
    {
        /// <summary>
        /// The IDENTITY of a device URI: scheme, host and path, without the query. The query carries
        /// settings (<c>?latitude=...&amp;port=...</c>) that reconciliation and re-discovery rewrite, so two
        /// URIs naming the same device routinely differ there. <see cref="IDeviceHub"/> keys connected
        /// devices by this and so must anyone matching a connected device against a profile's URI -- the
        /// mount-limit watcher compared whole URIs once and silently skipped any profile whose mount query
        /// had drifted from the connected one. Compare with <see cref="DeviceKeyComparer"/>.
        /// </summary>
        public string DeviceKey => uri.GetLeftPart(UriPartial.Path);
    }
}
