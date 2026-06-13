using System;
using System.Collections.Generic;

namespace TianWen.Lib.Astrometry.Catalogs
{
    /// <summary>
    /// Counts catalog stars in a sky field, by walking the <see cref="ICelestialObjectDB.CoordinateGrid"/>
    /// cells that overlap the field box. Shared by the session's first-scout obstruction oracle / zenith
    /// gauge (expected-star-count + effective-limiting-magnitude inversion) and, via the same cell-walk
    /// core, by the fake camera's <c>SyntheticStarFieldRenderer.ProjectCatalogStars</c> so the two cannot
    /// drift apart (feedback_one_path).
    /// </summary>
    public static class CatalogStarCounter
    {
        /// <summary>Magnitude bins for <see cref="CountStarsByMagnitude"/>: 30 bins, 0.5 mag each, covering V 0..15.</summary>
        public const int MagBinCount = 30;

        private const double Deg2Rad = Math.PI / 180.0;

        /// <summary>
        /// Enumerates the unique <see cref="ObjectType.Star"/> catalog entries (non-NaN V magnitude) whose
        /// containing grid cells fall within <paramref name="searchRadiusDeg"/> of the field centre. This is
        /// the shared cell-walk core: it dedupes <see cref="CatalogIndex"/> across overlapping cells and applies
        /// only the star/NaN filter — callers apply their own magnitude and spatial (box / projection) tests.
        /// </summary>
        public static IEnumerable<CelestialObject> EnumerateFieldStars(
            ICelestialObjectDB db, double raHours, double decDeg, double searchRadiusDeg)
        {
            var dec0Rad = decDeg * Deg2Rad;
            var decMin = decDeg - searchRadiusDeg;
            var decMax = decDeg + searchRadiusDeg;
            // RA half-width in hours, widened by 1/cos(dec) so the cell box still covers the field near the poles.
            var raRadiusHours = searchRadiusDeg / (15.0 * Math.Max(Math.Cos(dec0Rad), 0.01));
            var raMinH = raHours - raRadiusHours;
            var raMaxH = raHours + raRadiusHours;

            var grid = db.CoordinateGrid;
            // Grid cells are ~1/15 h in RA, ~1 deg in Dec — step at that resolution so every overlapping cell is hit.
            const double raStepH = 1.0 / 15.0;
            const double decStep = 1.0;

            var seen = new HashSet<CatalogIndex>();
            for (var dec = decMin; dec <= decMax + decStep; dec += decStep)
            {
                var queryDec = Math.Clamp(dec, -90.0, 90.0);
                for (var ra = raMinH; ra <= raMaxH + raStepH; ra += raStepH)
                {
                    var queryRA = ((ra % 24.0) + 24.0) % 24.0; // wrap across 0/24 h
                    foreach (var index in grid[queryRA, queryDec])
                    {
                        if (!seen.Add(index))
                        {
                            continue;
                        }
                        if (db.TryLookupByIndex(index, out var obj)
                            && obj.ObjectType is ObjectType.Star
                            && !Half.IsNaN(obj.V_Mag))
                        {
                            yield return obj;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// True when a star at (<paramref name="starRaHours"/>, <paramref name="starDecDeg"/>) lies inside the
        /// <paramref name="fovWdeg"/> x <paramref name="fovHdeg"/> box centred on the field. RA separation is
        /// projected onto the sky by cos(dec) so the box width is in true degrees, and RA wrap across 0/24 h is
        /// handled.
        /// </summary>
        private static bool InBox(
            double starRaHours, double starDecDeg,
            double centreRaHours, double centreDecDeg, double cosCentreDec, double fovWdeg, double fovHdeg)
        {
            if (Math.Abs(starDecDeg - centreDecDeg) > fovHdeg * 0.5)
            {
                return false;
            }
            var dRaHours = starRaHours - centreRaHours;
            dRaHours -= 24.0 * Math.Round(dRaHours / 24.0); // wrap to [-12, 12] h
            var dRaSkyDeg = dRaHours * 15.0 * cosCentreDec;
            return Math.Abs(dRaSkyDeg) <= fovWdeg * 0.5;
        }

        private static double SearchRadiusDeg(double fovWdeg, double fovHdeg)
            => Math.Sqrt(fovWdeg * fovWdeg + fovHdeg * fovHdeg) * 0.5 + 0.1; // half-diagonal + small margin

        /// <summary>
        /// Number of catalog stars no fainter than <paramref name="magLimit"/> inside the
        /// <paramref name="fovWdeg"/> x <paramref name="fovHdeg"/> field box centred on (RA, Dec).
        /// </summary>
        public static int CountStarsInField(
            ICelestialObjectDB db, double raHours, double decDeg, double fovWdeg, double fovHdeg, double magLimit)
        {
            var cosDec = Math.Cos(decDeg * Deg2Rad);
            var count = 0;
            foreach (var obj in EnumerateFieldStars(db, raHours, decDeg, SearchRadiusDeg(fovWdeg, fovHdeg)))
            {
                if ((double)obj.V_Mag <= magLimit
                    && InBox(obj.RA, (double)obj.Dec, raHours, decDeg, cosDec, fovWdeg, fovHdeg))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Cumulative star counts per 0.5-mag bin inside the field box: <c>result[b]</c> is the number of stars
        /// with V magnitude &lt;= <c>(b + 1) * 0.5</c> (so the array is monotone non-decreasing, bin 29 = V&lt;=15).
        /// One cell-walk serves any magnitude query, and inverting it ("which mag yields N detected?") gives the
        /// effective limiting magnitude for the zenith transparency gauge.
        /// </summary>
        public static int[] CountStarsByMagnitude(
            ICelestialObjectDB db, double raHours, double decDeg, double fovWdeg, double fovHdeg)
        {
            var cosDec = Math.Cos(decDeg * Deg2Rad);
            var bins = new int[MagBinCount];
            foreach (var obj in EnumerateFieldStars(db, raHours, decDeg, SearchRadiusDeg(fovWdeg, fovHdeg)))
            {
                if (!InBox(obj.RA, (double)obj.Dec, raHours, decDeg, cosDec, fovWdeg, fovHdeg))
                {
                    continue;
                }
                // Lowest bin whose threshold (b+1)*0.5 is >= this star's mag; brighter stars also count in all
                // fainter bins, so accumulate into a per-bin tally first and make it cumulative below.
                var mag = (double)obj.V_Mag;
                var bin = (int)Math.Ceiling(mag / 0.5) - 1;
                bin = Math.Clamp(bin, 0, MagBinCount - 1);
                bins[bin]++;
            }
            // Make cumulative: bins[b] = count with mag <= (b+1)*0.5.
            for (var b = 1; b < MagBinCount; b++)
            {
                bins[b] += bins[b - 1];
            }
            return bins;
        }
    }
}
