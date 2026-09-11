using nom.tam.fits;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;

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
///   <item><term>CRPIX1/2</term><description>Reference pixel, written 1-based as the FITS standard requires; 0-based in memory (see below)</description></item>
///   <item><term>CD1_1..CD2_2</term><description>Linear transformation matrix (degrees/pixel), encoding scale, rotation, and any skew/flip</description></item>
///   <item><term>EQUINOX</term><description>Equinox of coordinates (always 2000.0)</description></item>
///   <item><term>PIXORIG</term><description>Marker that CRPIX follows the standard (see <see cref="PixelOriginCard"/>)</description></item>
/// </list>
/// <para><b>Pixel convention.</b> In memory every pixel coordinate on this type, <see cref="CRPix1"/> and
/// <see cref="CRPix2"/> included, is in the same 0-based frame as a detected centroid: the centre of pixel
/// <c>[y, x]</c> is <c>(x, y)</c>. That is the frame <c>CatalogPlateSolver</c> fits in and the one every
/// consumer compares against, so <see cref="SkyToPixel"/> answers in it with no origin shift. The FITS
/// standard puts the centre of the first pixel at <c>(1, 1)</c>, so the header is the ONLY place the two
/// differ: <see cref="WriteToHeader"/> adds one and stamps <see cref="PixelOriginCard"/>;
/// <see cref="FromHeader"/> and <see cref="FromAstapIniFile"/> subtract one. Until 2026-09-05 the numbers
/// crossed the header unchanged under a "1-based" comment, so every file TianWen solved was one pixel
/// off in astropy, PixInsight, Siril and ASTAP, and every third-party WCS was one pixel off in TianWen;
/// measured against Gaia DR3 on four fields as a constant (+0.95, +0.91) px offset in (x, y), the same on
/// bright stars and in every quarter of the frame. A file written before the marker existed is still
/// read correctly where its authorship can be seen (<see cref="IsLegacyZeroBasedHeader"/>).</para>
/// </summary>
/// <param name="CenterRA">J2000.0 RA of the reference pixel in 0..24h (hours)</param>
/// <param name="CenterDec">J2000.0 Dec of the reference pixel in -90..+90 degrees</param>
public record struct WCS(double CenterRA, double CenterDec)
{
    /// <summary>Reference pixel X in the 0-based centroid frame (see the pixel-convention remarks on the type);
    /// the header carries it plus one. The frame centre in this frame is <c>(Width - 1) / 2.0</c>; the solver's
    /// nominal <c>(Width + 1) / 2.0</c> sits one pixel from it, which is harmless because CRVAL is re-derived at
    /// whatever pixel CRPIX names.</summary>
    public double CRPix1 { get; init; } = double.NaN;

    /// <summary>Reference pixel Y in the 0-based centroid frame; the header carries it plus one. See <see cref="CRPix1"/>.</summary>
    public double CRPix2 { get; init; } = double.NaN;

    /// <summary>
    /// Card stamped beside CRPIX by <see cref="WriteToHeader"/>: <c>1</c> says the CRPIX values follow the FITS
    /// standard. Its absence on a TianWen-authored file marks a header written before 2026-09-05, whose CRPIX is
    /// the 0-based in-memory value verbatim. Not spelled with a <c>CRPIX</c> or <c>WCS</c> prefix on purpose:
    /// wcslib (and so astropy) warns on every keyword that looks like a near-miss of a standard one, and
    /// <c>CRPIXORG</c> drew "looks very much like CRPIXja but isn't" on every file.
    /// </summary>
    public const string PixelOriginCard = "PIXORIG";

    /// <summary>The FITS standard's coordinate of the first pixel's centre: the offset between header and memory.</summary>
    private const double FitsPixelOrigin = 1.0;

    /// <summary>
    /// The same solution read against a CROPPED frame: the sky is untouched, the pixel grid's origin
    /// moves to <paramref name="cropX"/>, <paramref name="cropY"/>.
    /// </summary>
    /// <remarks>
    /// A crop is a pure TRANSLATION of the pixel grid, so only the reference pixel moves -- the CD
    /// matrix is a derivative and the SIP coefficients are relative to CRPIX, both unchanged. Exists so
    /// no caller hand-edits CRPIX: these are the 0-BASED in-memory values (see the type remarks), the
    /// header's plus-one is applied on write, and a caller subtracting one here would inject the very
    /// off-by-one <c>WcsPixelOriginTests</c> exists to prevent. NaN in means NaN out, which is what an
    /// unsolved frame carries.
    /// </remarks>
    public readonly WCS CroppedTo(int cropX, int cropY)
        => this with { CRPix1 = CRPix1 - cropX, CRPix2 = CRPix2 - cropY };

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
    /// <remarks>
    /// The <see cref="MemberNotNullWhenAttribute"/> is what lets a guarded branch read
    /// <see cref="SipA"/> / <see cref="SipB"/> without a null-forgiving <c>!</c>: the invariant is
    /// STATED here once, where it is decided, instead of being re-asserted at each of the six call
    /// sites that rely on it.
    /// </remarks>
    [MemberNotNullWhen(true, nameof(SipA), nameof(SipB))]
    public readonly bool HasSip => SipOrder > 0 && SipA is not null && SipB is not null;

