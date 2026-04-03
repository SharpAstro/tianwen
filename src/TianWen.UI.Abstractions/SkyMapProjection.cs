using System;
using System.Runtime.CompilerServices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Stereographic projection for the sky map. Conformal (preserves angles),
    /// handles wide FOVs gracefully, same as Stellarium's default projection.
    /// Maps RA/Dec coordinates to screen pixels given a viewport center and FOV.
    /// </summary>
    public static class SkyMapProjection
    {
        private const double Deg2Rad = Math.PI / 180.0;
        private const double Rad2Deg = 180.0 / Math.PI;
        private const double Hours2Rad = Math.PI / 12.0;

        /// <summary>
        /// Project a celestial coordinate (RA/Dec) onto screen pixel coordinates
        /// using stereographic projection. Returns false if the point is at the
        /// antipode (exactly 180° from center).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Project(
            double ra, double dec,
            double centerRA, double centerDec,
            double pixelsPerRadian,
            float centerX, float centerY,
            out float screenX, out float screenY)
        {
            var dRA = (ra - centerRA) * Hours2Rad;
            var (sinDec, cosDec) = Math.SinCos(dec * Deg2Rad);
            var (sinDec0, cosDec0) = Math.SinCos(centerDec * Deg2Rad);
            var (sinDRA, cosDRA) = Math.SinCos(dRA);

            // Cosine of angular distance
            var cosD = sinDec0 * sinDec + cosDec0 * cosDec * cosDRA;

            // Stereographic: k = 2 / (1 + cos_dist). At antipode (cosD = -1), k → ∞
            if (cosD <= -0.99)
            {
                screenX = float.NaN;
                screenY = float.NaN;
                return false;
            }

            var k = 2.0 / (1.0 + cosD);
            var x = k * cosDec * sinDRA;
            var y = k * (cosDec0 * sinDec - sinDec0 * cosDec * cosDRA);

            // RA increases to the left (east), so negate X for screen coordinates
            screenX = centerX - (float)(x * pixelsPerRadian);
            screenY = centerY - (float)(y * pixelsPerRadian);
            return true;
        }

        /// <summary>
        /// Inverse stereographic projection: convert screen pixel coordinates back to RA/Dec.
        /// </summary>
        public static (double RA, double Dec) Unproject(
            float screenX, float screenY,
            double centerRA, double centerDec,
            double pixelsPerRadian,
            float centerX, float centerY)
        {
            var x = -(screenX - centerX) / pixelsPerRadian;
            var y = -(screenY - centerY) / pixelsPerRadian;

            var rho = Math.Sqrt(x * x + y * y);
            if (rho < 1e-12)
            {
                return (centerRA, centerDec);
            }

            // Stereographic inverse: c = 2 * atan(rho / 2)
            var c = 2.0 * Math.Atan(rho * 0.5);
            var (sinC, cosC) = Math.SinCos(c);
            var (sinDec0, cosDec0) = Math.SinCos(centerDec * Deg2Rad);

            var dec = Math.Asin(cosC * sinDec0 + y * sinC * cosDec0 / rho) * Rad2Deg;
            var ra = centerRA + Math.Atan2(x * sinC, rho * cosDec0 * cosC - y * sinDec0 * sinC) / Hours2Rad;

            ra = ((ra % 24.0) + 24.0) % 24.0;
            return (ra, dec);
        }

        /// <summary>
        /// Compute pixels-per-radian for stereographic projection.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double PixelsPerRadian(float viewportHeight, double fovDeg)
        {
            // Stereographic: fov/2 maps to 2*tan(fov/4) radians on the projection plane
            var quarterFovRad = fovDeg * 0.25 * Deg2Rad;
            return viewportHeight / (4.0 * Math.Tan(quarterFovRad));
        }

        /// <summary>
        /// Map star visual magnitude to a display radius in pixels.
        /// Scales with FOV so stars grow when zooming in (like Stellarium).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float StarRadius(float vMag, double fovDeg)
        {
            // Base radius from magnitude (at 60° reference FOV)
            const float basePx = 4.0f;
            var r = basePx * MathF.Pow(10f, -0.14f * vMag);

            // Scale with zoom: sqrt(60/fov) so stars grow when zooming in
            var zoomScale = MathF.Sqrt(60f / MathF.Max(1f, (float)fovDeg));
            r *= zoomScale;

            return MathF.Max(1.2f, MathF.Min(r, 15f));
        }

        /// <summary>
        /// Map B-V color index to an approximate RGB color for star rendering.
        /// </summary>
        public static (byte R, byte G, byte B) StarColor(float bMinusV)
        {
            if (float.IsNaN(bMinusV))
            {
                return (255, 255, 255);
            }

            var bv = Math.Clamp(bMinusV, -0.4f, 2.0f);

            // Piecewise linear approximation of blackbody color
            byte r, g, b;
            if (bv < 0.0f)
            {
                // Hot blue-white stars
                r = (byte)(155 + (int)(100 * (bv + 0.4f) / 0.4f));
                g = (byte)(175 + (int)(80 * (bv + 0.4f) / 0.4f));
                b = 255;
            }
            else if (bv < 0.4f)
            {
                // White to yellow-white
                r = 255;
                g = (byte)(255 - (int)(25 * bv / 0.4f));
                b = (byte)(255 - (int)(55 * bv / 0.4f));
            }
            else if (bv < 0.8f)
            {
                // Yellow-white to yellow
                r = 255;
                g = (byte)(230 - (int)(40 * (bv - 0.4f) / 0.4f));
                b = (byte)(200 - (int)(80 * (bv - 0.4f) / 0.4f));
            }
            else if (bv < 1.2f)
            {
                // Yellow to orange
                r = 255;
                g = (byte)(190 - (int)(50 * (bv - 0.8f) / 0.4f));
                b = (byte)(120 - (int)(60 * (bv - 0.8f) / 0.4f));
            }
            else
            {
                // Orange to red
                r = 255;
                g = (byte)(140 - (int)(40 * Math.Min((bv - 1.2f) / 0.8f, 1.0f)));
                b = (byte)(60 - (int)(40 * Math.Min((bv - 1.2f) / 0.8f, 1.0f)));
            }

            return (r, g, b);
        }
    }
}
