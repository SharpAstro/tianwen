using System;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Denormalized info-panel data for a selected sky-map object: name, coordinates,
/// brightness, and rise/transit/set for the current site and viewing time.
/// Built once when the selection changes; the panel redraws cheaply from it.
/// </summary>
public readonly record struct SkyMapInfoPanelData(
    string Name,
    string Canonical,
    ObjectType ObjType,
    Constellation Constellation,
    double RA,
    double Dec,
    float VMag,
    float BMinusV,
    double AltDeg,
    double AzDeg,
    DateTimeOffset? RiseTime,
    DateTimeOffset? TransitTime,
    DateTimeOffset? SetTime,
    bool Circumpolar,
    bool NeverRises,
    double? AngularSizeDeg,
    CelestialObjectShape? Shape,
    CatalogIndex? Index,
    // Surface brightness in mag/arcsec² (OpenNGC), NaN when unknown or not applicable (positions,
    // planets, comets). Trailing-optional so only FromCatalogObject sets it; every other builder
    // gets NaN and the panel simply omits the row.
    float SurfaceBrightness = float.NaN,
    bool IsMount = false,
    // Which fixed reference point (if any) this selection is. SkyFixedPoint.Zenith flags the
    // panel as horizon-relative so the renderer re-resolves it to the current overhead point each
    // frame instead of freezing it at the click-time RA/Dec (see SkyFixedPoint).
    SkyFixedPoint FixedPoint = SkyFixedPoint.None)
{
    /// <summary>
    /// Build a panel payload for a catalog object at the given site and viewing time.
    /// Alt/az come from the same <see cref="SiteContext"/> the sky map uses; rise/set
    /// use <see cref="RiseTransitSetHelper"/> (Meeus alg. 15, fixed RA/Dec only).
    /// </summary>
    public static SkyMapInfoPanelData FromCatalogObject(
        in CelestialObject obj,
        double siteLat, double siteLon,
        DateTimeOffset viewingUtc,
        in SiteContext site,
        CelestialObjectShape? shape)
    {
        var (altDeg, azDeg) = ComputeAltAz(obj.RA, obj.Dec, site);

        RiseTransitSetHelper.TryComputeRiseTransitSet(
            obj.RA, obj.Dec, siteLat, siteLon, viewingUtc,
            out var rise, out var transit, out var set,
            out var circumpolar, out var neverRises);

        double? angularSizeDeg = null;
        if (shape is { } s)
        {
            var major = (double)s.MajorAxis;
            if (!double.IsNaN(major) && major > 0)
            {
                angularSizeDeg = major / 60.0;
            }
        }

        return new SkyMapInfoPanelData(
            Name: obj.DisplayName,
            Canonical: obj.Index.ToCanonical(),
            ObjType: obj.ObjectType,
            Constellation: obj.Constellation,
            RA: obj.RA,
            Dec: obj.Dec,
            VMag: (float)obj.V_Mag,
            BMinusV: (float)obj.BMinusV,
            AltDeg: altDeg,
            AzDeg: azDeg,
            RiseTime: circumpolar || neverRises ? null : rise,
            TransitTime: neverRises ? null : transit,
            SetTime: circumpolar || neverRises ? null : set,
            Circumpolar: circumpolar,
            NeverRises: neverRises,
            AngularSizeDeg: angularSizeDeg,
            Shape: shape,
            Index: obj.Index,
            SurfaceBrightness: (float)obj.SurfaceBrightness);
    }

    /// <summary>
    /// Build a panel payload for a position or name with no catalog entry (Position
    /// tab, SIMBAD result, click-on-empty). <paramref name="raHours"/> and
    /// <paramref name="decDeg"/> are J2000.
    /// </summary>
    public static SkyMapInfoPanelData FromPosition(
        string name, double raHours, double decDeg,
        double siteLat, double siteLon,
        DateTimeOffset viewingUtc,
        in SiteContext site)
    {
        var (altDeg, azDeg) = ComputeAltAz(raHours, decDeg, site);

        RiseTransitSetHelper.TryComputeRiseTransitSet(
            raHours, decDeg, siteLat, siteLon, viewingUtc,
            out var rise, out var transit, out var set,
            out var circumpolar, out var neverRises);

        return new SkyMapInfoPanelData(
            Name: name,
            Canonical: "",
            ObjType: ObjectType.Unknown,
            Constellation: default,
            RA: raHours,
            Dec: decDeg,
            VMag: float.NaN,
            BMinusV: float.NaN,
            AltDeg: altDeg,
            AzDeg: azDeg,
            RiseTime: circumpolar || neverRises ? null : rise,
            TransitTime: neverRises ? null : transit,
            SetTime: circumpolar || neverRises ? null : set,
            Circumpolar: circumpolar,
            NeverRises: neverRises,
            AngularSizeDeg: null,
            Shape: null,
            Index: null);
    }

    /// <summary>
    /// Build a panel payload for the mount marker (the believed pointing reticle).
    /// Same fields as <see cref="FromPosition"/> with <see cref="IsMount"/> set so the
    /// panel swaps the Goto / View / Pin actions for "Solve &amp; Sync" - a goto to the
    /// mount's own reported position is a no-op, and the truthful action is to find
    /// out where it ACTUALLY points.
    /// </summary>
    public static SkyMapInfoPanelData FromMount(
        string name, double raHours, double decDeg,
        double siteLat, double siteLon,
        DateTimeOffset viewingUtc,
        in SiteContext site)
        => FromPosition(name, raHours, decDeg, siteLat, siteLon, viewingUtc, site) with { IsMount = true };

    /// <summary>
    /// Returns a copy with Alt/Az recomputed for the current <paramref name="site"/>, leaving the
    /// (viewing-time-independent) RA/Dec and the date-resolved rise/transit/set untouched. Alt/Az are
    /// horizon coordinates -- they swing with the hour angle even for a fixed-RA/Dec star or DSO -- so a
    /// selection panel left open while the map is time-scrubbed must refresh them or it shows a stale
    /// altitude (the "Helix still says Alt -30 four hours later" bug). Deliberately cheap: only the O(1)
    /// <see cref="ComputeAltAz"/> trig runs, so this is safe to call every frame while the panel is
    /// visible. Rise/transit/set are NOT recomputed here -- they only shift on a DATE change, and the
    /// iterative Meeus solve is too heavy to re-run per frame. Solar-system bodies and the zenith take
    /// the fuller per-frame re-resolve instead (their RA/Dec move too); this is the fixed-object path.
    /// </summary>
    public SkyMapInfoPanelData WithLiveHorizontal(in SiteContext site)
    {
        if (!site.IsValid)
        {
            return this;
        }

        var (altDeg, azDeg) = ComputeAltAz(RA, Dec, site);
        return this with { AltDeg = altDeg, AzDeg = azDeg };
    }

    // Local-only alt/az from a SiteContext (fast; no SOFA pipeline needed for display).
    // For the precision plate-solving needs the full Transform path — but the info
    // panel displays alt/az to the nearest 0.1 deg, so this is plenty.
    private static (double AltDeg, double AzDeg) ComputeAltAz(
        double raHours, double decDeg, in SiteContext site)
    {
        if (!site.IsValid) return (double.NaN, double.NaN);

        var ha = (site.LST - raHours) * Math.PI / 12.0;
        var (sinDec, cosDec) = Math.SinCos(decDeg * Math.PI / 180.0);
        var (sinHa, cosHa) = Math.SinCos(ha);

        var sinAlt = site.SinLat * sinDec + site.CosLat * cosDec * cosHa;
        sinAlt = Math.Clamp(sinAlt, -1.0, 1.0);
        var alt = Math.Asin(sinAlt);

        // Azimuth from N through E, 0..360.
        var az = Math.Atan2(
            -cosDec * sinHa,
            site.CosLat * sinDec - site.SinLat * cosDec * cosHa);
        var azDeg = double.RadiansToDegrees(az);
        if (azDeg < 0) azDeg += 360.0;

        return (double.RadiansToDegrees(alt), azDeg);
    }
}
