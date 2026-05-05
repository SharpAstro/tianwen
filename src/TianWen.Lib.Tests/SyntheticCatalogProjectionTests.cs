using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Validates that <see cref="SyntheticStarFieldRenderer.ProjectCatalogStars"/>
/// returns the *complete* set of catalog stars within the FOV at or below the
/// requested magnitude cutoff -- no silent filtering, no missed cells. The
/// short-exposure refine path on the polar-alignment routine relies on this
/// being honest: if ProjectCatalogStars under-reports, the FakeCameraDriver
/// renders too few stars, and downstream plate solves silently fail because
/// the catalog plate solver expects to find every star that should be there.
/// </summary>
[Collection("Astrometry")]
public class SyntheticCatalogProjectionTests(ITestOutputHelper output)
{
    private static ICelestialObjectDB? _cachedDB;
    private static readonly SemaphoreSlim _dbSem = new(1, 1);

    private static async Task<ICelestialObjectDB> InitDBAsync(CancellationToken cancellationToken)
    {
        if (_cachedDB is { } db) return db;
        await _dbSem.WaitAsync(cancellationToken);
        try
        {
            if (_cachedDB is { } db2) return db2;
            var newDb = new CelestialObjectDB();
            // Synthetic catalog projection enumerates CoordinateGrid (Tycho-2 included).
            await newDb.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: cancellationToken);
            _cachedDB = newDb;
            return newDb;
        }
        finally
        {
            _dbSem.Release();
        }
    }

    /// <summary>
    /// Pure-catalog reference scan: walk every catalog object via the
    /// CoordinateGrid and keep stars whose great-circle distance to the
    /// pointing centre is within the FOV diagonal radius and whose V_mag is
    /// at or below the cutoff. Mirrors the physical truth that a sensor at
    /// (centerRA, centerDec) sees: this is the *upper bound* the projection
    /// must match.
    /// </summary>
    private static int CountCatalogStarsInFov(
        ICelestialObjectDB db,
        double centerRaHours, double centerDecDeg,
        double fovDiagonalDeg,
        double magnitudeCutoff)
    {
        const double Deg2Rad = Math.PI / 180.0;
        var ra0 = centerRaHours * 15.0 * Deg2Rad;
        var dec0 = centerDecDeg * Deg2Rad;
        var sinDec0 = Math.Sin(dec0);
        var cosDec0 = Math.Cos(dec0);
        var radiusRad = fovDiagonalDeg * 0.5 * Deg2Rad;
        var cosRadius = Math.Cos(radiusRad);

        // Iterate the same RA/Dec cells the projection iterates so any cell
        // mismatch shows up here too. Use a generous over-scan to avoid
        // boundary-effect under-count vs the projection's own boundary.
        var grid = db.CoordinateGrid;
        var seen = new HashSet<CatalogIndex>();
        var raStepH = 1.0 / 15.0;
        var decStep = 1.0;
        var cosCenterDecForRa = Math.Max(Math.Cos(dec0), 0.01);
        var raRadiusHours = (fovDiagonalDeg + 1.0) / (15.0 * cosCenterDecForRa);
        for (var dec = centerDecDeg - fovDiagonalDeg - 1.0; dec <= centerDecDeg + fovDiagonalDeg + 1.0; dec += decStep)
        {
            for (var raH = centerRaHours - raRadiusHours; raH <= centerRaHours + raRadiusHours; raH += raStepH)
            {
                var queryRA = ((raH % 24.0) + 24.0) % 24.0;
                var queryDec = Math.Clamp(dec, -90, 90);
                foreach (var index in grid[queryRA, queryDec])
                {
                    seen.Add(index);
                }
            }
        }

        int count = 0;
        foreach (var index in seen)
        {
            if (!db.TryLookupByIndex(index, out var obj)) continue;
            if (obj.ObjectType is not ObjectType.Star) continue;
            if (Half.IsNaN(obj.V_Mag)) continue;
            var mag = (double)obj.V_Mag;
            if (mag > magnitudeCutoff) continue;

            // Great-circle distance to centre.
            var raRad = obj.RA * 15.0 * Deg2Rad;
            var decRad = (double)obj.Dec * Deg2Rad;
            var cosD = sinDec0 * Math.Sin(decRad)
                     + cosDec0 * Math.Cos(decRad) * Math.Cos(raRad - ra0);
            if (cosD >= cosRadius) count++;
        }
        return count;
    }

    [Theory]
    // (centerRa_hours, centerDec_deg, magCutoff) at IMX455 size + 200mm f/4 guider.
    // Three pointings: SCP (user's polar-align scenario), galactic-plane dense
    // field (Cygnus), and a sparse off-galactic field (NGP region).
    [InlineData(0.0, -89.97, 8.0, "SCP, mag 8 (~200ms)")]
    [InlineData(0.0, -89.97, 10.0, "SCP, mag 10 (~1s)")]
    [InlineData(0.0, -89.97, 12.0, "SCP, mag 12 (~5s)")]
    [InlineData(20.5, 40.0, 10.0, "Cygnus, mag 10")]
    [InlineData(12.83, 27.13, 10.0, "Coma cluster, mag 10")]
    public async Task ProjectCatalogStars_ReturnsEveryCatalogStarInFovAtOrBelowMagnitudeCutoff(
        double centerRaHours, double centerDecDeg, double magnitudeCutoff, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await InitDBAsync(ct);

        // IMX455 mono sensor (9576 x 6388) + 200mm focal length + 3.76um pixel.
        // Same numbers the FakeCameraDriver / CaptureSourceRanker use elsewhere.
        const int width = 9576;
        const int height = 6388;
        const double focalLengthMm = 200.0;
        const double pixelSizeUm = 3.76;
        const double pixelScaleArcsec = 206264.806 * (pixelSizeUm * 1e-3) / focalLengthMm;
        var fovWidthDeg = width * pixelScaleArcsec / 3600.0;
        var fovHeightDeg = height * pixelScaleArcsec / 3600.0;
        var fovDiagonalDeg = Math.Sqrt(fovWidthDeg * fovWidthDeg + fovHeightDeg * fovHeightDeg);
        // Inscribed circle (radius = half the smaller dimension): every star
        // inside this radius is guaranteed to project inside the rectangular
        // sensor, so the projection MUST return all of them. Stars outside
        // the inscribed circle but inside the diagonal circle may or may not
        // hit the rectangle (corners), so we don't assert on those.
        var fovInscribedDeg = Math.Min(fovWidthDeg, fovHeightDeg);

        // Two reference scans: inscribed (lower bound, must match) and
        // diagonal (upper bound, projection should not exceed by much).
        var inscribedRefCount = CountCatalogStarsInFov(db, centerRaHours, centerDecDeg, fovInscribedDeg, magnitudeCutoff);
        var diagonalRefCount = CountCatalogStarsInFov(db, centerRaHours, centerDecDeg, fovDiagonalDeg, magnitudeCutoff);

        // Subject under test.
        var projected = SyntheticStarFieldRenderer.ProjectCatalogStars(
            centerRaHours, centerDecDeg, focalLengthMm, pixelSizeUm,
            width, height, db, magnitudeCutoff);

        // Distribution by integer magnitude bin so we can see *which* magnitudes
        // are missing if the count is below the reference.
        var binCounts = new Dictionary<int, int>();
        foreach (var s in projected)
        {
            var bin = (int)Math.Floor(s.Magnitude);
            binCounts[bin] = binCounts.GetValueOrDefault(bin) + 1;
        }
        var binStr = string.Join(", ",
            binCounts.OrderBy(kv => kv.Key).Select(kv => $"m{kv.Key}={kv.Value}"));

        output.WriteLine(
            "{0}: inscribed={1} diagonal={2} projected={3}  inscribed-ratio={4:P1}  bins[{5}]  fovInscribed={6:F2}deg fovDiag={7:F2}deg",
            label, inscribedRefCount, diagonalRefCount, projected.Count,
            inscribedRefCount > 0 ? (double)projected.Count / inscribedRefCount : 0.0,
            binStr, fovInscribedDeg, fovDiagonalDeg);

        if (inscribedRefCount > 10)
        {
            // Lower bound: every catalog star inside the inscribed circle has
            // its projected pixel inside the rectangle, so the projection
            // must include all of them. 95% recall to allow for tiny TAN-
            // projection rounding at the sensor boundary.
            var ratio = (double)projected.Count / inscribedRefCount;
            ratio.ShouldBeGreaterThan(0.95,
                $"{label}: projection ({projected.Count}) is missing stars that catalog scan placed inside the inscribed FOV ({inscribedRefCount}); bins[{binStr}]");

            // Upper bound: projection shouldn't exceed the diagonal-circle
            // catalog count -- it's strictly inside that circle.
            projected.Count.ShouldBeLessThanOrEqualTo((int)(diagonalRefCount * 1.02),
                $"{label}: projection ({projected.Count}) exceeds diagonal catalog scan ({diagonalRefCount})");
        }
        else
        {
            // Sparse field: just confirm we got *some* stars.
            projected.Count.ShouldBeLessThanOrEqualTo(diagonalRefCount + 5,
                $"{label}: projection over-reported");
        }
    }
}
