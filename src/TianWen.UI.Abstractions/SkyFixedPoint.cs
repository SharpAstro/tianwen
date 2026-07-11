namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// A selectable fixed reference point on the sky map (the amber reticles). Distinguishes points
    /// whose sky coordinates are constant in the equatorial frame (the celestial poles, fixed at
    /// Dec +/-90) from the <see cref="Zenith"/>, which is horizon-relative -- its RA (= LST) and
    /// Dec (= latitude) both advance with the viewing time. A zenith selection must therefore
    /// re-resolve to the current overhead point every frame (the same live treatment planets get)
    /// or its crosshair drifts off the marker when the map is date-/time-scrubbed.
    /// </summary>
    public enum SkyFixedPoint
    {
        /// <summary>Not a fixed reference point -- an ordinary catalog object, position, or mount.</summary>
        None = 0,

        /// <summary>The local zenith (horizon-relative: re-resolved to the live overhead point each frame).</summary>
        Zenith,

        /// <summary>North celestial pole (Dec +90, equatorially fixed -- no re-resolution needed).</summary>
        NorthCelestialPole,

        /// <summary>South celestial pole (Dec -90, equatorially fixed -- no re-resolution needed).</summary>
        SouthCelestialPole,
    }
}