    /// <summary>
    /// True when inverse SIP terms (<see cref="SipAP"/> + <see cref="SipBP"/>)
    /// are present. Independent of <see cref="HasSip"/>: a header can carry
    /// only forward terms, in which case <see cref="SkyToPixel"/> falls back
    /// to one Newton iteration with the forward polynomial.
    /// </summary>
    [MemberNotNullWhen(true, nameof(SipAP), nameof(SipBP))]
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
    /// Position angle of the sensor's +Y axis on the sky, degrees in [0, 360), measured from north
    /// through east -- the frame's ORIENTATION, and 0 for a conventional north-up image. NaN with no
    /// CD matrix.
    /// <para>
    /// The +Y pixel direction maps through the CD matrix onto the intermediate world coordinates
    /// (<c>CD1_2</c>, <c>CD2_2</c>), which are east and north respectively (see the <c>u</c>/<c>v</c>
    /// pair in <see cref="PixelToSky"/>), so the angle is <c>atan2(east, north)</c>.
    /// </para>
    /// <para>
    /// This is PARITY-INDEPENDENT by construction: a mirror in the optical train reverses the sense
    /// in which the angle grows but not the direction the +Y axis points, so two frames from the same
    /// train are always comparable. That is what makes it a witness to a German meridian flip, which
    /// is a pure 180 degree rotation of the field and changes nothing else -- see
    /// <c>docs/plans/meridian-flip-verification.md</c>. Do not fold a handedness term into it.
    /// </para>
    /// </summary>
    public readonly double RotationDeg => HasCDMatrix
        ? CoordinateUtils.ConditionDegrees(double.RadiansToDegrees(Math.Atan2(CD1_2, CD2_2)))
        : double.NaN;

    /// <summary>
    /// How far this frame's field is rotated from <paramref name="other"/>'s, degrees in (-180, 180].
    /// NaN when either lacks a CD matrix. A magnitude near 180 across a commanded meridian flip is
    /// the flip having physically happened; near 0 is it having been declined.
    /// </summary>
    public readonly double RotationDeltaDeg(in WCS other)
        => CoordinateUtils.ConditionDegreesSigned(RotationDeg - other.RotationDeg);

