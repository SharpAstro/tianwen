using System;

namespace TianWen.Lib.Devices;

public static class DeviceUriExtensions
{
    extension(Uri uri)
    {
        /// <summary>
        /// The IDENTITY of a device URI: scheme, host and path, without the query. The query carries
        /// settings (<c>?latitude=...&amp;port=...</c>) that reconciliation and re-discovery rewrite, so two
        /// URIs naming the same device routinely differ there. <see cref="IDeviceHub"/> keys connected
        /// devices by this and so must anyone matching a connected device against a profile's URI -- the
        /// mount-limit watcher compared whole URIs once and silently skipped any profile whose mount query
        /// had drifted from the connected one. Compare with <see cref="StringComparison.OrdinalIgnoreCase"/>.
        /// </summary>
        public string DeviceKey => uri.GetLeftPart(UriPartial.Path);
    }
}
