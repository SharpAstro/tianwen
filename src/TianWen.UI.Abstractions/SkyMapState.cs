using System;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Mutable viewport state for the sky map tab. Tracks view center (RA/Dec),
    /// field of view, display toggles, and drag state.
    /// </summary>
    public class SkyMapState
    {
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
    }

    public enum SkyMapMode
    {
        Equatorial,
        Horizon
    }
}
