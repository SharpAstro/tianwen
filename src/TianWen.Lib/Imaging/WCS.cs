using nom.tam.fits;
using System;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Represents a FITS World Coordinate System (WCS) solution in the TAN (gnomonic) projection.
/// <para>
/// The primary positional parameters <see cref="CenterRA"/> and <see cref="CenterDec"/> give the
/// sky coordinates of the image centre in J2000.0. When a full astrometric solution is available,
/// the CD matrix (<see cref="CD1_1"/>, <see cref="CD1_2"/>, <see cref="CD2_1"/>, <see cref="CD2_2"/>)
/// and reference pixel (<see cref="CRPix1"/>, <see cref="CRPix2"/>) encode the complete affine
/// mapping between pixel and sky coordinates.
/// </para>
/// <para><b>FITS keywords stored:</b></para>
/// <list type="table">
///   <listheader><term>Keyword</term><description>Description</description></listheader>
///   <item><term>CTYPE1/2</term><description>Coordinate type: <c>RA---TAN</c> / <c>DEC--TAN</c></description></item>
///   <item><term>CRVAL1/2</term><description>Reference sky coordinate (RA in degrees, Dec in degrees)</description></item>
///   <item><term>CRPIX1/2</term><description>Reference pixel (1-based, typically image centre)</description></item>
///   <item><term>CD1_1..CD2_2</term><description>Linear transformation matrix (degrees/pixel), encoding scale, rotation, and any skew/flip</description></item>
///   <item><term>EQUINOX</term><description>Equinox of coordinates (always 2000.0)</description></item>
/// </list>
/// </summary>
/// <param name="CenterRA">J2000.0 RA of the reference pixel in 0..24h (hours)</param>
/// <param name="CenterDec">J2000.0 Dec of the reference pixel in -90..+90 degrees</param>
public record struct WCS(double CenterRA, double CenterDec)
{
    /// <summary>Reference pixel X (1-based FITS convention). Typically image centre: (Width + 1) / 2.0.</summary>
    public double CRPix1 { get; init; } = double.NaN;

    /// <summary>Reference pixel Y (1-based FITS convention). Typically image centre: (Height + 1) / 2.0.</summary>
    public double CRPix2 { get; init; } = double.NaN;

    /// <summary>Partial derivative ∂RA/∂x in degrees per pixel.</summary>
    public double CD1_1 { get; init; } = double.NaN;

    /// <summary>Partial derivative ∂RA/∂y in degrees per pixel.</summary>
    public double CD1_2 { get; init; } = double.NaN;

    /// <summary>Partial derivative ∂Dec/∂x in degrees per pixel.</summary>
    public double CD2_1 { get; init; } = double.NaN;

    /// <summary>Partial derivative ∂Dec/∂y in degrees per pixel.</summary>
    public double CD2_2 { get; init; } = double.NaN;

    /// <summary>
    /// Whether a full astrometric solution (CD matrix + reference pixel) is present,
    /// as opposed to just the centre coordinates.
    /// </summary>
    public readonly bool HasCDMatrix =>
        !double.IsNaN(CD1_1) && !double.IsNaN(CD1_2) &&
        !double.IsNaN(CD2_1) && !double.IsNaN(CD2_2) &&
        !double.IsNaN(CRPix1) && !double.IsNaN(CRPix2);

    /// <summary>
    /// Pixel scale in arcseconds per pixel, derived from the CD matrix determinant.
    /// Returns <see cref="double.NaN"/> if no CD matrix is available.
    /// </summary>
    public readonly double PixelScaleArcsec
    {
        get
        {
            if (!HasCDMatrix)
            {
                return double.NaN;
            }
            var det = Math.Abs(CD1_1 * CD2_2 - CD1_2 * CD2_1);
            return Math.Sqrt(det) * 3600.0;
        }
    }

