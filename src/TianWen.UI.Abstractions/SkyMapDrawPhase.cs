namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Which half of the sky map a draw call records, for a host that puts its own content BETWEEN
    /// them -- the FITS viewer, which composites a photograph onto the sky it was taken from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The split is imagery against geometry, and that is the whole rule.</b> What the sky map
    /// PAINTS -- its twilight ground, the milky way, the star field -- is what a photograph of that
    /// same sky replaces where it covers, so it belongs behind. What the sky map DRAWS AS LINES --
    /// the coordinate grid, constellation figures and boundaries, the horizon, the meridian -- is
    /// annotation about the sky at large, and a line that stops at the edge of a photograph and
    /// resumes on the other side reads as broken rather than as occluded.
    /// </para>
    /// <para>
    /// Point annotations (object markers, planet and comet labels) stay with the imagery: they are
    /// occluded honestly, because the photograph shows those objects itself and the viewer draws its
    /// own overlay for them at full fidelity. A LINE has no such counterpart.
    /// </para>
    /// </remarks>
    public enum SkyMapDrawPhase
    {
        /// <summary>Everything, in one pass. What a host that gives the map a screen to itself uses.</summary>
        All = 0,

        /// <summary>The imagery only: twilight ground, milky way, horizon fill, star field.</summary>
        Backdrop = 1,

        /// <summary>The line geometry only: grid, Alt/Az grid, meridian, ecliptic, boundaries,
        /// figures, horizon, and the sensor / mosaic outlines.</summary>
        Lines = 2,
    }
}
