using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Astrometry.PlateSolve;

/// <summary>
/// A plate solver that matches detected image stars against a local Tycho-2 star catalog
/// to determine the World Coordinate System (WCS) centre of an image.
///
/// <para><b>Algorithm overview</b></para>
/// <list type="number">
///   <item>Query <see cref="ICelestialObjectDB"/> for catalog stars within the search radius
///         around the supplied <c>searchOrigin</c>. Stars are sorted by Johnson V magnitude
///         (brightest first) to enable brightness-aware matching.</item>
///   <item>Detect stars in the image via <see cref="Image.FindStarsAsync"/> (SNR ≥ 5, up to 500 stars).</item>
///   <item>Project the catalog stars onto the image plane using a gnomonic (tangent-plane) projection
///         centred on the current WCS estimate, with pixel scale derived from <see cref="ImageDim"/>.</item>
///   <item>Match projected catalog stars to detected image stars using a proximity search with a
///         <b>soft brightness-rank penalty</b>: detected stars are ranked by flux (brightest first),
///         and projected catalog stars inherit rank order from the brightness-sorted catalog.
///         The matching score is <c>spatialDistance + |detRank − catRank| × scale</c>, which
///         prefers both spatially close and brightness-similar pairs without hard cutoffs.</item>
///   <item>Fit a least-squares affine transform (<see cref="System.Numerics.Matrix3x2"/>) from
///         matched projected positions to detected positions, invert it to find the image centre
///         in catalog coordinates, then update the WCS estimate via inverse tangent projection.</item>
///   <item>Repeat steps 3–5 for up to 5 iterations (with shrinking match tolerance) until
///         convergence (ΔRA &lt; 10⁻⁶° and ΔDec &lt; 10⁻⁶°). Both standard and mirror-flipped
///         orientations are attempted.</item>
/// </list>
///
/// <para>Requires a pre-initialised <see cref="ICelestialObjectDB"/> with Tycho-2 data and a
/// valid <c>searchOrigin</c>; blind solving (no search hint) is not supported and returns
/// <c>null</c>.</para>
/// </summary>
internal sealed class CatalogPlateSolver(ICelestialObjectDB db) : IPlateSolver
{
    const int MinStarsForMatch = 6;

    public string Name => "Catalog plate solver";

    public float Priority => 0.99f;

