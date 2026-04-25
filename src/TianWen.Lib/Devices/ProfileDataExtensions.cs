using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices;

/// <summary>
/// Extension block helpers for <see cref="ProfileData"/>.
/// </summary>
public static class ProfileDataExtensions
{
    extension(ProfileData profile)
    {
        /// <summary>
        /// True if any URI slot in the profile references a fake device. Used at
        /// startup to opt the first device discovery into <c>IncludeFake: true</c> so
        /// a profile set up with fake devices can actually connect without the user
        /// having to press Shift+Discover first.
        /// </summary>
        /// <remarks>
        /// Fake device URIs use the device type as the scheme and <c>FakeDevice</c> as
        /// the host (e.g. <c>Mount://FakeDevice/fake-sky</c>), not a dedicated
        /// <c>fakedevice://</c> scheme — see <c>EquipmentActions.TryDeviceFromUri</c>.
        /// </remarks>
        public bool ReferencesAnyFakeDevice
        {
            get
            {
                if (IsFake(profile.Mount)) return true;
                if (IsFake(profile.Guider)) return true;
                if (IsFake(profile.GuiderCamera)) return true;
                if (IsFake(profile.GuiderFocuser)) return true;
                if (IsFake(profile.Weather)) return true;

                foreach (var ota in profile.OTAs)
                {
                    if (IsFake(ota.Camera)) return true;
                    if (IsFake(ota.Cover)) return true;
                    if (IsFake(ota.Focuser)) return true;
                    if (IsFake(ota.FilterWheel)) return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Yields every non-empty, non-<c>NoneDevice</c> device URI assigned in the profile,
        /// in a stable order: Mount, Guider, GuiderCamera, GuiderFocuser, Weather, then per-OTA
        /// Camera, Cover, Focuser, FilterWheel. Useful for bulk operations like "Connect All"
        /// or "Disconnect All" that need to walk the profile's full device set without
        /// duplicating the slot list at every call site.
        /// </summary>
        public IEnumerable<Uri> AssignedDeviceUris
        {
            get
            {
                if (IsAssigned(profile.Mount)) yield return profile.Mount;
                if (IsAssigned(profile.Guider)) yield return profile.Guider;
                if (IsAssigned(profile.GuiderCamera)) yield return profile.GuiderCamera!;
                if (IsAssigned(profile.GuiderFocuser)) yield return profile.GuiderFocuser!;
                if (IsAssigned(profile.Weather)) yield return profile.Weather!;

                foreach (var ota in profile.OTAs)
                {
                    if (IsAssigned(ota.Camera)) yield return ota.Camera;
                    if (IsAssigned(ota.Cover)) yield return ota.Cover!;
                    if (IsAssigned(ota.Focuser)) yield return ota.Focuser!;
                    if (IsAssigned(ota.FilterWheel)) yield return ota.FilterWheel!;
                }
            }
        }
    }

    private static bool IsFake(Uri? uri)
        => uri is not null
           && uri.Host.Equals("FakeDevice", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssigned(Uri? uri)
        => uri is not null && uri != NoneDevice.Instance.DeviceUri;
}
