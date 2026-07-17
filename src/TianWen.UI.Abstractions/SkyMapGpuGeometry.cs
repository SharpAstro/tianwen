using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Backend-agnostic geometry builders for the GPU sky map: pure float-list construction of
    /// J2000 unit-vector line lists (constellation figures/boundaries, RA/Dec grid, ecliptic,
    /// horizon, meridian, Alt/Az grid) and the 5-float star instance stream
    /// (<see cref="SkyMapState.FloatsPerStar"/>). The Vulkan pipeline (TianWen.UI.Shared) and the
    /// WebGL pipeline (TianWen.UI.Web) both upload these into their own persistent GPU buffers —
    /// the geometry math has exactly one implementation here.
    /// </summary>
    public static class SkyMapGpuGeometry
    {
        /// <summary>Grid scale definitions: (raStepHours, decStepDeg, minFov, maxFov). A scale
        /// draws only the lines coarser scales don't already draw; renderers overlay every scale
        /// whose FOV window contains the current field of view.</summary>
        public static readonly (double RaStep, double DecStep, double MinFov, double MaxFov)[] GridScales =
        [
            (6.0,  30.0,  30.0, 999.0),
            (3.0,  15.0,  10.0, 120.0),
            (1.0,  10.0,   3.0,  40.0),
            (0.5,   5.0,   1.0,  15.0),
            (10.0 / 60.0, 1.0, 0.2, 5.0),
        ];

        /// <summary>
        /// The bright-star seed: 5 floats per star (unit x/y/z, vMag, B-V) for the ~1000
        /// constellation-figure HIP stars — the buffer the Vulkan pipeline shows while the full
        /// Tycho-2 build runs, and the WHOLE star field for the Lightweight/browser build
        /// (naked-eye sky; tyc2 is stripped there).
        /// </summary>
        public static List<float> BuildFigureStarInstances(ICelestialObjectDB db)
        {
            var hipNumbers = ConstellationFigures.AllFigureStarHipNumbers;
            var floats = new List<float>(hipNumbers.Count * SkyMapState.FloatsPerStar);

            foreach (var hip in hipNumbers)
            {
                // Lite lookup: ra/dec/mag/bv straight from the catalog arrays, skipping the
                // per-star constellation polygon test + cross-ref/type machinery a plotted seed
                // dot never needs.
                if (!db.TryGetHipStarLite(hip, out var ra, out var dec, out var vMag, out var bv)
                    || float.IsNaN(vMag))
                {
                    continue;
                }

                var (x, y, z) = SkyMapState.RaDecToUnitVec(ra, dec);
                floats.Add(x);
                floats.Add(y);
                floats.Add(z);
                floats.Add(vMag);
                floats.Add(float.IsNaN(bv) ? 0.65f : bv); // default to solar B-V if unknown
            }

            return floats;
        }

        /// <summary>
        /// The full HR (Bright Star) catalog as star instances (~9k stars, the naked-eye sky) —
        /// the browser star field. Unlike the HIP-keyed figure seed this needs no cross-identity
        /// resolution (a Lightweight build has no Tycho-2), just catalog enumeration. Do NOT
        /// combine with <see cref="BuildFigureStarInstances"/> in one buffer: the figure stars
        /// resolve to the same bright stars, and the additive star blend would double them.
        /// </summary>
        public static List<float> BuildHrStarInstances(ICelestialObjectDB db)
        {
            var floats = new List<float>(9200 * SkyMapState.FloatsPerStar);
            foreach (var idx in db.AllObjectIndices)
            {
                if (idx.ToCatalog() != Catalog.HR
                    || !db.TryLookupByIndex(idx, out var obj)
                    || Half.IsNaN(obj.V_Mag))
                {
                    continue;
                }

                var (x, y, z) = SkyMapState.RaDecToUnitVec(obj.RA, obj.Dec);
                floats.Add(x);
                floats.Add(y);
                floats.Add(z);
                floats.Add((float)obj.V_Mag);
                floats.Add(Half.IsNaN(obj.BMinusV) ? 0.65f : (float)obj.BMinusV);
            }

            return floats;
        }

        /// <summary>Constellation stick figures as a line list (6 floats per segment).</summary>
        public static List<float> BuildConstellationFigureLines(ICelestialObjectDB db)
        {
            var floats = new List<float>(4096);

            foreach (var constellation in Enum.GetValues<Constellation>())
            {
                if (constellation is Constellation.SerpensCaput or Constellation.SerpensCauda)
                {
                    continue;
                }

                var figure = constellation.Figure;
                if (figure.IsDefaultOrEmpty)
                {
                    continue;
                }

                foreach (var polyline in figure)
                {
                    float prevX = 0, prevY = 0, prevZ = 0;
                    var hasPrev = false;

                    foreach (var hip in polyline)
                    {
                        if (!db.TryLookupHIP(hip, out var ra, out var dec, out _, out _))
                        {
                            hasPrev = false;
                            continue;
                        }

                        var (x, y, z) = SkyMapState.RaDecToUnitVec(ra, dec);

                        if (hasPrev)
                        {
                            floats.Add(prevX); floats.Add(prevY); floats.Add(prevZ);
                            floats.Add(x); floats.Add(y); floats.Add(z);
                        }

                        prevX = x; prevY = y; prevZ = z;
                        hasPrev = true;
                    }
                }
            }

            return floats;
        }

        /// <summary>
        /// IAU constellation boundaries as a line list. Boundaries are defined in B1875
        /// (Delporte 1930 standard) while stars + grid are J2000; each tessellated point is
        /// precessed B1875 -> J2000 or the boundaries sit ~1.74 deg off the stars they delimit
        /// (and pan at a visibly different speed under the stereographic projection).
        /// </summary>
        public static List<float> BuildConstellationBoundaryLines()
        {
            var floats = new List<float>(65536);

            const double FromEpoch = 1875.0;
            const double ToEpoch = 2000.0;

            static (double RA, double Dec) Precess1875ToJ2000(double ra, double dec)
                => CoordinateUtils.Precess(ra, dec, FromEpoch, ToEpoch);

            foreach (var edge in ConstellationEdges.Edges)
            {
                if (edge.Type == ConstellationEdges.EdgeType.Parallel)
                {
                    // Constant B1875 Dec arc from RA1 to RA2, tessellated in B1875 (the shape is
                    // correct there), each point precessed to J2000.
                    var raRange = edge.RA2 - edge.RA1;
                    if (raRange < -12) raRange += 24;
                    if (raRange > 12) raRange -= 24;
                    var steps = Math.Max(5, (int)(Math.Abs(raRange) * 8));
                    TessellateArc(floats, steps, i =>
                        Precess1875ToJ2000(edge.RA1 + i * raRange / steps, edge.Dec1));
                }
                else if (edge.Type == ConstellationEdges.EdgeType.Meridian)
                {
                    // Constant B1875 RA arc from Dec1 to Dec2 — precessed per step.
                    var decRange = edge.Dec2 - edge.Dec1;
                    var steps = Math.Max(5, (int)(Math.Abs(decRange) / 2));
                    TessellateArc(floats, steps, i =>
                        Precess1875ToJ2000(edge.RA1, edge.Dec1 + i * decRange / steps));
                }
                else
                {
                    // Straight segment: just two endpoints, both precessed.
                    var (p1RA, p1Dec) = Precess1875ToJ2000(edge.RA1, edge.Dec1);
                    var (p2RA, p2Dec) = Precess1875ToJ2000(edge.RA2, edge.Dec2);
                    var (x1, y1, z1) = SkyMapState.RaDecToUnitVec(p1RA, p1Dec);
                    var (x2, y2, z2) = SkyMapState.RaDecToUnitVec(p2RA, p2Dec);
                    floats.Add(x1); floats.Add(y1); floats.Add(z1);
                    floats.Add(x2); floats.Add(y2); floats.Add(z2);
                }
            }

            return floats;
        }

        /// <summary>
        /// One grid scale's RA/Dec line list, skipping lines a coarser scale already draws
        /// (renderers stack the active scales, so shared lines must not double-draw).
        /// </summary>
        public static List<float> BuildGridLines(int scaleIndex)
        {
            var (raStep, decStep, _, _) = GridScales[scaleIndex];
            var floats = new List<float>(32768);

            // RA lines (constant RA, varying Dec — great circles)
            var lineSteps = Math.Max(60, Math.Min(200, (int)(180.0 / decStep * 2)));
            for (var ra = 0.0; ra < 24.0; ra += raStep)
            {
                if (IsCoarserRaLine(ra, scaleIndex))
                {
                    continue;
                }
                TessellateArc(floats, lineSteps, j => (ra, -90.0 + j * 180.0 / lineSteps));
            }

            // Dec lines (constant Dec, varying RA — small circles)
            var raSteps = Math.Max(60, Math.Min(200, (int)(24.0 / raStep * 2)));
            for (var dec = -90.0 + decStep; dec < 90.0; dec += decStep)
            {
                if (IsCoarserDecLine(dec, scaleIndex))
                {
                    continue;
                }
                TessellateArc(floats, raSteps, j => (j * 24.0 / raSteps, dec));
            }

            return floats;
        }

        /// <summary>
        /// The ecliptic great circle (the Sun's annual path), tessellated as a line list of
        /// J2000 unit vectors. Inclination is the mean obliquity at J2000.0 (IAU 2006 / SOFA).
        /// </summary>
        public static List<float> BuildEclipticLine()
        {
            const double obliquityJ2000Deg = 23.4392911;
            var (sinE, cosE) = Math.SinCos(double.DegreesToRadians(obliquityJ2000Deg));

            const int steps = 360;
            var floats = new List<float>(steps * 6);
            float prevX = 0, prevY = 0, prevZ = 0;
            for (var i = 0; i <= steps; i++)
            {
                // lambda = ecliptic longitude in radians, sweeping the full circle.
                var lambda = i * (2.0 * Math.PI / steps);
                var (sinL, cosL) = Math.SinCos(lambda);
                // J2000 unit vector for ecliptic-longitude lambda, latitude 0.
                var x = (float)cosL;
                var y = (float)(sinL * cosE);
                var z = (float)(sinL * sinE);

                if (i > 0)
                {
                    floats.Add(prevX); floats.Add(prevY); floats.Add(prevZ);
                    floats.Add(x); floats.Add(y); floats.Add(z);
                }
                prevX = x; prevY = y; prevZ = z;
            }

            return floats;
        }

        /// <summary>Dynamic horizon curve for the site (line list of unit vectors).</summary>
        public static void BuildHorizonLine(SiteContext site, List<float> floats)
        {
            if (!site.IsValid)
            {
                return;
            }

            const int steps = 120;
            float prevX = 0, prevY = 0, prevZ = 0;
            var hasPrev = false;

            for (var i = 0; i <= steps; i++)
            {
                var ra = i * 24.0 / steps;
                var decHorizon = site.HorizonDec(ra);
                var (x, y, z) = SkyMapState.RaDecToUnitVec(ra, decHorizon);

                if (hasPrev)
                {
                    floats.Add(prevX); floats.Add(prevY); floats.Add(prevZ);
                    floats.Add(x); floats.Add(y); floats.Add(z);
                }

                prevX = x; prevY = y; prevZ = z;
                hasPrev = true;
            }
        }

        /// <summary>Dynamic meridian line (the LST great circle).</summary>
        public static void BuildMeridianLine(double lst, List<float> floats)
        {
            const int steps = 200;
            var antiLst = (lst + 12.0) % 24.0;

            // First half: LST line from south pole to north pole
            TessellateArc(floats, steps / 2, i => (lst, -90.0 + i * 180.0 / (steps / 2)));
            // Second half: anti-LST line from north pole to south pole
            TessellateArc(floats, steps / 2, i => (antiLst, 90.0 - i * 180.0 / (steps / 2)));
        }

        /// <summary>
        /// Dynamic Alt/Az grid: altitude circles at 10/20/30/45/60/80 degrees, azimuth lines
        /// every 30 degrees, converted to J2000 unit vectors for the observer's latitude + LST.
        /// </summary>
        public static void BuildAltAzGrid(SiteContext site, List<float> floats)
        {
            if (!site.IsValid)
            {
                return;
            }

            // Altitude circles: constant altitude, sweep azimuth 0..360
            double[] altitudes = [10, 20, 30, 45, 60, 80];
            const int azSteps = 120;

            foreach (var alt in altitudes)
            {
                TessellateArc(floats, azSteps, i =>
                {
                    var az = i * 360.0 / azSteps;
                    AltAzToRaDec(alt, az, site, out var ra, out var dec);
                    return (ra, dec);
                });
            }

            // Azimuth lines: constant azimuth, sweep altitude 0..89
            const int altSteps = 60;
            for (var az = 0.0; az < 360.0; az += 30.0)
            {
                TessellateArc(floats, altSteps, i =>
                {
                    var a = i * 89.0 / altSteps;
                    AltAzToRaDec(a, az, site, out var ra, out var dec);
                    return (ra, dec);
                });
            }
        }

        /// <summary>
        /// Convert horizontal coordinates (Alt, Az in degrees) to equatorial (RA in hours,
        /// Dec in degrees) at the site's latitude + LST.
        /// </summary>
        public static void AltAzToRaDec(
            double altDeg, double azDeg, SiteContext site,
            out double raHours, out double decDeg)
        {
            var (sinAlt, cosAlt) = Math.SinCos(double.DegreesToRadians(altDeg));
            var (sinAz, cosAz) = Math.SinCos(double.DegreesToRadians(azDeg));

            var sinDec = site.SinLat * sinAlt + site.CosLat * cosAlt * cosAz;
            decDeg = double.RadiansToDegrees(Math.Asin(sinDec));

            var cosDec = Math.Cos(Math.Asin(sinDec));
            if (Math.Abs(cosDec) < 1e-12)
            {
                raHours = site.LST;
                return;
            }

            var sinHA = -sinAz * cosAlt / cosDec;
            var cosHA = (sinAlt - site.SinLat * sinDec) / (site.CosLat * cosDec);
            var ha = Math.Atan2(sinHA, cosHA); // radians

            raHours = (site.LST - ha / (Math.PI / 12.0)) % 24.0;
            if (raHours < 0) raHours += 24.0;
        }

        /// <summary>Tessellate an arc into line segments (line list: 2 vertices per segment).</summary>
        public static void TessellateArc(List<float> floats, int steps, Func<int, (double RA, double Dec)> coordFunc)
        {
            float prevX = 0, prevY = 0, prevZ = 0;
            var hasPrev = false;

            for (var i = 0; i <= steps; i++)
            {
                var (ra, dec) = coordFunc(i);
                var (x, y, z) = SkyMapState.RaDecToUnitVec(ra, dec);

                if (hasPrev)
                {
                    floats.Add(prevX); floats.Add(prevY); floats.Add(prevZ);
                    floats.Add(x); floats.Add(y); floats.Add(z);
                }

                prevX = x; prevY = y; prevZ = z;
                hasPrev = true;
            }
        }

        private static bool IsCoarserRaLine(double ra, int scaleIndex)
        {
            for (var j = 0; j < scaleIndex; j++)
            {
                var coarserStep = GridScales[j].RaStep;
                var remainder = ra % coarserStep;
                if (remainder < 1e-9 || coarserStep - remainder < 1e-9)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsCoarserDecLine(double dec, int scaleIndex)
        {
            for (var j = 0; j < scaleIndex; j++)
            {
                var coarserStep = GridScales[j].DecStep;
                var remainder = Math.Abs(dec) % coarserStep;
                if (remainder < 1e-9 || coarserStep - remainder < 1e-9)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
