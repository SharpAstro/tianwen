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

        /// <summary>Show Alt/Az coordinate grid (A key toggles mode + grid).</summary>
        public bool ShowAltAzGrid { get; set; }

        /// <summary>Cached view matrix, updated each frame by the rendering layer.</summary>
        public Matrix4x4 CurrentViewMatrix { get; set; } = Matrix4x4.Identity;

        // Drag state
        public bool IsDragging { get; set; }
        public (float X, float Y) DragStart { get; set; }
        public (double RA, double Dec) DragStartCenter { get; set; }
        /// <summary>View matrix at drag start — needed for correct unproject during drag.</summary>
        public Matrix4x4 DragStartViewMatrix { get; set; }

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
        /// In equatorial mode, "up" is toward the celestial north pole.
        /// In horizon mode, "up" is toward the local zenith (horizon stays horizontal).
        /// Returns a <see cref="Matrix4x4"/> (column-major layout matches std140 mat4).
        /// </summary>
        /// <param name="zenithX">J2000 X component of the local zenith (only used in Horizon mode).</param>
        /// <param name="zenithY">J2000 Y component of the local zenith.</param>
        /// <param name="zenithZ">J2000 Z component of the local zenith.</param>
        public Matrix4x4 ComputeViewMatrix(float zenithX = 0f, float zenithY = 0f, float zenithZ = 1f)
        {
            var (sinRA, cosRA) = Math.SinCos(CenterRA * Hours2Rad);
            var (sinDec, cosDec) = Math.SinCos(CenterDec * Deg2Rad);

            // Forward direction: unit vector toward (CenterRA, CenterDec)
            var fx = (float)(cosDec * cosRA);
            var fy = (float)(cosDec * sinRA);
            var fz = (float)sinDec;

            // "Up" reference direction depends on mode:
            // Equatorial: celestial north pole (0, 0, 1)
            // Horizon: local zenith (cosLat*cosLST, cosLat*sinLST, sinLat)
            float upRefX, upRefY, upRefZ;
            if (Mode == SkyMapMode.Horizon)
            {
                upRefX = zenithX;
                upRefY = zenithY;
                upRefZ = zenithZ;
            }
            else
            {
                upRefX = 0f;
                upRefY = 0f;
                upRefZ = 1f;
            }

            // Right = forward × upRef, then normalize
            var rx = fy * upRefZ - fz * upRefY;
            var ry = fz * upRefX - fx * upRefZ;
            var rz = fx * upRefY - fy * upRefX;
            var rLen = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
            if (rLen > 1e-6f)
            {
                rx /= rLen;
                ry /= rLen;
                rz /= rLen;
            }
            else
            {
                // Forward is parallel to up reference — pick an arbitrary right vector
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
