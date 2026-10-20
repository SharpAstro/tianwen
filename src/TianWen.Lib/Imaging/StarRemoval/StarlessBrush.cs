using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>A catalogued star placed on a master through its WCS: frame pixels in the detected-centroid convention
/// (0-based), its catalogue magnitude, and whether it is MOVING (a significant proper motion or parallax, which a galaxy's
/// own clusters and HII regions never have).</summary>
public readonly record struct CatalogueStar(float X, float Y, float Magnitude, bool Moving);

/// <summary>
/// R0's brush (docs/plans/star-remover-training.md, "The brush: clean plates by construction"): after the classical builder
/// has taken its stars out, every star of a catalogue the master SHOWS is painted over out to where its light, halo included,
/// falls under <see cref="NoiseFraction"/> of the local noise, with a surface fitted in a ring outside the footprint and grain
/// of the master's own level and shape. Inside <see cref="Protect"/> (a distant galaxy) only a MOVING catalogue star is
/// removed, and a source the builder took there that no moving star explains is put back: that is the galaxy's own cluster
/// or HII region. A nebula is not protected that way, since a point source on it is a star; its structure is kept by the
/// fill itself, which leaves the builder's plate where a footprint's ring is no plane and only raises its pits.
/// </summary>
/// <param name="Catalogue">The catalogue on this master (Gaia DR3 to about G 20.5, <see cref="ReadCatalogue"/>).</param>
/// <param name="Protect">The extended sources to keep (<see cref="BuildProtect"/>); null protects nothing.</param>
/// <param name="NoiseFraction">A footprint reaches out to where the light its star still adds at the edge, which the surface
/// inherits over the whole disc, would stand under this fraction of the local pixel noise summed over that disc: the eye sums
/// a disc, so +0.2 sigma over 30 px is a blob though no pixel shows it.</param>
/// <param name="RingPx">The ring the surface is fitted in, pixels wide, outside every footprint.</param>
/// <param name="SeenSigma">A catalogue star the builder did not take is brushed only where the master shows it: its 3x3 mean
/// this many half-noises over its own ring.</param>
public sealed record StarlessBrush(
    ImmutableArray<CatalogueStar> Catalogue,
    BitMatrix? Protect = null,
    float NoiseFraction = 1f,
    float RingPx = 6f,
    float SeenSigma = 2f)
{
    /// <summary>How much larger than its catalogued outline (the 25 B-mag isophote) a protected object is drawn.</summary>
    public const double DefaultOutlineScale = 1.3;

    /// <summary>
    /// Reads a catalogue CSV (<c>ra_deg,dec_deg,gmag,moving</c>, as <c>training/denoise/gaia_brush_catalogue.py</c> writes it)
    /// and places every row through <paramref name="wcs"/>, keeping those inside the frame.
    /// </summary>
    public static ImmutableArray<CatalogueStar> ReadCatalogue(string csvPath, WCS wcs, int width, int height)
    {
        var stars = ImmutableArray.CreateBuilder<CatalogueStar>();
        using var reader = new StreamReader(csvPath);
        var header = reader.ReadLine() ?? throw new InvalidDataException($"{csvPath} is empty");
        var columns = header.Split(',');
        var ra = Array.IndexOf(columns, "ra_deg");
        var dec = Array.IndexOf(columns, "dec_deg");
        var mag = Array.IndexOf(columns, "gmag");
        var moving = Array.IndexOf(columns, "moving");
        if (ra < 0 || dec < 0 || mag < 0 || moving < 0)
        {
            throw new InvalidDataException($"{csvPath}: expected the columns ra_deg, dec_deg, gmag, moving; found {header}");
        }
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length < columns.Length
                || !double.TryParse(parts[ra], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                || !double.TryParse(parts[dec], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                || !float.TryParse(parts[mag], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
                || wcs.SkyToPixel(a / 15.0, d) is not { } p
                || p.X < -0.5 || p.Y < -0.5 || p.X > width - 0.5 || p.Y > height - 0.5)
            {
                continue;
            }
            stars.Add(new CatalogueStar((float)p.X, (float)p.Y, m, parts[moving].Trim() == "1"));
        }
        return stars.ToImmutable();
    }

    /// <summary>Whether an object of this type is protected: a galaxy, whose clusters and HII regions are point sources that are
    /// not stars. A nebula is not (a point source on it is a star), nor a cluster (its resolved stars are what a starless
    /// plate removes).</summary>
    public static bool IsProtectedType(ObjectType type) => type switch
    {
        ObjectType.Galaxy or ObjectType.PairG or ObjectType.GinPair or ObjectType.InteractingG or ObjectType.StarburstG
            or ObjectType.HIIG or ObjectType.EmissionG or ObjectType.LowSurfBrghtG or ObjectType.BlueCompactG
            or ObjectType.Seyfert or ObjectType.Seyfert1 or ObjectType.Seyfert2 or ObjectType.LINER or ObjectType.AGN
            or ObjectType.RadioG or ObjectType.BrightestCG or ObjectType.CompactGrG or ObjectType.PartofG
            or ObjectType.GtowardsGroup or ObjectType.GtowardsCl => true,
        _ => false,
    };

    /// <summary>
    /// The protect mask: every catalogued galaxy whose outline reaches the frame, drawn at <paramref name="outlineScale"/>
    /// times its catalogued size, and the detector's extended sources (<paramref name="detected"/>) that touch one of those
    /// outlines. A galaxy whose outline is larger than the frame's diagonal is the field the frame sits in, not a source in it
    /// (the Large Magellanic Cloud round 30 Doradus), and is left out: its stars are resolved, and the frame's stars. The
    /// names of the catalogued objects drawn are added to <paramref name="drawn"/>.
    /// </summary>
    public static BitMatrix BuildProtect(
        ICelestialObjectDB db, WCS wcs, int width, int height, ExtendedCoreMask? detected = null,
        double outlineScale = DefaultOutlineScale, ICollection<string>? drawn = null)
    {
        var mask = new BitMatrix(height, width);
        var arcminPx = wcs.PixelScaleArcsec > 0 ? 60.0 / wcs.PixelScaleArcsec : double.NaN;
        var diagonal = Math.Sqrt((double)width * width + (double)height * height);
        foreach (var index in db.AllObjectIndices)
        {
            if (!db.TryLookupByIndex(index, out var obj) || !IsProtectedType(obj.ObjectType)
                || wcs.SkyToPixel(obj.RA, obj.Dec) is not { } at || !db.TryGetShape(index, out var shape)
                || Half.IsNaN(shape.MajorAxis) || !((double)shape.MajorAxis > 0) || !double.IsFinite(arcminPx))
            {
                continue;
            }
            var semiMajor = (double)shape.MajorAxis / 2.0 * outlineScale;
            var semiMinor = (Half.IsNaN(shape.MinorAxis) || !((double)shape.MinorAxis > 0) ? (double)shape.MajorAxis : (double)shape.MinorAxis) / 2.0 * outlineScale;
            var reach = semiMajor * arcminPx;
            if (reach > diagonal || at.X < -reach || at.Y < -reach || at.X > width + reach || at.Y > height + reach)
            {
                continue;
            }
            var pa = Half.IsNaN(shape.PositionAngle) ? 0.0 : (double)shape.PositionAngle;
            if (StampEllipse(mask, wcs, obj.RA, obj.Dec, at, semiMajor, semiMinor, pa) && drawn is not null)
            {
                drawn.Add(index.ToCanonical());
            }
        }
        if (detected is not null && mask.Any())
        {
            var outlines = Copy(mask);
            var gw = detected.GridWidth;
            foreach (var segment in detected.Segments)
            {
                foreach (var b in segment.Blocks)
                {
                    var (cx, cy) = ((b % gw) * ExtendedCoreMask.BlockPx + ExtendedCoreMask.BlockPx / 2, (b / gw) * ExtendedCoreMask.BlockPx + ExtendedCoreMask.BlockPx / 2);
                    if (outlines[cy, cx])
                    {
                        detected.Stamp(segment, mask);
                        break;
                    }
                }
            }
        }
        return mask;
    }

    private static BitMatrix Copy(BitMatrix source)
    {
        var copy = new BitMatrix(source.Rows, source.Columns);
        source.AllWords.CopyTo(copy.AllWords);
        return copy;
    }

    // An ellipse on the sky (semi-axes in arcminutes, position angle from north through east) drawn in frame pixels through
    // the WCS's local linear map at its centre, so a mirrored frame or a rotated one draws it where the sky has it.
    private static bool StampEllipse(BitMatrix mask, WCS wcs, double raHours, double decDeg, (double X, double Y) at, double semiMajor, double semiMinor, double paDeg)
    {
        static (double Ra, double Dec) Offset(double raHours, double decDeg, double arcmin, double angleDeg)
        {
            var (s, c) = Math.SinCos(double.DegreesToRadians(angleDeg));
            var dDec = arcmin / 60.0 * c;
            var dRa = arcmin / 60.0 * s / Math.Cos(double.DegreesToRadians(decDeg));
            return (raHours + dRa / 15.0, decDeg + dDec);
        }
        var (ra1, dec1) = Offset(raHours, decDeg, 1.0, paDeg);
        var (ra2, dec2) = Offset(raHours, decDeg, 1.0, paDeg + 90.0);
        if (wcs.SkyToPixel(ra1, dec1) is not { } p1 || wcs.SkyToPixel(ra2, dec2) is not { } p2)
        {
            return false;
        }
        // Pixels per arcminute along the major (e1) and the minor (e2) axis.
        var (e1x, e1y) = (p1.X - at.X, p1.Y - at.Y);
        var (e2x, e2y) = (p2.X - at.X, p2.Y - at.Y);
        var det = e1x * e2y - e1y * e2x;
        if (Math.Abs(det) < 1e-12)
        {
            return false;
        }
        var reach = semiMajor * Math.Max(Math.Sqrt(e1x * e1x + e1y * e1y), Math.Sqrt(e2x * e2x + e2y * e2y));
        var x0 = Math.Max(0, (int)Math.Floor(at.X - reach));
        var x1 = Math.Min(mask.Columns - 1, (int)Math.Ceiling(at.X + reach));
        var y0 = Math.Max(0, (int)Math.Floor(at.Y - reach));
        var y1 = Math.Min(mask.Rows - 1, (int)Math.Ceiling(at.Y + reach));
        var any = false;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var (dx, dy) = (x - at.X, y - at.Y);
                // Solve (dx, dy) = alpha e1 + beta e2 for alpha, beta in arcminutes.
                var alpha = (dx * e2y - dy * e2x) / det;
                var beta = (e1x * dy - e1y * dx) / det;
                if (alpha * alpha / (semiMajor * semiMajor) + beta * beta / (semiMinor * semiMinor) <= 1.0)
                {
                    mask[y, x] = true;
                    any = true;
                }
            }
        }
        return any;
    }
}

