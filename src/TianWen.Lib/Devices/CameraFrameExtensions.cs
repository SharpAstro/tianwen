using System;

namespace TianWen.Lib.Devices;

/// <summary>
/// Setting a camera's binning and readout frame together: in the order its setters need, and snapped to what it can read
/// out. For a host that sets them on request, the device plane (P2 of docs/plans/hardware-in-the-server.md, #929).
/// </summary>
public static class CameraFrameExtensions
{
    extension(ICameraDriver camera)
    {
        /// <summary>
        /// Bins the camera <paramref name="bin"/> x <paramref name="bin"/> and reads out <paramref name="frame"/>, in binned
        /// pixels, or the whole binned sensor when it is null. The frame is snapped to the camera's
        /// <see cref="ICameraDriver.RoiConstraints"/> and kept on the binned sensor, whose extent a driver's constraints
        /// do not always shrink for binning. The binning is set FIRST: the frame's setters check against the binned
        /// sensor.
        /// </summary>
        /// <returns>The frame as set, after snapping.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The camera cannot bin that far.</exception>
        public RoiRect SetFrame(int bin, RoiRect? frame)
        {
            var maxBin = Math.Min(camera.MaxBinX, camera.MaxBinY);
            if (bin < 1 || bin > maxBin)
            {
                throw new ArgumentOutOfRangeException(nameof(bin), bin, $"Binning must be between 1 and {maxBin}");
            }

            camera.BinX = bin;
            camera.BinY = bin;

            var sensorWidth = camera.CameraXSize / bin;
            var sensorHeight = camera.CameraYSize / bin;
            var constraints = camera.RoiConstraints;
            constraints = constraints with
            {
                MaxWidth = Math.Min(constraints.MaxWidth, sensorWidth),
                MaxHeight = Math.Min(constraints.MaxHeight, sensorHeight),
            };
            var set = constraints.Snap(frame ?? new RoiRect(0, 0, sensorWidth, sensorHeight));

            camera.NumX = set.Width;
            camera.NumY = set.Height;
            camera.StartX = set.X;
            camera.StartY = set.Y;
            return set;
        }
    }
}
