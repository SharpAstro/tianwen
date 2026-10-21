using System;
using System.Web;
using TianWen.Lib.Connections;

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
/// Skywatcher and SkyGuider Pro probes, an LX200/OnStep mount without a UUID, and a Canon through Windows' stock USB driver (WPD) put the PORT
/// (or USB path) in it, so their key
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

        /// <summary>
        /// The device's MODEL, for a family whose ID ends in its serial port because the hardware has no identity of its own
        /// (a CH340's Gemini focuser, flat panel and power box, a QHY wheel or focuser, a Skywatcher, a SkyGuider Pro, an
        /// LX200 or OnStep mount without a UUID): <see cref="DeviceKey"/> with the trailing <c>_&lt;port&gt;</c> taken out, so
        /// <c>GeminiFocuser_COM3</c> and <c>GeminiFocuser_COM4</c> are one model. Null for a device whose ID does not end in
        /// the port its query names, which its key alone identifies. Compare with <see cref="DeviceKeyComparer"/> (#1744).
        /// </summary>
        public string? ModelKey
        {
            get
            {
                if (HttpUtility.ParseQueryString(uri.Query)[DeviceQueryKey.Port.Key] is not { Length: > 0 } port)
                {
                    return null;
                }

                var key = Uri.UnescapeDataString(uri.DeviceKey);
                var bare = ISerialConnection.RemoveProtoPrrefix(port);
                // The forms a probe writes the port in: as named, with /dev/ for a tty, or made path-safe.
                foreach (var form in (ReadOnlySpan<string>)[bare, ISerialConnection.CleanupPortName(port), bare.Replace('_', '-').Replace('/', '-').Replace(':', '-')])
                {
                    var suffix = "_" + form;
                    if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && key.Length > suffix.Length && key[^(suffix.Length + 1)] != '/')
                    {
                        return key[..^suffix.Length];
                    }
                }

                return null;
            }
        }
    }
}
