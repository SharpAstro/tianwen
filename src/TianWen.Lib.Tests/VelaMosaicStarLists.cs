using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Frozen STAR LISTS from the user's 20-panel Vela SNR mosaic (ASI533MC Pro on a
    /// Samyang 135 mm, 3008x3008, ~6"/px, so each panel is a 5-degree SQUARE field) --
    /// the real data that exposed the dense-field silent-garbage failure in
    /// <c>CatalogPlateSolver</c>.
    ///
    /// <para><b>Why star lists and not FITS.</b> The failure was purely geometric: with
    /// ~6,400 catalog stars projecting into frame, every detected star finds SOME catalog
    /// star within the match tolerance by chance, so the matcher reported 1,434 "matches"
    /// whose residual distribution exactly reproduced the Poisson nearest-neighbour
    /// prediction (19.23 px median observed vs 19.5 predicted). Reproducing that needs the
    /// positions and the DENSITY, not the pixels -- 96 frames of FITS is ~9 GB, the same
    /// fields as star lists are a couple of MB. It also keeps the regression honest about
    /// what it covers: matching geometry, not star detection.</para>
    ///
    /// <para><b>One catalog for the whole mosaic.</b> The panels overlap heavily (5-degree
    /// squares spaced ~1.5 degrees), so a shared list is ~13x smaller than per-panel ones
    /// AND gives cross-panel star IDENTITY: catalog index <c>i</c> is the same physical
    /// star in every panel, which is what lets the overlap tests match stars exactly
    /// instead of by proximity.</para>
    ///
    /// <para><b>Provenance.</b> Produced by the env-gated
    /// <see cref="VelaMosaicStarListExport.ExportStarLists"/> against the archive on the
    /// user's D: drive (read-only; that archive is the only copy). Each frame's WCS is the
    /// gate-verified solution from the fixed solver, frozen at export time, so it is an
    /// ORACLE rather than a fresh output of the code under test.</para>
    /// </summary>
    internal static class VelaMosaicStarLists
    {
        internal const string ResourceName = "vela-mosaic-starlists.json";

        private static readonly Lazy<VelaMosaicManifest> Cached = new(Load, isThreadSafe: true);

        /// <summary>The embedded manifest. Parsed once per process.</summary>
        internal static VelaMosaicManifest Manifest => Cached.Value;

        /// <summary>
        /// Env override pointing at a manifest on disk, so a re-export can be evaluated
        /// against the tests without rebuilding the embedded resource first. Unset in
        /// every normal run, which reads the embedded copy.
        /// </summary>
        private const string PathOverrideVar = "TIANWEN_VELA_STARLISTS";

        private static VelaMosaicManifest Load()
        {
            using var gz = Environment.GetEnvironmentVariable(PathOverrideVar) is { Length: > 0 } path
                ? File.OpenRead(path)
                : SharedTestData.OpenEmbeddedFileStream(ResourceName + ".gz")
                    ?? throw new InvalidOperationException($"Missing embedded test data {ResourceName}.gz");
            using var raw = new GZipStream(gz, CompressionMode.Decompress, false);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var catalog = ImmutableArray.CreateBuilder<VelaCatalogStar>();
            foreach (var c in root.GetProperty("catalog").EnumerateArray())
            {
                catalog.Add(new VelaCatalogStar(c[0].GetDouble(), c[1].GetDouble(), (float)c[2].GetDouble()));
            }

            var panels = ImmutableArray.CreateBuilder<VelaPanel>();
            foreach (var p in root.GetProperty("panels").EnumerateArray())
            {
                var frames = ImmutableArray.CreateBuilder<VelaFrame>();
                foreach (var f in p.GetProperty("frames").EnumerateArray())
                {
                    var detected = ImmutableArray.CreateBuilder<Vector3>();
                    foreach (var d in f.GetProperty("detected").EnumerateArray())
                    {
                        detected.Add(new Vector3((float)d[0].GetDouble(), (float)d[1].GetDouble(), (float)d[2].GetDouble()));
                    }

                    frames.Add(new VelaFrame(
                        f.GetProperty("file").GetString() ?? "",
                        f.GetProperty("hintRA").GetDouble(),
                        f.GetProperty("hintDec").GetDouble(),
                        ReadWcs(f.GetProperty("wcs")),
                        f.GetProperty("verifyRmsPx").GetDouble(),
                        f.GetProperty("verifyMatches").GetInt32(),
                        (float)f.GetProperty("medianHfd").GetDouble(),
                        (float)f.GetProperty("medianFwhm").GetDouble(),
                        (float)f.GetProperty("medianEllipticity").GetDouble(),
                        detected.DrainToImmutable()));
                }

                panels.Add(new VelaPanel(
                    p.GetProperty("id").GetString() ?? "",
                    p.GetProperty("target").GetString() ?? "",
                    p.GetProperty("session").GetString() ?? "",
                    p.GetProperty("width").GetInt32(),
                    p.GetProperty("height").GetInt32(),
                    p.GetProperty("pixelScaleArcsec").GetDouble(),
                    frames.DrainToImmutable()));
            }

            return new VelaMosaicManifest(
                root.GetProperty("version").GetInt32(),
                root.GetProperty("note").GetString() ?? "",
                catalog.DrainToImmutable(),
                panels.DrainToImmutable());
        }

        private static WCS ReadWcs(JsonElement e)
        {
            var wcs = new WCS(e.GetProperty("crval1").GetDouble() / 15.0, e.GetProperty("crval2").GetDouble())
            {
                CRPix1 = e.GetProperty("crpix1").GetDouble(),
                CRPix2 = e.GetProperty("crpix2").GetDouble(),
                CD1_1 = e.GetProperty("cd1_1").GetDouble(),
                CD1_2 = e.GetProperty("cd1_2").GetDouble(),
                CD2_1 = e.GetProperty("cd2_1").GetDouble(),
                CD2_2 = e.GetProperty("cd2_2").GetDouble(),
            };

            // SIP is present whenever the solver's fit beat the overfit-noise floor. A
            // 5-degree square field on a fast 135 mm lens has real distortion, so the
            // linear CD alone leaves corner residuals the sub-pixel assertions would trip
            // over -- the frozen oracle keeps the polynomial the solver accepted.
            if (e.TryGetProperty("sipOrder", out var orderEl) && orderEl.GetInt32() is var order && order > 0)
            {
                wcs = wcs with
                {
                    SipOrder = order,
                    SipA = ReadCoeffs(e.GetProperty("sipA"), order),
                    SipB = ReadCoeffs(e.GetProperty("sipB"), order),
                    SipAP = ReadCoeffs(e.GetProperty("sipAP"), order),
                    SipBP = ReadCoeffs(e.GetProperty("sipBP"), order),
                };
            }

            return wcs;
        }

        /// <summary>Row-major <c>[order + 1, order + 1]</c>, matching the FITS <c>A_i_j</c> layout.</summary>
        private static double[,] ReadCoeffs(JsonElement e, int order)
        {
            var n = order + 1;
            var m = new double[n, n];
            var flat = 0;
            foreach (var v in e.EnumerateArray())
            {
                m[flat / n, flat % n] = v.GetDouble();
                flat++;
            }
            return m;
        }
    }

    internal sealed record VelaMosaicManifest(
        int Version,
        string Note,
        ImmutableArray<VelaCatalogStar> Catalog,
        ImmutableArray<VelaPanel> Panels)
    {
        internal VelaPanel Panel(string id)
        {
            foreach (var p in Panels)
            {
                if (p.Id == id)
                {
                    return p;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(id), id, "No such panel in the frozen manifest");
        }

        /// <summary>The catalog in the solver's own query shape, brightest first.</summary>
        internal List<(double RA, double Dec, double VMag)> CatalogTuples()
        {
            var list = new List<(double RA, double Dec, double VMag)>(Catalog.Length);
            foreach (var c in Catalog)
            {
                list.Add((c.RA, c.Dec, c.VMag));
            }
            return list;
        }
    }

    /// <param name="RA">J2000 RA in HOURS, proper-motion propagated to the mosaic epoch.</param>
    /// <param name="Dec">J2000 Dec in degrees, likewise propagated.</param>
    /// <param name="VMag">Johnson V; 99 where the catalog has none.</param>
    internal readonly record struct VelaCatalogStar(double RA, double Dec, float VMag);

    /// <summary>One panel-session pointing; its frames differ only by dither.</summary>
    internal sealed record VelaPanel(
        string Id,
        string Target,
        string Session,
        int Width,
        int Height,
        double PixelScaleArcsec,
        ImmutableArray<VelaFrame> Frames)
    {
        internal ImageDim Dim => new ImageDim(PixelScaleArcsec, Width, Height);

        /// <summary>Field of view of the (square) sensor in degrees.</summary>
        internal double FovDeg => PixelScaleArcsec * Width / 3600.0;
    }

    /// <param name="Detected">Detected stars, brightest first: (XCentroid, YCentroid, Flux) in
    /// 0-BASED pixel coordinates, exactly as <c>CatalogPlateSolver</c> consumes them
    /// (<c>FindStarsAsync(snrMin: 5, maxStars: 500, minStars: 50, maxRetries: 0)</c>).</param>
    /// <param name="Wcs">The frozen gate-verified solution: the oracle, not an output of the code under test.</param>
    /// <param name="VerifyRmsPx">Export-time residual RMS over mutual-nearest-neighbour catalog matches.</param>
    internal sealed record VelaFrame(
        string File,
        double HintRA,
        double HintDec,
        WCS Wcs,
        double VerifyRmsPx,
        int VerifyMatches,
        float MedianHfd,
        float MedianFwhm,
        float MedianEllipticity,
        ImmutableArray<Vector3> Detected)
    {
        internal string Name => Path.GetFileNameWithoutExtension(File);

        /// <summary>The header pointing hint, as the solver receives it from a FITS with no solution.</summary>
        internal WCS Hint => new WCS(HintRA, HintDec);

        internal Vector2[] DetectedPoints(int take = int.MaxValue)
        {
            var n = Math.Min(take, Detected.Length);
            var pts = new Vector2[n];
            for (var i = 0; i < n; i++)
            {
                pts[i] = new Vector2(Detected[i].X, Detected[i].Y);
            }
            return pts;
        }
    }

    /// <summary>
    /// Shared geometry for the frozen-field tests. These go through
    /// <see cref="WCS.SkyToPixel"/> rather than re-deriving a gnomonic projection, so the
    /// tests exercise the same projection the solver's own acceptance gate uses.
    /// </summary>
    internal static class VelaProjection
    {
        /// <summary>
        /// Builds the WCS the solver's FIRST iteration works from: an unrotated tangent
        /// plane at <paramref name="hint"/> with the nominal pixel scale and parity
        /// <paramref name="xSign"/>. Mirrors the CD construction in
        /// <c>CatalogPlateSolver.AttachCDMatrix</c> for an identity affine
        /// (<c>CD1_1 = xSign * scale</c>, <c>CD2_2 = -scale</c>), so projecting through it
        /// reproduces the solver's own <c>ProjectCatalogStars</c> to the pixel.
        /// </summary>
        internal static WCS HintWcs(WCS hint, ImageDim dim, double xSign = -1.0)
        {
            var scaleDeg = dim.PixelScale / 3600.0;
            return new WCS(hint.CenterRA, hint.CenterDec)
            {
                CRPix1 = (dim.Width + 1) / 2.0,
                CRPix2 = (dim.Height + 1) / 2.0,
                CD1_1 = xSign * scaleDeg,
                CD1_2 = 0,
                CD2_1 = 0,
                CD2_2 = -scaleDeg,
                IsApproximate = true,
            };
        }

        /// <summary>
        /// Projects the catalog through <paramref name="wcs"/> into 0-based pixel
        /// coordinates, keeping in-frame stars (plus <paramref name="marginFraction"/>
        /// overscan) in catalog order, i.e. brightest first.
        /// </summary>
        internal static Vector2[] ProjectInFrame(
            ImmutableArray<VelaCatalogStar> catalog,
            WCS wcs,
            int width,
            int height,
            double marginFraction = 0.0)
        {
            var pts = new List<Vector2>(catalog.Length / 4);
            foreach (var (x, y, _) in ProjectInFrameIndexed(catalog, wcs, width, height, marginFraction))
            {
                pts.Add(new Vector2(x, y));
            }
            return pts.ToArray();
        }

        /// <summary>
        /// As <see cref="ProjectInFrame"/> but keeping each star's CATALOG INDEX, so two
        /// panels can be compared by star identity rather than by proximity.
        /// </summary>
        internal static List<(float X, float Y, int Index)> ProjectInFrameIndexed(
            ImmutableArray<VelaCatalogStar> catalog,
            WCS wcs,
            int width,
            int height,
            double marginFraction = 0.0)
        {
            var mx = width * marginFraction;
            var my = height * marginFraction;
            var pts = new List<(float X, float Y, int Index)>(catalog.Length / 4);
            for (var i = 0; i < catalog.Length; i++)
            {
                var c = catalog[i];
                if (wcs.SkyToPixel(c.RA, c.Dec) is not { } px)
                {
                    continue;
                }

                // NO origin shift: a solver-built WCS answers directly in detected-centroid
                // coordinates (measured, see CatalogPlateSolver.CountTightMatches). Subtracting 1
                // here for a nominal 1-based FITS convention put a 1.41 px offset into every
                // projection -- which the triple-overlap test caught as a 2.82 px disagreement,
                // that offset applied once in each direction.
                var x = (float)px.X;
                var y = (float)px.Y;
                if (x >= -mx && x <= width - 1 + mx && y >= -my && y <= height - 1 + my)
                {
                    pts.Add((x, y, i));
                }
            }
            return pts;
        }

        /// <summary>
        /// The brightest <paramref name="k"/> catalog stars that project INTO the frame through its
        /// own frozen solution: a perfect hint, zero error, so whatever fails against this set fails
        /// for a reason other than pointing.
        /// </summary>
        internal static Vector2[] ProjectTopK(
            VelaMosaicManifest manifest, VelaPanel panel,
            VelaFrame frame, int k)
        {
            var all = ProjectInFrameIndexed(manifest.Catalog, frame.Wcs, panel.Width, panel.Height);
            var take = Math.Min(k, all.Count);
            var pts = new Vector2[take];
            for (var i = 0; i < take; i++)
            {
                pts[i] = new Vector2(all[i].X, all[i].Y);
            }

            return pts;
        }

        /// <summary>
        /// Builds a quad list from bare positions. <see cref="StarQuadList"/>'s ctor takes
        /// <see cref="ImagedStar"/> and reads only the two centroids, and its three-nearest-neighbour
        /// window is an INDEX range, so the input must be X-sorted (as
        /// <c>SortedStarList.FindQuadsAsync</c> does before calling it) or that search looks in the
        /// wrong part of the frame.
        /// </summary>
        internal static StarQuadList BuildQuads(Vector2[] points)
        {
            var stars = new ImagedStar[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                stars[i] = new ImagedStar(0, 0, 0, 0, points[i].X, points[i].Y, 0);
            }
            Array.Sort(stars, (a, b) => a.XCentroid.CompareTo(b.XCentroid));
            return new StarQuadList(stars.AsSpan());
        }

        /// <summary>
        /// Counts how many of <paramref name="probe"/> land within
        /// <paramref name="tolerancePx"/> of a point in <paramref name="field"/>, alongside
        /// the Poisson expectation at the field's density -- the chance model the whole
        /// dense-field hardening rests on.
        /// </summary>
        internal static (int Hits, double ExpectedByChance) CountWithin(
            ReadOnlySpan<Vector2> probe,
            ReadOnlySpan<Vector2> field,
            int width,
            int height,
            float tolerancePx)
        {
            var grid = new PairRansacLock.PointGrid(field, width, height, tolerancePx);
            var hits = 0;
            foreach (var p in probe)
            {
                if (grid.HasWithin(p.X, p.Y))
                {
                    hits++;
                }
            }

            var density = field.Length / ((double)width * height);
            return (hits, probe.Length * density * Math.PI * tolerancePx * tolerancePx);
        }

        /// <summary>
        /// Residual statistics of MUTUAL nearest-neighbour pairs between two pixel sets
        /// within <paramref name="tolerancePx"/>. Mutual matching is what keeps a dense
        /// list from letting several of its points claim one point of the other.
        /// </summary>
        internal static (int Matches, double RmsPx, double MedianPx) MutualMatchStats(
            ReadOnlySpan<Vector2> a,
            ReadOnlySpan<Vector2> b,
            int width,
            int height,
            float tolerancePx)
        {
            var aGrid = new PairRansacLock.PointGrid(a, width, height, tolerancePx);
            var bGrid = new PairRansacLock.PointGrid(b, width, height, tolerancePx);

            var residuals = new List<double>(Math.Min(a.Length, b.Length));
            double sumSq = 0;
            foreach (var p in a)
            {
                if (!bGrid.TryNearest(p.X, p.Y, out var q))
                {
                    continue;
                }
                if (!aGrid.TryNearest(q.X, q.Y, out var back) || Vector2.DistanceSquared(back, p) > 1e-6f)
                {
                    continue;
                }
                var dSq = Vector2.DistanceSquared(p, q);
                sumSq += dSq;
                residuals.Add(Math.Sqrt(dSq));
            }

            if (residuals.Count == 0)
            {
                return (0, double.NaN, double.NaN);
            }

            residuals.Sort();
            return (residuals.Count, Math.Sqrt(sumSq / residuals.Count), residuals[residuals.Count / 2]);
        }
    }
}
