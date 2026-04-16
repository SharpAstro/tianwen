using System;

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
    }

    private static bool IsFake(Uri? uri)
        => uri is not null
           && uri.Host.Equals("FakeDevice", StringComparison.OrdinalIgnoreCase);
}