    /// <summary>
    /// The direction in PIXEL space that a sky position angle points in on this frame: degrees in
    /// [0, 360) with 0 along +X (columns) and 90 along +Y (rows, which is DOWN a top-down frame).
    /// NaN without a CD matrix.
    /// <para>
    /// A unit step on the sky at position angle <paramref name="positionAngleDeg"/> (from north
    /// through east) is <c>(sin PA, cos PA)</c> in the intermediate world coordinates (east, north)
    /// the CD matrix produces, so the inverse matrix carries it to pixels. Handedness comes along for
    /// free: a mirrored frame answers with the mirrored pixel direction, which is the point of asking
    /// the matrix rather than adding the angle to <see cref="RotationDeg"/>.
    /// </para>
    /// </summary>
    public readonly double SkyPositionAngleToPixelAngleDeg(double positionAngleDeg)
    {
        if (!HasCDMatrix)
        {
            return double.NaN;
        }
        var det = CD1_1 * CD2_2 - CD1_2 * CD2_1;
        if (Math.Abs(det) < 1e-20)
        {
            return double.NaN;
        }
        var (east, north) = Math.SinCos(double.DegreesToRadians(positionAngleDeg));
        var dx = (CD2_2 * east - CD1_2 * north) / det;
        var dy = (-CD2_1 * east + CD1_1 * north) / det;
        return CoordinateUtils.ConditionDegrees(double.RadiansToDegrees(Math.Atan2(dy, dx)));
    }

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
    /// Converts a pixel position (0-based centroid frame, see the type remarks) to sky coordinates
    /// using the CD matrix and inverse gnomonic (TAN) deprojection.
    /// Returns <c>null</c> if no CD matrix is available.
    /// </summary>
    /// <param name="x">Pixel X, 0-based.</param>
    /// <param name="y">Pixel Y, 0-based.</param>
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
            var dxC = SipPolynomial.Apply(dx, dy, SipA);
            var dyC = SipPolynomial.Apply(dx, dy, SipB);
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
    /// Converts sky coordinates to pixel position (0-based centroid frame, see the type remarks)
    /// using the CD matrix inverse and gnomonic (TAN) projection.
    /// Returns <c>null</c> if no CD matrix is available or the CD matrix is singular.
    /// </summary>
    /// <param name="ra">RA in hours (0..24).</param>
    /// <param name="dec">Dec in degrees (-90..+90).</param>
    /// <returns>Pixel position, 0-based, directly comparable to a detected centroid; or <c>null</c> if no CD matrix or behind tangent plane.</returns>
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
            var dxC = SipPolynomial.Apply(dx, dy, SipAP);
            var dyC = SipPolynomial.Apply(dx, dy, SipBP);
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
            var dxC = SipPolynomial.Apply(dx, dy, SipA);
            var dyC = SipPolynomial.Apply(dx, dy, SipB);
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
    /// True when the header's CRPIX must be read WITHOUT the origin shift: a file TianWen wrote before
    /// <see cref="PixelOriginCard"/> existed, whose CRPIX numbers are the 0-based in-memory values verbatim.
    /// Shifting them would move every such master one pixel in TianWen's own viewer, which is the one
    /// place they were right. Authorship is <c>STACK_N</c> (a TianWen integration) or a <c>TianWen.</c>
    /// SWCREATE; a file carrying the marker is never legacy whatever its author, and a foreign file never
    /// is. Two cases this cannot see, both reading one pixel off here exactly as they did before the shift
    /// existed, and both ended by re-solving, which writes the marker: a legacy master whose WCS came from
    /// the ASTAP or astrometry.net fallback (compliant numbers all along), and a foreign frame TianWen
    /// solved in place with <c>solve --update-fits</c> (0-based numbers under its author's SWCREATE).
    /// </summary>
    private static bool IsLegacyZeroBasedHeader(Header header)
    {
        var origin = header.GetIntValue(PixelOriginCard, -1);
        if (origin == 0)
        {
            // Nothing writes 0 today; it is what a header declaring 0-based numbers would say.
            return true;
        }
        else if (origin > 0)
        {
            return false;
        }
        else
        {
            // No marker: legacy only if TianWen wrote the file.
            return header.GetIntValue("STACK_N", 0) > 0
                || IntegrationFitsWriter.IsTianWenProduct(header.GetStringValue("SWCREATE"));
        }
    }

    /// <summary>
    /// Read WCS parameters from a FITS header.
    /// </summary>
    public static WCS? FromHeader(Header header)
    {
        // Try CRVAL1/2 first (degrees), then OBJCTRA/OBJCTDEC (HMS/DMS strings), then RA/DEC (degrees).
        // IMPORTANT: Always pass double.NaN as default; GetDoubleValue returns 0.0 for missing keys
        // if no default is specified, which would silently produce coordinates at (0, 0).
        //
        // OBJCTRA/OBJCTDEC beats RA/DEC because the two mean different things and only one of them
        // describes THIS frame. OBJCTRA is the target the framing put on the sensor -- intent, and
        // wrong only if the slew failed. RA/DEC is the position the MOUNT REPORTED, which is intent
        // plus whatever the pointing model got wrong, and nothing in the header says whether the
        // mount was synced. On an unsynced rig the gap is not small: an SMC integration whose
        // reference sub carries RA/DEC = (0.4621h, -71.164) and OBJCTRA/OBJCTDEC = (0.8778h,
        // -72.795) plate-solves to (0.9016h, -72.386) -- the mount readout is 2.39 deg out and the
        // target 0.42 deg. That matters because CatalogPlateSolver's pair-lock anchor pool is the
        // brightest catalog stars that PROJECT INSIDE the frame from the hint, so a hint off by
        // most of a field fills the pool with stars the image does not contain and the seed never
        // reaches consensus (measured: 11-13 hits against a threshold of 24, and widening the
        // search radius to 8 deg does not help, because coverage was never the problem). With the
        // target as the hint the same file locks at 104/160 consensus and passes the acceptance
        // gate 116/120.
        //
        // TianWen writes both keywords from the same ImageMeta.TargetRA/TargetDec (Image.Fits.cs),
        // so this reorder is a no-op on our own files; it only changes third-party files that
        // distinguish the two. Image.Fits.ParseTargetCoords deliberately mirrors this order.
        var raDeg = header.GetDoubleValue("CRVAL1", double.NaN);
        var dec = header.GetDoubleValue("CRVAL2", double.NaN);

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
            raDeg = header.GetDoubleValue("RA", double.NaN);
            dec = header.GetDoubleValue("DEC", double.NaN);
        }

