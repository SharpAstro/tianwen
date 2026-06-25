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
        /// <paramref name="height"/> readout SIZE, <b>snapped to the camera's <see cref="RoiConstraints"/></b>
        /// (step / alignment / min / max) -- the single source of truth, so e.g. a ZWO width rounds to a
        /// multiple of 8 and a height to a multiple of 2. Sets only the size (NumX/NumY); the readout origin
        /// is left at the driver default for now -- the fake centres its synthetic disk in the ROI, and native
        /// cameras keep their default origin until the Phase-C recenter loop pans them. Returns the applied
        /// (width, height) after snapping.
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

            // Snap the requested size to the camera's real ROI rule (free step-1 default for ASCOM / Alpaca;
            // the fake reports ZWO-style 8 / 2). Snap leaves the origin at 0 and the fake recentres the disk
            // internally, so only the size is applied here. Keep the size strictly below the sensor extent so
            // the NumX/NumY setters (which validate value < CameraXSize) accept it.
            var snapped = camera.RoiConstraints.Snap(new RoiRect(0, 0, width, height));
            var w = Math.Min(snapped.Width, Math.Max(16, camera.CameraXSize - camera.RoiConstraints.WidthStep));
            var h = Math.Min(snapped.Height, Math.Max(16, camera.CameraYSize - camera.RoiConstraints.HeightStep));
            camera.NumX = w;
            camera.NumY = h;
            return (w, h);
        }
    }
}