    private int _catalogStars, _detectedStars, _projectedStars, _matchedStars, _iterations;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);

    public Task<PlateSolveResult> SolveFileAsync(
        string fitsFile,
        ImageDim? imageDim = default,
        float range = IPlateSolver.DefaultRange,
        WCS? searchOrigin = default,
        double? searchRadius = default,
        CancellationToken cancellationToken = default
    )
    {
        var sw = Stopwatch.StartNew();

        if (searchOrigin is null)
        {
            return Task.FromResult(new PlateSolveResult(null, sw.Elapsed));
        }

        if (!Image.TryReadFitsFile(fitsFile, out var image))
        {
            return Task.FromResult(new PlateSolveResult(null, sw.Elapsed));
        }

        return SolveImageAsync(image, imageDim, range, searchOrigin, searchRadius, cancellationToken);
    }

    public async Task<PlateSolveResult> SolveImageAsync(
        Image image,
        ImageDim? imageDim = default,
        float range = IPlateSolver.DefaultRange,
        WCS? searchOrigin = default,
        double? searchRadius = default,
        CancellationToken cancellationToken = default
    )
    {
        var sw = Stopwatch.StartNew();

        PlateSolveResult Result(WCS? wcs) => new PlateSolveResult(wcs, sw.Elapsed)
        {
            CatalogStars = _catalogStars,
            DetectedStars = _detectedStars,
            ProjectedStars = _projectedStars,
            MatchedStars = _matchedStars,
            Iterations = _iterations
        };

        if (searchOrigin is not { } origin)
        {
            return Result(null);
        }

        if ((imageDim ?? image.GetImageDim()) is not { } dim)
        {
            return Result(null);
        }

        var fov = dim.FieldOfView;
        var searchRadiusDeg = searchRadius ?? Math.Max(fov.width, fov.height) * 0.75;

        // Query catalog stars within search radius
        var catalogCoords = QueryCatalogStarsInRegion(origin, searchRadiusDeg);
        _catalogStars = catalogCoords.Count;
        if (catalogCoords.Count < MinStarsForMatch)
        {
            return Result(null);
        }

        // Detect stars in image
        var detectedStars = await image.FindStarsAsync(0, snrMin: 5f, maxStars: 500, cancellationToken: cancellationToken);
        _detectedStars = detectedStars.Count;
        if (detectedStars.Count < MinStarsForMatch)
        {
            return Result(null);
        }

        // Sort catalog stars by brightness (lowest mag = brightest first).
        // This improves proximity matching since the brightest projected catalog stars
        // are most likely to correspond to detected stars.
        catalogCoords.Sort((a, b) => a.VMag.CompareTo(b.VMag));

        var pixelScaleRad = dim.PixelScale * Math.PI / (3600.0 * 180.0);
        var cx = image.Width / 2.0;
        var cy = image.Height / 2.0;

        // Try standard orientation, then mirror-flipped
        var wcs = TrySolveWithProximityMatching(detectedStars, catalogCoords, origin, pixelScaleRad, cx, cy, dim, xSign: 1.0);

        return Result(wcs ?? TrySolveWithProximityMatching(detectedStars, catalogCoords, origin, pixelScaleRad, cx, cy, dim, xSign: -1.0));
    }

    private WCS? TrySolveWithProximityMatching(
        StarList detectedStars,
        List<(double RA, double Dec, double VMag)> catalogCoords,
        WCS origin,
        double pixelScaleRad,
        double cx,
        double cy,
        ImageDim dim,
        double xSign
    )
    {
        var currentOrigin = origin;

        // Iteratively refine: project → match → fit affine → update WCS → repeat
        for (int iteration = 0; iteration < 5; iteration++)
        {
            var projected = ProjectCatalogStars(catalogCoords, currentOrigin, pixelScaleRad, cx, cy, dim, xSign);
            if (projected.Count < MinStarsForMatch)
            {
                return null;
            }

            // Match tolerance shrinks with each iteration
            var diagonal = Math.Sqrt(dim.Width * dim.Width + dim.Height * dim.Height);
            var matchTolerance = (float)(diagonal * (iteration == 0 ? 0.1 : 0.03));

            // Rank detected stars by flux (brightest first) for brightness-aware matching.
            // Projected catalog stars preserve brightness order from the pre-sorted catalogCoords
            // (brightest VMag first), so their list index is their brightness rank.
            var rankedDetected = new List<ImagedStar>(detectedStars);
            rankedDetected.Sort((a, b) => b.Flux.CompareTo(a.Flux));

            // Use rank difference as a soft penalty added to spatial distance.
            // This prefers matches that are both spatially close and brightness-similar,
            // without excluding good spatial matches at different brightness ranks.
            // The penalty is scaled so a rank mismatch of N stars adds ~(N/total * tolerance * 0.5) pixels.
            var rankPenaltyScale = projected.Count > 0 ? matchTolerance * 0.25f / projected.Count : 0f;

            var matchedDetected = new List<Vector2>();
            var matchedProjected = new List<Vector2>();

            for (int detRank = 0; detRank < rankedDetected.Count; detRank++)
            {
                var det = rankedDetected[detRank];
                var bestScore = matchTolerance;
                ImagedStar? bestMatch = null;

                for (int catRank = 0; catRank < projected.Count; catRank++)
                {
                    var cat = projected[catRank];
                    var dx = det.XCentroid - cat.XCentroid;
                    var dy = det.YCentroid - cat.YCentroid;
                    var dist = MathF.Sqrt(dx * dx + dy * dy);

                    if (dist < matchTolerance)
                    {
                        var rankPenalty = Math.Abs(detRank - catRank) * rankPenaltyScale;
                        var score = dist + rankPenalty;

                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestMatch = cat;
                        }
                    }
                }

                if (bestMatch is { } bm)
                {
                    matchedDetected.Add(new Vector2(det.XCentroid, det.YCentroid));
                    matchedProjected.Add(new Vector2(bm.XCentroid, bm.YCentroid));
                }
            }

            if (matchedDetected.Count < MinStarsForMatch)
            {
                _projectedStars = projected.Count;
                _matchedStars = matchedDetected.Count;
                _iterations = iteration + 1;
                return iteration > 0 ? currentOrigin : null;
            }

            // Compute offset using Matrix3x2 affine fit (handles translation + rotation)
            var M = FitAffineTransform(matchedProjected, matchedDetected);
            if (M is null || !Matrix3x2.Invert(M.Value, out var Minv))
            {
                return iteration > 0 ? currentOrigin : null;
            }

            var centerInProjected = Vector2.Transform(new Vector2((float)cx, (float)cy), Minv);
            var refined = InverseTanProject(centerInProjected, currentOrigin, pixelScaleRad, cx, cy, xSign);

            if (refined is not { } refinedWcs)
            {
                return iteration > 0 ? currentOrigin : null;
            }

            // Check convergence
            var dRA = Math.Abs(refinedWcs.CenterRA - currentOrigin.CenterRA) * 15.0;
            var dDec = Math.Abs(refinedWcs.CenterDec - currentOrigin.CenterDec);

            currentOrigin = refinedWcs;

            _projectedStars = projected.Count;
            _matchedStars = matchedDetected.Count;
            _iterations = iteration + 1;

            if (dRA < 1e-6 && dDec < 1e-6)
            {
                break;
            }
        }

        return currentOrigin;
    }

    /// <summary>
    /// Fits a Matrix3x2 affine transform that maps source points to destination points
    /// using least squares (normal equations).
    /// </summary>
    private static Matrix3x2? FitAffineTransform(List<Vector2> source, List<Vector2> dest)
    {
        int n = source.Count;
        if (n < 3)
        {
            return null;
        }

        // Solve two independent systems:
        //   dest.X = m11*src.X + m21*src.Y + m31
        //   dest.Y = m12*src.X + m22*src.Y + m32
        // via normal equations: A^T A x = A^T b

        // Build A^T A (3x3) and A^T b (3x1) for each component
        double sXX = 0, sYY = 0, sXY = 0, sX = 0, sY = 0;
        double sDxX = 0, sDxY = 0, sDx = 0;
        double sDyX = 0, sDyY = 0, sDy = 0;

        for (int i = 0; i < n; i++)
        {
            double sx = source[i].X, sy = source[i].Y;
            double dx = dest[i].X, dy = dest[i].Y;

            sXX += sx * sx;
            sYY += sy * sy;
            sXY += sx * sy;
            sX += sx;
            sY += sy;

            sDxX += dx * sx;
            sDxY += dx * sy;
            sDx += dx;

            sDyX += dy * sx;
            sDyY += dy * sy;
            sDy += dy;
        }

        // A^T A = [[sXX, sXY, sX], [sXY, sYY, sY], [sX, sY, n]]
        // Solve via Cramer's rule
        double det = sXX * (sYY * n - sY * sY)
                   - sXY * (sXY * n - sY * sX)
                   + sX * (sXY * sY - sYY * sX);

        if (Math.Abs(det) < 1e-12)
        {
            return null;
        }

        double invDet = 1.0 / det;

        // Cofactor matrix for inversion
        double c00 = sYY * n - sY * sY;
        double c01 = -(sXY * n - sY * sX);
        double c02 = sXY * sY - sYY * sX;
        double c10 = -(sXY * n - sX * sY);
        double c11 = sXX * n - sX * sX;
        double c12 = -(sXX * sY - sXY * sX);
        double c20 = sXY * sY - sYY * sX;
        double c21 = -(sXX * sY - sX * sXY);
        double c22 = sXX * sYY - sXY * sXY;

        // Solve for X component: [m11, m21, m31]
        double m11 = (c00 * sDxX + c01 * sDxY + c02 * sDx) * invDet;
        double m21 = (c10 * sDxX + c11 * sDxY + c12 * sDx) * invDet;
        double m31 = (c20 * sDxX + c21 * sDxY + c22 * sDx) * invDet;

        // Solve for Y component: [m12, m22, m32]
        double m12 = (c00 * sDyX + c01 * sDyY + c02 * sDy) * invDet;
        double m22 = (c10 * sDyX + c11 * sDyY + c12 * sDy) * invDet;
        double m32 = (c20 * sDyX + c21 * sDyY + c22 * sDy) * invDet;

        return new Matrix3x2(
            (float)m11, (float)m12,
            (float)m21, (float)m22,
            (float)m31, (float)m32
        );
    }

    private List<(double RA, double Dec, double VMag)> QueryCatalogStarsInRegion(WCS origin, double radiusDeg)
    {
        var result = new List<(double RA, double Dec, double VMag)>();

        var centerRA = origin.CenterRA;     // hours
        var centerDec = origin.CenterDec;   // degrees

        // RA search radius in hours, adjusted for cos(dec)
        var cosDecl = Math.Cos(centerDec * Math.PI / 180.0);
        var radiusRA = cosDecl > 0.01 ? radiusDeg / (15.0 * cosDecl) : 24.0;

        var minRA = centerRA - radiusRA;
        var maxRA = centerRA + radiusRA;
        var minDec = Math.Max(-90.0, centerDec - radiusDeg);
        var maxDec = Math.Min(90.0, centerDec + radiusDeg);

        // Grid cells are 1/15 hour in RA and 1° in Dec
        const double raCellSize = 1.0 / 15.0;

        var seen = new HashSet<CatalogIndex>();

        for (var cellRA = Math.Floor(minRA / raCellSize) * raCellSize + raCellSize * 0.5;
             cellRA <= maxRA;
             cellRA += raCellSize)
        {
            var queryRA = ConditionRA(cellRA);
            for (var cellDec = Math.Floor(minDec) + 0.5; cellDec <= maxDec; cellDec += 1.0)
            {
                foreach (var idx in db.CoordinateGrid[queryRA, cellDec])
                {
                    if (seen.Add(idx) && db.TryLookupByIndex(idx, out var obj) && obj.ObjectType is ObjectType.Star)
                    {
                        result.Add((obj.RA, obj.Dec, Half.IsNaN(obj.V_Mag) ? 99.0 : (double)obj.V_Mag));
                    }
                }
            }
        }

        return result;
    }

    private static List<ImagedStar> ProjectCatalogStars(
        List<(double RA, double Dec, double VMag)> catalogCoords,
        WCS origin,
        double pixelScaleRad,
        double cx,
        double cy,
        ImageDim dim,
        double xSign
    )
    {
        var projected = new List<ImagedStar>();

        var alpha0 = origin.CenterRA * Math.PI / 12.0;
        var delta0 = origin.CenterDec * Math.PI / 180.0;
        var sinDelta0 = Math.Sin(delta0);
        var cosDelta0 = Math.Cos(delta0);

        var marginX = dim.Width * 0.1;
        var marginY = dim.Height * 0.1;

        foreach (var (ra, dec, _) in catalogCoords)
        {
            var alpha = ra * Math.PI / 12.0;
            var delta = dec * Math.PI / 180.0;
            var deltaAlpha = alpha - alpha0;

            var sinDelta = Math.Sin(delta);
            var cosDelta = Math.Cos(delta);
            var cosDeltaAlpha = Math.Cos(deltaAlpha);

            var cosC = sinDelta0 * sinDelta + cosDelta0 * cosDelta * cosDeltaAlpha;
            if (cosC <= 0)
            {
                continue; // behind the tangent plane
            }

            var xi = cosDelta * Math.Sin(deltaAlpha) / cosC;
            var eta = (cosDelta0 * sinDelta - sinDelta0 * cosDelta * cosDeltaAlpha) / cosC;

            var xPix = (float)(cx + xSign * xi / pixelScaleRad);
            var yPix = (float)(cy - eta / pixelScaleRad);

            if (xPix >= -marginX && xPix <= dim.Width + marginX &&
                yPix >= -marginY && yPix <= dim.Height + marginY)
            {
                projected.Add(new ImagedStar(2f, 2f, 100f, 1000f, xPix, yPix));
            }
        }

        return projected;
    }

    private static WCS? InverseTanProject(
        Vector2 pixelPos,
        WCS origin,
        double pixelScaleRad,
        double cx,
        double cy,
        double xSign
    )
    {
        var alpha0 = origin.CenterRA * Math.PI / 12.0;
        var delta0 = origin.CenterDec * Math.PI / 180.0;
        var sinDelta0 = Math.Sin(delta0);
        var cosDelta0 = Math.Cos(delta0);

        var xi = xSign * (pixelPos.X - cx) * pixelScaleRad;
        var eta = -(pixelPos.Y - cy) * pixelScaleRad;

        var rho = Math.Sqrt(xi * xi + eta * eta);

        if (rho < 1e-12)
        {
            return origin;
        }

        var c = Math.Atan(rho);
        var sinC = Math.Sin(c);
        var cosC = Math.Cos(c);

        var centerDec = Math.Asin(cosC * sinDelta0 + eta * sinC * cosDelta0 / rho) * 180.0 / Math.PI;
        var centerRA = (alpha0 + Math.Atan2(xi * sinC, rho * cosDelta0 * cosC - eta * sinDelta0 * sinC)) * 12.0 / Math.PI;

        return new WCS(ConditionRA(centerRA), centerDec);
    }
}
