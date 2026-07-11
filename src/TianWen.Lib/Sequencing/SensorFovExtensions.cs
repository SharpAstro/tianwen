using System.Collections.Immutable;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Offline sensor field-of-view derivation from the profile's persisted focal length + sensor
/// geometry (auto-captured on first camera connect). Lets the planner compute smart framing groups
/// before any device is connected. Lives next to <see cref="MosaicGenerator"/> so the profile DTOs
/// stay pure data.
/// </summary>
public static class SensorFovExtensions
{
    extension(OTAData ota)
    {
        /// <summary>
        /// The camera sensor FOV (width, height) in degrees from the persisted focal length + pixel
        /// size + sensor dimensions, or <see langword="null"/> when any of those specs is missing
        /// (i.e. the camera has never connected to capture them). At binning 1 -- the framing preview
        /// is a geometry estimate, not the actual capture binning.
        /// </summary>
        public (double WidthDeg, double HeightDeg)? SensorFovDeg
        {
            get
            {
                if (ota.FocalLength > 0
                    && ota.CameraPixelSizeUm is { } px && px > 0
                    && ota.CameraSensorWidthPx is { } w && w > 0
                    && ota.CameraSensorHeightPx is { } h && h > 0)
                {
                    return MosaicGenerator.ComputeFieldOfView(ota.FocalLength, px, w, h);
                }

                return null;
            }
        }
    }

    extension(ProfileData profile)
    {
        /// <summary>
        /// Sensor FOV of the primary OTA (index 0), or <see langword="null"/> when there is no OTA or
        /// its sensor specs haven't been captured. The smart-framing planner uses the primary OTA's FOV
        /// as the framing rectangle.
        /// </summary>
        public (double WidthDeg, double HeightDeg)? PrimarySensorFovDeg
            => profile.OTAs is { Length: > 0 } otas ? otas[0].SensorFovDeg : null;
    }
}
