using nom.tam.fits;
using System;
using System.Globalization;

namespace TianWen.Lib.Astrometry;

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
    /// SIP (Shupe et al. 2005) forward polynomial coefficients for the
    /// pixel-X axis. <c>null</c> when the WCS is linear-only. When set,
    /// shape is <c>[SipOrder + 1, SipOrder + 1]</c> with terms at
    /// <c>i + j ∈ [1, SipOrder]</c>; the (0, 0) entry is conventionally
    /// absent (absorbed by CRPIX1).
    /// </summary>
    public double[,]? SipA { get; init; }

    /// <summary>Companion to <see cref="SipA"/> for the pixel-Y axis.</summary>
    public double[,]? SipB { get; init; }

    /// <summary>
    /// SIP inverse polynomial coefficients for the pixel-X axis, used by
    /// <see cref="SkyToPixel"/> to avoid Newton iteration when going from
    /// sky to pixel through a distorted CD matrix.
    /// </summary>
    public double[,]? SipAP { get; init; }

    /// <summary>Companion to <see cref="SipAP"/> for the pixel-Y axis.</summary>
    public double[,]? SipBP { get; init; }

    /// <summary>
    /// SIP polynomial order (max <c>i + j</c> across A/B). 0 means the
    /// WCS is purely linear; <see cref="PixelToSky"/> and
    /// <see cref="SkyToPixel"/> skip the polynomial branch in that case.
    /// </summary>
    public int SipOrder { get; init; }

    /// <summary>
    /// True when forward SIP terms (<see cref="SipA"/> + <see cref="SipB"/>)
    /// are present and applicable.
    /// </summary>
    public readonly bool HasSip => SipOrder > 0 && SipA is not null && SipB is not null;

    /// <summary>
    /// True when inverse SIP terms (<see cref="SipAP"/> + <see cref="SipBP"/>)
    /// are present. Independent of <see cref="HasSip"/>: a header can carry
    /// only forward terms, in which case <see cref="SkyToPixel"/> falls back
    /// to one Newton iteration with the forward polynomial.
    /// </summary>
    public readonly bool HasInverseSip => SipOrder > 0 && SipAP is not null && SipBP is not null;

    /// <summary>
    /// Whether the CD matrix was constructed from approximate data (PIXSCALE + ANGLE)
    /// rather than from an actual plate solution.
    /// </summary>
    public bool IsApproximate { get; init; }

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
    /// Converts a pixel position (1-based FITS convention) to sky coordinates
    /// using the CD matrix and inverse gnomonic (TAN) deprojection.
    /// Returns <c>null</c> if no CD matrix is available.
    /// </summary>
    /// <param name="x">Pixel X (1-based).</param>
    /// <param name="y">Pixel Y (1-based).</param>
    /// <returns>RA in hours, Dec in degrees; or <c>null</c> if no CD matrix.</returns>
    public readonly (double RA, double Dec)? PixelToSky(double x, double y)
    {
        if (!HasCDMatrix)
        {
            return null;
        }

        // Pixel offset from reference pixel
        var dx = x - CRPix1;
        var dy = y - CRPix2;

        // Forward SIP distortion: u' = u + A(u, v), v' = v + B(u, v),
        // evaluated at the original (u, v). The CD matrix then maps the
        // *corrected* relative-pixel coords into intermediate world coords.
        if (HasSip)
        {
            var dxC = SipPolynomial.Apply(dx, dy, SipA!);
            var dyC = SipPolynomial.Apply(dx, dy, SipB!);
            dx += dxC;
            dy += dyC;
        }

        // Intermediate world coordinates (degrees) via CD matrix
        var u = CD1_1 * dx + CD1_2 * dy;
        var v = CD2_1 * dx + CD2_2 * dy;

        // Convert to radians for gnomonic deprojection
        var xi = double.DegreesToRadians(u);
        var eta = double.DegreesToRadians(v);

        // Reference point in radians
        var ra0 = CenterRA * (Math.PI / 12.0);  // hours → radians
        var (sinDec0, cosDec0) = Math.SinCos(double.DegreesToRadians(CenterDec));

        var rho = Math.Sqrt(xi * xi + eta * eta);
        if (rho < 1e-15)
        {
            return (CenterRA, CenterDec);
        }

        var (sinC, cosC) = Math.SinCos(Math.Atan(rho));

        var dec = double.RadiansToDegrees(Math.Asin(cosC * sinDec0 + eta * sinC * cosDec0 / rho));
        var ra = (ra0 + Math.Atan2(xi * sinC, rho * cosDec0 * cosC - eta * sinDec0 * sinC)) * (12.0 / Math.PI);

        // Normalize RA to [0, 24)
        if (ra < 0) ra += 24.0;
        if (ra >= 24.0) ra -= 24.0;

        return (ra, dec);
    }

    /// <summary>
    /// Converts sky coordinates to pixel position (1-based FITS convention)
    /// using the CD matrix inverse and gnomonic (TAN) projection.
    /// Returns <c>null</c> if no CD matrix is available or the CD matrix is singular.
    /// </summary>
    /// <param name="ra">RA in hours (0..24).</param>
    /// <param name="dec">Dec in degrees (-90..+90).</param>
    /// <returns>Pixel position (1-based); or <c>null</c> if no CD matrix or behind tangent plane.</returns>
    public readonly (double X, double Y)? SkyToPixel(double ra, double dec)
    {
        if (!HasCDMatrix)
        {
            return null;
        }

        // Reference point in radians
        var ra0 = CenterRA * (Math.PI / 12.0);
        var (sinDec0, cosDec0) = Math.SinCos(double.DegreesToRadians(CenterDec));

        // Target in radians
        var alpha = ra * (Math.PI / 12.0);
        var (sinDelta, cosDelta) = Math.SinCos(double.DegreesToRadians(dec));
        var deltaAlpha = alpha - ra0;

        var cosC = sinDec0 * sinDelta + cosDec0 * cosDelta * Math.Cos(deltaAlpha);
        if (cosC <= 0)
        {
            return null; // behind the tangent plane
        }

        // Gnomonic standard coordinates (radians)
        var xi = cosDelta * Math.Sin(deltaAlpha) / cosC;
        var eta = (cosDec0 * sinDelta - sinDec0 * cosDelta * Math.Cos(deltaAlpha)) / cosC;

        // Convert to degrees (intermediate world coordinates)
        var u = double.RadiansToDegrees(xi);
        var v = double.RadiansToDegrees(eta);

        // Invert CD matrix: (dx, dy) = CD⁻¹ · (u, v)
        var det = CD1_1 * CD2_2 - CD1_2 * CD2_1;
        if (Math.Abs(det) < 1e-20)
        {
            return null;
        }

        var dx = (CD2_2 * u - CD1_2 * v) / det;
        var dy = (-CD2_1 * u + CD1_1 * v) / det;

        // Inverse SIP: x = U + F(U, V), y = V + G(U, V), with F/G evaluated
        // at the post-CD-inverse coords (U, V) ≡ pre-correction (dx, dy).
        // Capture corrections in temps so both polynomials see the same input.
        if (HasInverseSip)
        {
            var dxC = SipPolynomial.Apply(dx, dy, SipAP!);
            var dyC = SipPolynomial.Apply(dx, dy, SipBP!);
            dx += dxC;
            dy += dyC;
        }
        else if (HasSip)
        {
            // Fall back to one Newton iteration of the forward polynomial:
            // we want (dx_obs, dy_obs) such that (dx_obs + A(dx_obs, dy_obs),
            // dy_obs + B(dx_obs, dy_obs)) = (dx, dy). Start from (dx, dy) and
            // subtract the forward correction evaluated there. One step is
            // enough for the small (<1 px) corrections SIP typically produces.
            var dxC = SipPolynomial.Apply(dx, dy, SipA!);
            var dyC = SipPolynomial.Apply(dx, dy, SipB!);
            dx -= dxC;
            dy -= dyC;
        }

        return (CRPix1 + dx, CRPix2 + dy);
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

        return FromHeader(header);
    }

    /// <summary>
    /// Read WCS parameters from a FITS header.
    /// </summary>
    public static WCS? FromHeader(Header header)
    {
        // Try CRVAL1/2 first (degrees), then RA/DEC (degrees), then OBJCTRA/OBJCTDEC (HMS/DMS strings)
        // IMPORTANT: Always pass double.NaN as default — GetDoubleValue returns 0.0 for missing keys
        // if no default is specified, which would silently produce coordinates at (0, 0).
        var raDeg = header.GetDoubleValue("CRVAL1", double.NaN);
        var dec = header.GetDoubleValue("CRVAL2", double.NaN);

        if (double.IsNaN(raDeg) || double.IsNaN(dec))
        {
            raDeg = header.GetDoubleValue("RA", double.NaN);
            dec = header.GetDoubleValue("DEC", double.NaN);
        }

        if (double.IsNaN(raDeg) || double.IsNaN(dec))
        {
            // OBJCTRA is "HH MM SS.sss", OBJCTDEC is "+DD MM SS.sss" (space-separated)
            var objctRa = header.GetStringValue("OBJCTRA");
            var objctDec = header.GetStringValue("OBJCTDEC");
            if (objctRa is not null && objctDec is not null)
            {
                raDeg = CoordinateUtils.HMSToDegree(objctRa.Replace(' ', ':'));
                dec = CoordinateUtils.DMSToDegree(objctDec.Replace(' ', ':'));
            }
        }

        if (double.IsNaN(raDeg) || double.IsNaN(dec))
        {
            return default;
        }

        var wcs = new WCS(raDeg / 15.0, dec)
        {
            CRPix1 = header.GetDoubleValue("CRPIX1", double.NaN),
            CRPix2 = header.GetDoubleValue("CRPIX2", double.NaN),
        };

        // Try CD matrix first (preferred modern convention)
        var cd1_1 = header.GetDoubleValue("CD1_1", double.NaN);
        var cd1_2 = header.GetDoubleValue("CD1_2", double.NaN);
        var cd2_1 = header.GetDoubleValue("CD2_1", double.NaN);
        var cd2_2 = header.GetDoubleValue("CD2_2", double.NaN);

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
            var cdelt1 = header.GetDoubleValue("CDELT1", double.NaN);
            var cdelt2 = header.GetDoubleValue("CDELT2", double.NaN);
            var crota2 = header.GetDoubleValue("CROTA2", double.NaN);

            if (!double.IsNaN(cdelt1) && !double.IsNaN(cdelt2))
            {
                if (double.IsNaN(crota2))
                {
                    crota2 = 0.0;
                }
                var (sinRot, cosRot) = Math.SinCos(double.DegreesToRadians(crota2));

                wcs = wcs with
                {
                    CD1_1 = cdelt1 * cosRot,
                    CD1_2 = -cdelt2 * sinRot,
                    CD2_1 = cdelt1 * sinRot,
                    CD2_2 = cdelt2 * cosRot,
                };
            }
            else
            {
                // Fall back to PIXSCALE/SCALE + ANGLE/POSANGLE (approximate WCS from mount + camera).
                // SIP is layered on top of a CD matrix, so we only reach this branch when we
                // already know there is no SIP either.
                var pixscale = header.GetDoubleValue("PIXSCALE", double.NaN);
                if (double.IsNaN(pixscale))
                {
                    pixscale = header.GetDoubleValue("SCALE", double.NaN);
                }
                // ANGLE is the image angle; POSANGLE is the camera rotator angle
                var posAngle = header.GetDoubleValue("ANGLE", double.NaN);
                if (double.IsNaN(posAngle))
                {
                    posAngle = header.GetDoubleValue("POSANGLE", double.NaN);
                }

                if (!double.IsNaN(pixscale) && pixscale > 0)
                {
                    if (double.IsNaN(posAngle))
                    {
                        posAngle = 0.0;
                    }

                    var pixscaleDeg = pixscale / 3600.0;

                    // ANGLE is the position angle in screen coordinates.
                    // For TOP-DOWN images (most hobby astro): Y is flipped vs FITS convention,
                    // so CROTA2 = 180 - ANGLE. For standard BOTTOM-UP: CROTA2 = ANGLE.
                    // FLIPPED mirrors the X axis, which also adds 180° offset.
                    var rowOrder = header.GetStringValue("ROWORDER");
                    var isTopDown = rowOrder is null or "TOP-DOWN";
                    var flippedStr = header.GetStringValue("FLIPPED");
                    var isFlipped = flippedStr is "T" or "True" or "true";
                    var effectiveCrota2 = (isTopDown != isFlipped) ? 180.0 - posAngle : posAngle;

                    var (sinRot, cosRot) = Math.SinCos(double.DegreesToRadians(effectiveCrota2));
                    wcs = wcs with
                    {
                        CD1_1 = pixscaleDeg * cosRot,
                        CD1_2 = -pixscaleDeg * sinRot,
                        CD2_1 = pixscaleDeg * sinRot,
                        CD2_2 = pixscaleDeg * cosRot,
                        IsApproximate = true,
                    };
                }
            }
        }

        // SIP polynomial distortion, layered on top of CD. Only valid when CTYPE1
        // explicitly carries the `-SIP` suffix; otherwise the A_*/B_* cards are
        // either absent or apply to a different convention we don't read.
        var ctype1 = header.GetStringValue("CTYPE1");
        if (wcs.HasCDMatrix && ctype1 is not null && ctype1.Contains("-SIP", StringComparison.OrdinalIgnoreCase))
        {
            wcs = ReadSipFromHeader(header, wcs);
        }

        return wcs;
    }

    /// <summary>
    /// Extracts SIP A/B/AP/BP coefficient arrays from a FITS header that
    /// has already been confirmed to carry a SIP CTYPE. Missing arrays
    /// (e.g. a header that only emits forward terms) leave the
    /// corresponding fields null; <see cref="SkyToPixel"/> falls back to
    /// one Newton iteration of the forward polynomial in that case.
    /// </summary>
    private static WCS ReadSipFromHeader(Header header, WCS wcs)
    {
        var aOrder = header.GetIntValue("A_ORDER", -1);
        var bOrder = header.GetIntValue("B_ORDER", -1);
        if (aOrder < 1 || bOrder < 1)
        {
            // Malformed SIP header — CTYPE claims SIP but the order cards
            // are missing. Skip the polynomial pickup; caller still gets a
            // valid linear WCS and downstream code keeps working.
            return wcs;
        }

        // The shared SipOrder is the max across A/B/AP/BP so the per-array
        // shape covers every emitted card; arrays are square at that order
        // even if a specific axis has a lower nominal order.
        var apOrder = header.GetIntValue("AP_ORDER", -1);
        var bpOrder = header.GetIntValue("BP_ORDER", -1);
        var maxOrder = Math.Max(Math.Max(aOrder, bOrder), Math.Max(apOrder, bpOrder));
        if (maxOrder < 1 || maxOrder > SipPolynomial.MaxOrder)
        {
            return wcs;
        }

        var a = ReadSipCoefficientMatrix(header, "A_", aOrder, maxOrder);
        var b = ReadSipCoefficientMatrix(header, "B_", bOrder, maxOrder);
        var ap = apOrder >= 1 ? ReadSipCoefficientMatrix(header, "AP_", apOrder, maxOrder) : null;
        var bp = bpOrder >= 1 ? ReadSipCoefficientMatrix(header, "BP_", bpOrder, maxOrder) : null;

        return wcs with
        {
            SipOrder = maxOrder,
            SipA = a,
            SipB = b,
            SipAP = ap,
            SipBP = bp,
        };
    }

    /// <summary>
    /// Reads <c>{prefix}i_j</c> coefficient cards (e.g. <c>A_2_1</c>) into a
    /// <c>[maxOrder + 1, maxOrder + 1]</c> matrix; missing cards stay zero.
    /// Both the <c>(0, 0)</c> term and any term where <c>i + j &gt; ownOrder</c>
    /// are skipped per SIP convention.
    /// </summary>
    private static double[,] ReadSipCoefficientMatrix(Header header, string prefix, int ownOrder, int maxOrder)
    {
        var coeffs = new double[maxOrder + 1, maxOrder + 1];
        for (var i = 0; i <= ownOrder; i++)
        {
            for (var j = 0; j <= ownOrder - i; j++)
            {
                if ((i | j) == 0) continue;
                var key = string.Concat(prefix, i.ToString(CultureInfo.InvariantCulture), "_", j.ToString(CultureInfo.InvariantCulture));
                coeffs[i, j] = header.GetDoubleValue(key, 0.0);
            }
        }
        return coeffs;
    }

    /// <summary>
    /// Reads WCS from an ASTAP plate solution .ini file (key=value pairs with FITS-like keywords).
    /// Returns a non-approximate WCS with CD matrix if the file contains a valid solution.
    /// </summary>
    public static WCS? FromAstapIniFile(string iniPath)
    {
        if (!System.IO.File.Exists(iniPath))
        {
            return null;
        }

        var values = new System.Collections.Generic.Dictionary<string, double>();
        foreach (var rawLine in System.IO.File.ReadLines(iniPath))
        {
            var line = rawLine.Trim();
            var eqIdx = line.IndexOf('=');
            if (eqIdx <= 0)
            {
                continue;
            }

            var key = line[..eqIdx].Trim();
            var valStr = line[(eqIdx + 1)..].Trim();

            if (double.TryParse(valStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val))
            {
                values[key] = val;
            }
        }

        if (!values.TryGetValue("CRVAL1", out var ra) || !values.TryGetValue("CRVAL2", out var dec))
        {
            return null;
        }

        var wcs = new WCS(ra / 15.0, dec)
        {
            CRPix1 = values.TryGetValue("CRPIX1", out var crpix1) ? crpix1 : double.NaN,
            CRPix2 = values.TryGetValue("CRPIX2", out var crpix2) ? crpix2 : double.NaN,
        };

        if (values.TryGetValue("CD1_1", out var cd11) && values.TryGetValue("CD1_2", out var cd12)
            && values.TryGetValue("CD2_1", out var cd21) && values.TryGetValue("CD2_2", out var cd22))
        {
            wcs = wcs with { CD1_1 = cd11, CD1_2 = cd12, CD2_1 = cd21, CD2_2 = cd22 };
        }
        else if (values.TryGetValue("CDELT1", out var cdelt1) && values.TryGetValue("CDELT2", out var cdelt2))
        {
            var crota2 = values.TryGetValue("CROTA2", out var cr2) ? cr2 : 0.0;
            var (sinRot, cosRot) = Math.SinCos(double.DegreesToRadians(crota2));
            wcs = wcs with
            {
                CD1_1 = cdelt1 * cosRot,
                CD1_2 = -cdelt2 * sinRot,
                CD2_1 = cdelt1 * sinRot,
                CD2_2 = cdelt2 * cosRot,
            };
        }

        return wcs;
    }

    /// <summary>
    /// Writes WCS keywords to a FITS header using the CD matrix convention.
    /// </summary>
    public readonly void WriteToHeader(Header header)
    {
        // CTYPE gets the `-SIP` suffix iff we are actually emitting a SIP
        // polynomial; readers must use that suffix to know to look for the
        // A_*/B_* cards.
        var hasSip = HasSip;
        header.AddCard(new HeaderCard("CTYPE1", hasSip ? "RA---TAN-SIP" : "RA---TAN", "TAN (gnomonic) projection"));
        header.AddCard(new HeaderCard("CTYPE2", hasSip ? "DEC--TAN-SIP" : "DEC--TAN", "TAN (gnomonic) projection"));
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

        if (hasSip)
        {
            WriteSipCoefficientMatrix(header, "A", SipA!, SipOrder);
            WriteSipCoefficientMatrix(header, "B", SipB!, SipOrder);
            if (HasInverseSip)
            {
                WriteSipCoefficientMatrix(header, "AP", SipAP!, SipOrder);
                WriteSipCoefficientMatrix(header, "BP", SipBP!, SipOrder);
            }
        }
    }

    /// <summary>
    /// Emits a SIP coefficient block as a series of <c>{name}_ORDER</c>
    /// + <c>{name}_i_j</c> cards. Both the constant term and entries where
    /// <c>i + j &gt; order</c> are skipped per the SIP convention.
    /// </summary>
    private static void WriteSipCoefficientMatrix(Header header, string name, double[,] coeffs, int order)
    {
        header.AddCard(new HeaderCard($"{name}_ORDER", order, "SIP polynomial order"));
        for (var i = 0; i <= order; i++)
        {
            for (var j = 0; j <= order - i; j++)
            {
                if ((i | j) == 0) continue;
                header.AddCard(new HeaderCard($"{name}_{i}_{j}", coeffs[i, j], null));
            }
        }
    }
}
