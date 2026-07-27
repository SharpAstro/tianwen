using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.Api.Alpaca
{
    /// <summary>One device this node offers over Alpaca.</summary>
    /// <param name="AlpacaType">Lower-case ASCOM device type, as it appears in the URL
    /// (<c>camera</c>, <c>telescope</c>, <c>focuser</c>, <c>filterwheel</c>, <c>covercalibrator</c>,
    /// <c>switch</c>).</param>
    /// <param name="Number">Alpaca device number within its type, 0-based.</param>
    /// <param name="DeviceUri">The local URI this entry proxies -- the key for the hub and for the
    /// ownership lease.</param>
    /// <param name="DisplayName">Human name for the management API.</param>
    public readonly record struct AlpacaDeviceEntry(string AlpacaType, int Number, Uri DeviceUri, string DisplayName);

    /// <summary>
    /// Which of this node's devices are exposed over Alpaca, and under which device numbers.
    /// <para>
    /// <b>Built from the active profile, in profile order</b>, not from live discovery. Discovery order
    /// varies between scans (a USB enumeration race, a serial probe finishing sooner), and an Alpaca
    /// device number that moved between two calls would point a client at a different piece of hardware
    /// mid-session. Profile order is deterministic, and a client that re-reads
    /// <c>/management/v1/configureddevices</c> after a profile change sees the new mapping -- which is
    /// exactly what that endpoint is for.
    /// </para>
    /// <para>
    /// Devices are listed whether or not they are currently connected: Alpaca's own model is that a
    /// client discovers a device and then connects it. Filtering to hub-connected devices would make
    /// the list flicker and would break the standard "PUT connected=true first" client preamble.
    /// </para>
    /// </summary>
    public sealed class AlpacaDeviceCatalog
    {
        /// <summary>ASCOM's name for a TianWen mount.</summary>
        public const string Telescope = "telescope";

        private readonly ImmutableArray<AlpacaDeviceEntry> _entries;

        private AlpacaDeviceCatalog(ImmutableArray<AlpacaDeviceEntry> entries) => _entries = entries;

        /// <summary>Every exposed device, ordered by type then number.</summary>
        public ImmutableArray<AlpacaDeviceEntry> Entries => _entries;

        /// <summary>An empty catalog -- a node with no active profile offers no devices.</summary>
        public static AlpacaDeviceCatalog Empty { get; } = new AlpacaDeviceCatalog([]);

        /// <summary>
        /// Builds the catalog for one profile. Each device type is numbered independently from 0, in the
        /// order the profile declares them: mount, then per-OTA camera / focuser / filter wheel / cover,
        /// then the guide camera, then switches.
        /// </summary>
        public static AlpacaDeviceCatalog FromProfile(ProfileData? profile)
        {
            if (profile is not { } data)
            {
                return Empty;
            }

            var byType = new Dictionary<string, int>(StringComparer.Ordinal);
            var entries = ImmutableArray.CreateBuilder<AlpacaDeviceEntry>();

            void Add(Uri? uri, string alpacaType, string name)
            {
                if (uri is null || string.Equals(uri.Scheme, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // The same physical device can fill two slots (an OAG camera that is also an OTA camera).
                // Expose it once -- two device numbers for one driver would let a client believe it had
                // two cameras and interleave exposures on one sensor.
                if (entries.Any(e => e.DeviceUri == uri))
                {
                    return;
                }

                var number = byType.TryGetValue(alpacaType, out var next) ? next : 0;
                byType[alpacaType] = number + 1;
                entries.Add(new AlpacaDeviceEntry(alpacaType, number, uri, name));
            }

            Add(data.Mount, Telescope, "Mount");

            var otas = data.OTAs;
            for (var i = 0; i < otas.Length; i++)
            {
                var ota = otas[i];
                var label = string.IsNullOrWhiteSpace(ota.Name) ? $"OTA {i + 1}" : ota.Name;
                Add(ota.Camera, "camera", $"{label} camera");
                Add(ota.Focuser, "focuser", $"{label} focuser");
                Add(ota.FilterWheel, "filterwheel", $"{label} filter wheel");
                Add(ota.Cover, "covercalibrator", $"{label} cover/calibrator");
            }

            // The guider itself has NO Alpaca device type -- ASCOM models Camera, CoverCalibrator, Dome,
            // FilterWheel, Focuser, ObservingConditions, Rotator, SafetyMonitor, Switch and Telescope, and
            // nothing else. Guiding stays on the native v1 session plane, which is why that plane cannot
            // be replaced by this one.
            Add(data.GuiderCamera, "camera", "Guide camera");

            return new AlpacaDeviceCatalog(entries.ToImmutable());
        }

        /// <summary>Resolves a URL's <c>{deviceType}/{deviceNumber}</c> to a device.</summary>
        public bool TryResolve(string alpacaType, int deviceNumber, out AlpacaDeviceEntry entry)
        {
            foreach (var candidate in _entries)
            {
                if (candidate.Number == deviceNumber
                    && string.Equals(candidate.AlpacaType, alpacaType, StringComparison.OrdinalIgnoreCase))
                {
                    entry = candidate;
                    return true;
                }
            }

            entry = default;
            return false;
        }

        /// <summary>
        /// A stable per-device identifier for the management API. Derived from the device URI (minus its
        /// query, matching how the hub keys devices), so it survives a re-plug that changes the COM port
        /// but names a genuinely different device when the hardware changes.
        /// </summary>
        public static string UniqueId(Uri deviceUri) => deviceUri.GetLeftPart(UriPartial.Path);
    }
}
