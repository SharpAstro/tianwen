using System;
using System.Numerics;
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
            var (sinDec, cosDec) = Math.SinCos(double.DegreesToRadians(dec));
            var (sinDec0, cosDec0) = Math.SinCos(double.DegreesToRadians(centerDec));
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
            var (sinDec0, cosDec0) = Math.SinCos(double.DegreesToRadians(centerDec));

            var dec = double.RadiansToDegrees(Math.Asin(cosC * sinDec0 + y * sinC * cosDec0 / rho));
            var ra = centerRA + Math.Atan2(x * sinC, rho * cosDec0 * cosC - y * sinDec0 * sinC) / Hours2Rad;

            ra = ((ra % 24.0) + 24.0) % 24.0;
            return (ra, dec);
        }

        /// <summary>
        /// Project a celestial coordinate using the view matrix, matching the GPU shader exactly.
        /// Works in both equatorial and horizon modes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ProjectWithMatrix(
            double ra, double dec,
            in Matrix4x4 viewMatrix,
            double pixelsPerRadian,
            float centerX, float centerY,
            out float screenX, out float screenY)
        {
            var (x, y, z) = SkyMapState.RaDecToUnitVec(ra, dec);

            // Apply view matrix (same as GPU: viewMatrix * vec4(pos, 1.0))
            var cx = viewMatrix.M11 * x + viewMatrix.M12 * y + viewMatrix.M13 * z;
            var cy = viewMatrix.M21 * x + viewMatrix.M22 * y + viewMatrix.M23 * z;
            var cz = viewMatrix.M31 * x + viewMatrix.M32 * y + viewMatrix.M33 * z;

            // Stereographic projection in camera space (forward is -Z)
            var cosD = -cz;
            if (cosD <= -0.99f)
            {
                screenX = float.NaN;
                screenY = float.NaN;
                return false;
            }

            var k = 2.0 / (1.0 + cosD);
            // +cx matches GPU shader (view matrix encodes RA direction)
            screenX = centerX + (float)(cx * k * pixelsPerRadian);
            screenY = centerY - (float)(cy * k * pixelsPerRadian);
            return true;
        }

        /// <summary>
        /// Inverse projection using the view matrix. Converts screen pixel coordinates back to RA/Dec.
        /// Works in both equatorial and horizon modes.
        /// </summary>
        public static (double RA, double Dec) UnprojectWithMatrix(
            float screenX, float screenY,
            in Matrix4x4 viewMatrix,
            double pixelsPerRadian,
            float centerX, float centerY)
        {
            // Reverse the viewport mapping
            var px = (screenX - centerX) / pixelsPerRadian;
            var py = -(screenY - centerY) / pixelsPerRadian;

            var rho = Math.Sqrt(px * px + py * py);
            if (rho < 1e-12)
            {
                // At view center — recover forward direction from view matrix row 2 (negated)
                var fx = -viewMatrix.M31;
                var fy = -viewMatrix.M32;
                var fz = -viewMatrix.M33;
                var dec = double.RadiansToDegrees(Math.Asin(fz));
                var ra = Math.Atan2(fy, fx) / Hours2Rad;
                return (((ra % 24.0) + 24.0) % 24.0, dec);
            }

            // Inverse stereographic: (px, py) -> camera-space unit vector
            var c = 2.0 * Math.Atan(rho * 0.5);
            var (sinC, cosC) = Math.SinCos(c);
            var camX = sinC * px / rho;
            var camY = sinC * py / rho;
            var camZ = -cosC; // forward is -Z

            // Rotate back to J2000 (view matrix is orthogonal, inverse = transpose)
            var jx = viewMatrix.M11 * camX + viewMatrix.M21 * camY + viewMatrix.M31 * camZ;
            var jy = viewMatrix.M12 * camX + viewMatrix.M22 * camY + viewMatrix.M32 * camZ;
            var jz = viewMatrix.M13 * camX + viewMatrix.M23 * camY + viewMatrix.M33 * camZ;

            var decResult = double.RadiansToDegrees(Math.Asin(Math.Clamp(jz, -1.0, 1.0)));
            var raResult = Math.Atan2(jy, jx) / Hours2Rad;
            raResult = ((raResult % 24.0) + 24.0) % 24.0;
            return (raResult, decResult);
        }

        /// <summary>
        /// Compute pixels-per-radian for stereographic projection.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double PixelsPerRadian(float viewportHeight, double fovDeg)
        {
            // Stereographic: fov/2 maps to 2*tan(fov/4) radians on the projection plane
            var quarterFovRad = double.DegreesToRadians(fovDeg * 0.25);
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
