using System;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Business logic for the live planetary capture tab, kept out of <c>AppSignalHandler</c>'s routing
    /// lambdas (the signal handler only routes; logic lives here).
    /// </summary>
    public static class PlanetaryCaptureActions
    {
        /// <summary>
        /// Configures a camera's sub-frame (ROI) for planetary video: bin 1 + a <paramref name="width"/> x
        /// <paramref name="height"/> readout, each axis clamped to the sensor. Sets only the readout SIZE
        /// (NumX/NumY); the readout origin is left at the driver default for now -- the fake camera centres
        /// its synthetic disk in the ROI internally, and native cameras keep their default origin until the
        /// Phase-C recenter loop pans them. Returns the applied (width, height) after clamping.
        /// </summary>
        public static (int Width, int Height) ConfigureRoi(ICameraDriver camera, int width, int height)
        {
            // Planetary wants unbinned readout. BinX must be set before NumX/NumY (their setters validate
            // against the binned sensor size).
            if (camera.BinX != 1)
            {
                camera.BinX = 1;
            }
            if (camera.BinY != 1)
            {
                camera.BinY = 1;
            }

            // NumX/NumY must stay strictly below the (unbinned) sensor extent; clamp each axis independently
            // so a non-square ROI (e.g. 640x320) is honoured rather than squared off.
            var w = Math.Clamp(width, 16, Math.Max(16, camera.CameraXSize - 2));
            var h = Math.Clamp(height, 16, Math.Max(16, camera.CameraYSize - 2));
            camera.NumX = w;
            camera.NumY = h;
            return (w, h);
        }
    }
}