/// <summary>What the brush did on one master.</summary>
/// <param name="CatalogueInFrame">Catalogue stars on the frame.</param>
/// <param name="CatalogueMatched">Of those, matched to a source the builder found (within 1.5 FWHM).</param>
/// <param name="CatalogueSeen">Of the rest, shown by the master (<see cref="StarlessBrush.SeenSigma"/>) and brushed.</param>
/// <param name="Brushed">Footprints painted.</param>
/// <param name="Restored">Sources the builder took inside the protect mask that no moving catalogue star explains, put back.</param>
/// <param name="ProtectFraction">The share of the frame protected.</param>
/// <param name="BrushedFraction">The share of the frame painted.</param>
/// <param name="HaloReachPx">How far out the field's measured halo profile reaches before it is lost in its own noise.</param>
/// <param name="HaloStars">The stars the halo profile was measured on.</param>
/// <param name="MagnitudeZeroPoint">log10(peak) + 0.4 mag over matched unsaturated stars: a catalogue star's peak.</param>
/// <param name="MatchOffsetX">The median catalogue-minus-found offset, pixels.</param>
/// <param name="MatchOffsetY">The same in y.</param>
/// <param name="MatchSpreadPx">The robust spread of the offsets.</param>
/// <param name="NoiseResampleSigma">The <see cref="Degradation.NoiseField.Warped"/> resampling width the grain was drawn with;
/// a negative value is a master less correlated than bilinear resampling, drawn as Warped at 0 blended with white noise, the
/// warped field's share of the variance being one plus this.</param>
/// <param name="NoiseBandRatio">The master's band1 / band0 noise ratio that width was matched to.</param>
/// <param name="ChannelCorrelation">The noise's correlation between channels the grain was drawn with.</param>
/// <param name="Completeness">Per significance band (outside the protect mask), how many of the builder's subtracted stars
/// the catalogue holds.</param>
/// <param name="Regions">The connected regions the footprints make.</param>
/// <param name="PlaneShare">The share of painted pixels that took a ring's plane; the rest took the smooth fill (a region larger
/// than its biggest footprint, a crowded field, or one whose ring is no plane).</param>
/// <param name="KeptShare">The share of footprint pixels left as the builder made them: single stars on structure, whose ring
/// is no plane, that the builder had already subtracted.</param>
/// <param name="PitsRaised">Of those, the pixels the builder had dug more than three sigma under the smooth fill round them,
/// which took that fill.</param>
public sealed record StarlessBrushStatistics(
    int CatalogueInFrame, int CatalogueMatched, int CatalogueSeen, int Brushed, int Restored,
    double ProtectFraction, double BrushedFraction, float HaloReachPx, int HaloStars, double MagnitudeZeroPoint,
    double MatchOffsetX, double MatchOffsetY, double MatchSpreadPx,
    double NoiseResampleSigma, double NoiseBandRatio, double ChannelCorrelation,
    ImmutableArray<CatalogueCompleteness> Completeness, int Regions, double PlaneShare, double KeptShare, int PitsRaised);

/// <summary>Of the builder's subtracted stars in a significance band, how many a catalogue star matches.</summary>
public readonly record struct CatalogueCompleteness(float MinSignificance, float MaxSignificance, int Stars, int Matched);
