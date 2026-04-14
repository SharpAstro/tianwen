using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Mutable viewport state for the sky map tab. Tracks view center (RA/Dec),
    /// field of view, display toggles, and drag state.
    /// </summary>
    public class SkyMapState
    {
        private const double Hours2Rad = Math.PI / 12.0;
        private const double Deg2Rad = Math.PI / 180.0;
        private const float Hours2RadF = MathF.PI / 12f;
        private const float Deg2RadF = MathF.PI / 180f;

        /// <summary>Viewport center RA in hours (J2000), range [0, 24).</summary>
        public double CenterRA { get; set; } = 0.0;

        /// <summary>Viewport center Dec in degrees (J2000), range [-90, +90].</summary>
        public double CenterDec { get; set; } = 0.0;

        /// <summary>True once the view has been initialized from site coordinates.</summary>
        public bool Initialized { get; set; }

        /// <summary>Full viewport vertical field of view in degrees, range [0.5, 180].</summary>
        public double FieldOfViewDeg { get; set; } = 60.0;

        /// <summary>Display mode: equatorial (RA/Dec grid) or horizon (Alt/Az grid).</summary>
        public SkyMapMode Mode { get; set; } = SkyMapMode.Equatorial;

        // Display toggles
        /// <summary>Show constellation boundary outlines (B key).</summary>
        public bool ShowConstellationBoundaries { get; set; } = true;

        /// <summary>Show horizon line and clip below-horizon stars (H key).</summary>
        public bool ShowHorizon { get; set; } = true;

        /// <summary>Show constellation stick figures (C key).</summary>
        public bool ShowConstellationFigures { get; set; } = true;
        public bool ShowGrid { get; set; } = true;
        public bool ShowPlanets { get; set; } = true;

        // Drag state
        public bool IsDragging { get; set; }
        public (float X, float Y) DragStart { get; set; }
        public (double RA, double Dec) DragStartCenter { get; set; }

        /// <summary>Magnitude limit for displayed stars. Brighter = lower number.</summary>
        public float MagnitudeLimit { get; set; } = 6.5f;

        /// <summary>True when viewport changed and the cached texture must be re-rendered.</summary>
        public bool NeedsRedraw { get; set; } = true;

        /// <summary>
        /// Clamp RA to [0, 24) and Dec to [-90, +90] after any modification.
        /// </summary>
        public void NormalizeCenter()
        {
            CenterRA = ((CenterRA % 24.0) + 24.0) % 24.0;
            // Clamp Dec away from poles to avoid gnomonic projection singularity
            CenterDec = Math.Clamp(CenterDec, -89.5, 89.5);
        }

        /// <summary>
        /// Compute the J2000 → camera rotation matrix for the current view center.
        /// The matrix maps the view direction to -Z (camera forward), with X = right and Y = up.
        /// Returns a <see cref="Matrix4x4"/> (column-major layout matches std140 mat4).
        /// </summary>
        public Matrix4x4 ComputeViewMatrix()
        {
            var (sinRA, cosRA) = Math.SinCos(CenterRA * Hours2Rad);
            var (sinDec, cosDec) = Math.SinCos(CenterDec * Deg2Rad);

            // Forward direction: unit vector toward (CenterRA, CenterDec)
            var fx = (float)(cosDec * cosRA);
            var fy = (float)(cosDec * sinRA);
            var fz = (float)sinDec;

            // Right = forward × north, where north = (0, 0, 1)
            // cross(f, (0,0,1)) = (fy, -fx, 0), then normalize
            var rLen = MathF.Sqrt(fx * fx + fy * fy);
            float rx, ry, rz;
            if (rLen > 1e-6f)
            {
                rx = fy / rLen;
                ry = -fx / rLen;
                rz = 0f;
            }
            else
            {
                // At poles, pick an arbitrary right vector
                rx = 1f;
                ry = 0f;
                rz = 0f;
            }

            // Up = right × forward (already unit length since right ⊥ forward and both unit)
            var ux = ry * fz - rz * fy;
            var uy = rz * fx - rx * fz;
            var uz = rx * fy - ry * fx;

            // View matrix: rows are (right, up, -forward)
            // Matrix4x4 constructor takes row-major arguments (M11..M44)
            return new Matrix4x4(
                rx,  ry,  rz,  0f,
                ux,  uy,  uz,  0f,
                -fx, -fy, -fz, 0f,
                0f,  0f,  0f,  1f);
        }

        /// <summary>
        /// Convert RA (hours) and Dec (degrees) to a J2000 unit vector.
        /// Convention: X toward (RA=0h, Dec=0°), Y toward (RA=6h, Dec=0°), Z toward Dec=+90°.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (float X, float Y, float Z) RaDecToUnitVec(double raHours, double decDeg)
        {
            var (sinRA, cosRA) = MathF.SinCos((float)(raHours * Hours2RadF));
            var (sinDec, cosDec) = MathF.SinCos((float)(decDeg * Deg2RadF));
            return (cosDec * cosRA, cosDec * sinRA, sinDec);
        }
    }

    public enum SkyMapMode
    {
        Equatorial,
        Horizon
    }
}
