using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Optics;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// One of Saturn's rings: a flat annulus in the planet's equatorial plane (docs/plans/planetary-restoration.md, S1, #1231).
/// </summary>
/// <param name="Name">The ring's name, for a report.</param>
/// <param name="InnerKm">Its inner edge, km from Saturn's centre.</param>
/// <param name="OuterKm">Its outer edge, km from Saturn's centre.</param>
/// <param name="Level">Its lit face's brightness over the globe's map's mean albedo (<see cref="PlanetMap.MeanAlbedo"/>), drawn flat:
/// a ring is a layer of particles, not a surface, so it carries no limb darkening.</param>
/// <param name="OpticalDepth">Its normal optical depth: a ray crossing it at an elevation <c>e</c> above the ring plane keeps
/// <c>exp(-OpticalDepth / sin e)</c> of the light behind it, the globe's for the observer and the Sun's for the globe.</param>
public readonly record struct SaturnRing(string Name, double InnerKm, double OuterKm, double Level, double OpticalDepth)
{
    /// <summary>The fraction of the light behind the ring that passes through it along a ray at <paramref name="sinElevation"/> to the ring plane.</summary>
    public double Transmission(double sinElevation) => sinElevation == 0 ? 0 : Math.Exp(-OpticalDepth / Math.Abs(sinElevation));
}

/// <summary>
/// Saturn's rings as a render draws them (S1, #1231): annuli in the equatorial plane, whose tilt to the line of sight and position angle
/// on the sky are the ephemeris' (<see cref="PhysicalEphemeris"/>: the sub-observer planetocentric latitude, Meeus's B, and the pole's
/// position angle, his P). Nothing about where the rings lie is fitted.
/// </summary>
/// <remarks>
/// <see cref="Main"/>'s edges are the main rings' as NASA's Saturnian Rings Fact Sheet lists them: the C ring from 74,658 km, the B
/// ring 92,000 to 117,580, the Cassini division to 122,170 and the A ring to 136,775. The Encke gap (325 km) and the F ring lie below
/// any capture's resolution. Their levels and optical depths are NOMINAL, of the order published (the B ring the brightest and opaque,
/// the A ring dimmer and half transparent, the C ring and the division faint and nearly clear), not measured here: a twin calibrates
/// them against a capture (S3), and the limb fit takes the levels as free (S2).
/// </remarks>
public sealed record SaturnRings(ImmutableArray<SaturnRing> Rings)
{
    /// <summary>
    /// The outer edge of the A ring as Meeus's chapter 45 takes it: 375.35 arcsec of major axis at 1 AU, about 136,115 km, older than
    /// today's 136,775. His worked example is reproduced with it.
    /// </summary>
    public const double MeeusOuterEdgeKm = 375.35 / 2 / ShortExposurePsf.ArcsecPerRadian * Constants.KMAU;

    /// <summary>The main rings, C, B, the Cassini division and A, at their nominal levels and optical depths.</summary>
    public static SaturnRings Main { get; } = new SaturnRings(
    [
        new SaturnRing("C", 74658, 92000, Level: 0.12, OpticalDepth: 0.1),
        new SaturnRing("B", 92000, 117580, Level: 0.85, OpticalDepth: 2.0),
        new SaturnRing("Cassini division", 117580, 122170, Level: 0.1, OpticalDepth: 0.1),
        new SaturnRing("A", 122170, 136775, Level: 0.6, OpticalDepth: 0.5),
    ]);

    /// <summary>The outer edge of the outermost ring, km.</summary>
    public double OuterKm
    {
        get
        {
            var outer = 0.0;
            foreach (var ring in Rings)
            {
                outer = Math.Max(outer, ring.OuterKm);
            }
            return outer;
        }
    }

    /// <summary>
    /// The ring at <paramref name="radius"/> equatorial radii from Saturn's centre; false in a gap, inside the C ring and beyond the A.
    /// </summary>
    public bool TryRingAt(double radius, out SaturnRing ring)
    {
        var km = radius * PhysicalEphemeris.Radii(CatalogIndex.Saturn).Equatorial;
        foreach (var candidate in Rings)
        {
            if (km >= candidate.InnerKm && km < candidate.OuterKm)
            {
                ring = candidate;
                return true;
            }
        }
        ring = default;
        return false;
    }

    /// <summary>
    /// The apparent ellipse of a ring edge <paramref name="edgeKm"/> from the centre, as Meeus gives it (ch. 45): its major axis, the
    /// edge's whole diameter at the planet's distance, and its minor axis, that times the sine of B, both in arcsec.
    /// </summary>
    public static (double MajorArcsec, double MinorArcsec) ApparentAxes(in PlanetAspect aspect, double edgeKm)
    {
        var major = 2 * Math.Atan(edgeKm / (aspect.DistanceAu * Constants.KMAU)) * ShortExposurePsf.ArcsecPerRadian;
        return (major, major * Math.Abs(Math.Sin(aspect.SubObserverLatitudeCentric * Math.PI / 180)));
    }
}