        if (double.IsNaN(raDeg) || double.IsNaN(dec))
        {
            return default;
        }

        // The header's CRPIX is 1-based and memory is 0-based (type remarks). A TianWen file from before
        // the marker card wrote the 0-based numbers verbatim, so it is read verbatim.
        var originShift = IsLegacyZeroBasedHeader(header) ? 0.0 : FitsPixelOrigin;
        var wcs = new WCS(raDeg / 15.0, dec)
        {
            CRPix1 = header.GetDoubleValue("CRPIX1", double.NaN) - originShift,
            CRPix2 = header.GetDoubleValue("CRPIX2", double.NaN) - originShift,
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
                    //
                    // Parsed through RowOrder.FromFITSValue, the SAME parser Image.Fits.cs uses on
                    // load, rather than the exact ordinal match this used to do
                    // (`rowOrder is null or "TOP-DOWN"`). One header deserves one parser: the
                    // ordinal form was case-sensitive and did not trim, so a file writing
                    // 'Top-Down' read correctly as an IMAGE while falling to the bottom-up branch
                    // here, and this branch is where that costs a full 180 degrees.
                    //
                    // An ABSENT card means TOP-DOWN, deliberately and not as a shrug. The FITS
                    // standard is bottom-up, but essentially every amateur capture and processing
                    // tool that writes this card writes TOP-DOWN (TianWen, N.I.N.A., SharpCap, APP,
                    // ASTAP), so a file that omits it is far likelier to be one of theirs with the
                    // card stripped than a genuinely bottom-up frame. The same default is chosen
                    // independently by both readers in Image.Fits.cs; if it is ever revisited, it
                    // must be revisited in all three, or an image and its own WCS hint disagree.
                    var isTopDown =
                        (RowOrder.FromFITSValue(header.GetStringValue("ROWORDER")) ?? Imaging.RowOrder.TopDown)
                        == Imaging.RowOrder.TopDown;
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
            // Malformed SIP header: CTYPE claims SIP but the order cards
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

        // ASTAP writes the standard's 1-based CRPIX; memory is 0-based (type remarks).
        var wcs = new WCS(ra / 15.0, dec)
        {
            CRPix1 = values.TryGetValue("CRPIX1", out var crpix1) ? crpix1 - FitsPixelOrigin : double.NaN,
            CRPix2 = values.TryGetValue("CRPIX2", out var crpix2) ? crpix2 - FitsPixelOrigin : double.NaN,
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

        // Memory is 0-based, the standard's first pixel centre is (1, 1) (type remarks). The marker is
        // what lets FromHeader tell this file from one written before the shift existed.
        if (!double.IsNaN(CRPix1))
        {
            header.AddCard(new HeaderCard("CRPIX1", CRPix1 + FitsPixelOrigin, "Reference pixel X (FITS 1-based)"));
        }
        if (!double.IsNaN(CRPix2))
        {
            header.AddCard(new HeaderCard("CRPIX2", CRPix2 + FitsPixelOrigin, "Reference pixel Y (FITS 1-based)"));
        }
        header.AddCard(new HeaderCard(PixelOriginCard, 1, "CRPIX origin (1 = FITS standard)"));

        if (HasCDMatrix)
        {
            header.AddCard(new HeaderCard("CD1_1", CD1_1, "dRA/dx [deg/pix]"));
            header.AddCard(new HeaderCard("CD1_2", CD1_2, "dRA/dy [deg/pix]"));
            header.AddCard(new HeaderCard("CD2_1", CD2_1, "dDec/dx [deg/pix]"));
            header.AddCard(new HeaderCard("CD2_2", CD2_2, "dDec/dy [deg/pix]"));
        }

        // Bound by pattern rather than read through HasSip / HasInverseSip. Those carry
        // MemberNotNullWhen and it does hold at the projection call sites, but here a SECOND property
        // read on this struct receiver discards the state the first one established -- so the arrays
        // say plainly that they are present instead of the call sites asserting it with a `!`.
        // SipOrder > 0 is already implied by hasSip, which is the only part of HasInverseSip these
        // two binds do not restate.
        if (hasSip && SipA is { } sipA && SipB is { } sipB)
        {
            WriteSipCoefficientMatrix(header, "A", sipA, SipOrder);
            WriteSipCoefficientMatrix(header, "B", sipB, SipOrder);
            if (SipAP is { } sipAP && SipBP is { } sipBP)
            {
                WriteSipCoefficientMatrix(header, "AP", sipAP, SipOrder);
                WriteSipCoefficientMatrix(header, "BP", sipBP, SipOrder);
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