    /// <summary>
    /// Read WCS parameters from a FITS file's primary HDU header.
    /// Reads CRVAL1/2, CRPIX1/2, CD matrix (or falls back to CDELT+CROTA2).
    /// </summary>
    public static WCS? FromFits(Fits fits)
    {
        var hdu = fits.ReadHDU();
        if (hdu?.Header is not { } header)
        {
            return default;
        }

        var ra = header.GetDoubleValue("CRVAL1");
        var dec = header.GetDoubleValue("CRVAL2");

        if (double.IsNaN(ra) || double.IsNaN(dec))
        {
            return default;
        }

        var wcs = new WCS(ra / 15.0, dec)
        {
            CRPix1 = header.GetDoubleValue("CRPIX1"),
            CRPix2 = header.GetDoubleValue("CRPIX2"),
        };

        // Try CD matrix first (preferred modern convention)
        var cd1_1 = header.GetDoubleValue("CD1_1");
        var cd1_2 = header.GetDoubleValue("CD1_2");
        var cd2_1 = header.GetDoubleValue("CD2_1");
        var cd2_2 = header.GetDoubleValue("CD2_2");

        if (!double.IsNaN(cd1_1) && !double.IsNaN(cd1_2) && !double.IsNaN(cd2_1) && !double.IsNaN(cd2_2))
        {
            wcs = wcs with
            {
                CD1_1 = cd1_1,
                CD1_2 = cd1_2,
                CD2_1 = cd2_1,
                CD2_2 = cd2_2,
            };
        }
        else
        {
            // Fall back to CDELT + CROTA2 (older convention)
            var cdelt1 = header.GetDoubleValue("CDELT1");
            var cdelt2 = header.GetDoubleValue("CDELT2");
            var crota2 = header.GetDoubleValue("CROTA2");

            if (!double.IsNaN(cdelt1) && !double.IsNaN(cdelt2))
            {
                if (double.IsNaN(crota2))
                {
                    crota2 = 0.0;
                }
                var rotRad = double.DegreesToRadians(crota2);
                var cosRot = Math.Cos(rotRad);
                var sinRot = Math.Sin(rotRad);

                wcs = wcs with
                {
                    CD1_1 = cdelt1 * cosRot,
                    CD1_2 = -cdelt2 * sinRot,
                    CD2_1 = cdelt1 * sinRot,
                    CD2_2 = cdelt2 * cosRot,
                };
            }
        }

        return wcs;
    }

    /// <summary>
    /// Writes WCS keywords to a FITS header using the CD matrix convention.
    /// </summary>
    public readonly void WriteToHeader(Header header)
    {
        header.AddCard(new HeaderCard("CTYPE1", "RA---TAN", "TAN (gnomonic) projection"));
        header.AddCard(new HeaderCard("CTYPE2", "DEC--TAN", "TAN (gnomonic) projection"));
        header.AddCard(new HeaderCard("EQUINOX", 2000.0, "J2000.0"));
        header.AddCard(new HeaderCard("CRVAL1", CenterRA * 15.0, "RA at reference pixel [deg]"));
        header.AddCard(new HeaderCard("CRVAL2", CenterDec, "Dec at reference pixel [deg]"));

        if (!double.IsNaN(CRPix1))
        {
            header.AddCard(new HeaderCard("CRPIX1", CRPix1, "Reference pixel X (1-based)"));
        }
        if (!double.IsNaN(CRPix2))
        {
            header.AddCard(new HeaderCard("CRPIX2", CRPix2, "Reference pixel Y (1-based)"));
        }

        if (HasCDMatrix)
        {
            header.AddCard(new HeaderCard("CD1_1", CD1_1, "dRA/dx [deg/pix]"));
            header.AddCard(new HeaderCard("CD1_2", CD1_2, "dRA/dy [deg/pix]"));
            header.AddCard(new HeaderCard("CD2_1", CD2_1, "dDec/dx [deg/pix]"));
            header.AddCard(new HeaderCard("CD2_2", CD2_2, "dDec/dy [deg/pix]"));
        }
    }
}
