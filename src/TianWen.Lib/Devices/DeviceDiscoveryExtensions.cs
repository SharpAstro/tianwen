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
}
