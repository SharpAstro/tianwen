using System;

namespace TianWen.Lib.Devices;

public static class DeviceDiscoveryExtensions
{
    extension(IDeviceDiscovery discovery)
    {
        /// <summary>
        /// Cross-references a stored device URI against the current discovery cache.
        /// If a live candidate exists with the same identity (scheme + authority + path,
        /// i.e. matching <see cref="DeviceBase.DeviceId"/>) but a different query, returns
        /// the live URI. Otherwise returns the input URI unchanged.
        /// <para>
        /// Fixes the "I replugged the USB to a different hub and now COM5 is COM6" class of
        /// bug, and the analogous "WiFi mount got a new DHCP lease" case: the deviceId stays
        /// stable (mount-resident UUID, MAC, etc.), but the transport query parameters need
        /// to track the OS's or network's current assignment.
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

                // Same device identity. A different URI means the query params drifted
                // (new port / new IP) — discovered URI wins because it reflects current
                // OS / network state.
                return candidate.DeviceUri != storedUri ? candidate.DeviceUri : storedUri;
            }

            return storedUri;
        }
    }
}
