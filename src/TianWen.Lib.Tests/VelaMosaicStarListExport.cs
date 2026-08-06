using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// PRODUCER for <see cref="VelaMosaicStarLists"/>: turns a set of real light frames
    /// into the frozen star-list manifest the regression tests consume. Env-gated and
    /// normally skipped -- it needs the user's archive, which lives on one disk and is
    /// the only copy of the data.
    ///
    /// <para><b>Read-only by construction.</b> Every input is opened for reading and the
    /// single output goes to a path the caller names, so a mis-run cannot touch the
    /// archive. Do not add a write path against the source frames here.</para>
    ///
    /// <para>Run it as:
    /// <code>
    /// TIANWEN_EXPORT_STARLISTS=&lt;frames.tsv&gt; \
    /// TIANWEN_EXPORT_STARLISTS_OUT=&lt;out.json.gz&gt; \
    /// dotnet test TianWen.Lib.Tests --filter FullyQualifiedName~ExportStarLists
    /// </code>
    /// where each TSV line is <c>panelId\tabsolute\path\to\frame.fits</c>. Frames of the
    /// same panel id must share a pointing (dither only).</para>
    /// </summary>
    [Collection("Astrometry")]
    public class VelaMosaicStarListExport(ITestOutputHelper output)
    {
        /// <summary>
        /// Catalog margin beyond the outermost panel centre, in degrees, on top of a
        /// panel's own half-diagonal. The wrong-origin regressions project the catalog
        /// through a DIFFERENT tangent point, so stars outside every true footprint move
        /// IN; without the margin those tests would see a thinned field and the chance
        /// rate they assert against would be wrong.
        /// </summary>
        private const double CatalogMarginDeg = 1.5;

        /// <summary>Mutual-nearest-neighbour window for the frozen quality figure, in pixels.</summary>
        private const float VerifyTolerancePx = 2f;

        /// <summary>
        /// A frame only enters the oracle if its own solution verifies this well. Good
        /// frames in this archive land at ~0.47 px RMS over ~1,200 mutual matches; the
        /// frames this rejects (Vela panel 4's two, panel 7's one) sit at ~2.0 px over
        /// 25-75 matches and disagree with EACH OTHER by 2.2 px, so their solutions are
        /// not trustworthy enough to assert against. The gate that let them through is
        /// working as designed -- it refuses noise, and these are weak-but-real locks --
        /// but an oracle needs a higher bar than "better than chance".
        /// </summary>
        private const double MaxOracleRmsPx = 1.0;

        private const int MinOracleMatches = 300;

        private readonly record struct SolvedFrame(
            string PanelId,
            string Path,
            string Session,
            string Target,
            ImageDim Dim,
            WCS Hint,
            WCS Wcs,
            DateTimeOffset Epoch,
            Vector3[] Detected,
            float MedianHfd,
            float MedianFwhm,
            float MedianEllipticity);

        [Fact]
        public async Task ExportStarLists()
        {
            if (Environment.GetEnvironmentVariable("TIANWEN_EXPORT_STARLISTS") is not { Length: > 0 } tsvPath || !File.Exists(tsvPath))
            {
                Assert.Skip("Set TIANWEN_EXPORT_STARLISTS to a TSV of 'panelId<TAB>fitsPath' lines to export frozen star lists.");
                return;
            }

            if (Environment.GetEnvironmentVariable("TIANWEN_EXPORT_STARLISTS_OUT") is not { Length: > 0 } outPath)
            {
                Assert.Skip("Set TIANWEN_EXPORT_STARLISTS_OUT to the manifest path to write (.json.gz).");
                return;
            }

            var cancellationToken = TestContext.Current.CancellationToken;
            var db = await SharedCatalogDB.InitAsync(cancellationToken);
            var solver = new CatalogPlateSolver(db, NullLogger.Instance);

            // Preserve TSV order so panels come out in mosaic order.
            var order = new List<string>();
            var byPanel = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var line in await File.ReadAllLinesAsync(tsvPath, cancellationToken))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0)
                {
                    continue;
                }
                var id = line[..tab];
                if (!byPanel.TryGetValue(id, out var frames))
                {
                    byPanel[id] = frames = new List<string>();
                    order.Add(id);
                }
                frames.Add(line[(tab + 1)..]);
            }

            output.WriteLine($"{order.Count} panels from {tsvPath}");

            // Pass 1: solve every frame. Images are NOT retained -- 96 x 3008x3008 float
            // would be several GB; only the star list and the solution are needed.
            var solved = new List<SolvedFrame>();
            var swAll = Stopwatch.StartNew();
            foreach (var panelId in order)
            {
                foreach (var framePath in byPanel[panelId])
                {
                    if (await SolveFrameAsync(solver, panelId, framePath, cancellationToken) is { } frame)
                    {
                        solved.Add(frame);
                        output.WriteLine($"  {panelId}/{Path.GetFileName(framePath)}: {frame.Detected.Length} detected, " +
                            $"RA={frame.Wcs.CenterRA:F5}h Dec={frame.Wcs.CenterDec:F4}° " +
                            $"(hint {frame.Hint.CenterRA:F5}h {frame.Hint.CenterDec:F4}°, " +
                            $"off by {Separation(frame.Hint, frame.Wcs) * 60:F1}')");
                    }
                }
            }

            solved.Count.ShouldBeGreaterThan(0, "no frame solved -- nothing to freeze");
            output.WriteLine($"Solved {solved.Count} frames in {swAll.Elapsed.TotalSeconds:F0} s");

            // ONE catalog for the whole mosaic. The panels are 5-degree squares spaced
            // ~1.5 degrees apart, so per-panel catalogs would re-store the same stars a
            // dozen times over; a shared list is ~13x smaller AND gives cross-panel star
            // IDENTITY (index i is the same physical star in every panel), which is what
            // makes the overlap tests exact instead of nearest-neighbour approximations.
            var (centre, radiusDeg) = MosaicFootprint(solved);

            // Proper motion: the frames span ~2 months, so one epoch for the whole
            // mosaic is exact to ~0.003 px (100 mas/yr over 0.2 yr at 6"/px).
            var epochs = new List<DateTimeOffset>(solved.Count);
            foreach (var f in solved)
            {
                epochs.Add(f.Epoch);
            }
            epochs.Sort();
            var epoch = epochs[epochs.Count / 2];
            var dtYr = epoch.Year > 1900 ? epoch.JulianYearsSinceJ2000() : 0.0;

            var swCat = Stopwatch.StartNew();
            var catalog = solver.QueryCatalogStarsInRegion(centre, radiusDeg, dtYr);
            catalog.Sort(static (a, b) => a.VMag.CompareTo(b.VMag));
            output.WriteLine($"Catalog: {catalog.Count} stars within {radiusDeg:F2}° of " +
                $"RA={centre.CenterRA:F4}h Dec={centre.CenterDec:F3}° at epoch {epoch:yyyy-MM-dd} " +
                $"({swCat.Elapsed.TotalSeconds:F1} s)");

            using var outStream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(outStream, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 2);
                writer.WriteString("note",
                    "Frozen star lists from the Vela SNR mosaic (ASI533MC Pro / Samyang 135, 3008x3008, ~6\"/px, " +
                    "5-degree square field). catalog = ONE mosaic-wide list of [raHours, decDeg, vmag], brightest-first, " +
                    "proper-motion propagated to the median frame epoch -- index i is the same physical star for every panel. " +
                    "detected = [x, y, flux] 0-based, brightest-first, from FindStarsAsync(snrMin 5, maxStars 500, " +
                    "minStars 50, maxRetries 0). wcs = the gate-verified solution incl. SIP; crval1 is RA in DEGREES.");
                writer.WriteString("epoch", epoch.UtcDateTime.ToString("O"));
                writer.WriteNumber("catalogCentreRA", Round(centre.CenterRA, 7));
                writer.WriteNumber("catalogCentreDec", Round(centre.CenterDec, 6));
                writer.WriteNumber("catalogRadiusDeg", Round(radiusDeg, 4));

                writer.WritePropertyName("catalog");
                writer.WriteStartArray();
                foreach (var (ra, dec, vmag) in catalog)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Round(ra, 7));      // hours: 1e-7 h = 1.4 mas
                    writer.WriteNumberValue(Round(dec, 6));     // deg:   1e-6 deg = 3.6 mas
                    writer.WriteNumberValue(Round(vmag, 2));
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();

                writer.WritePropertyName("panels");
                writer.WriteStartArray();

                // Quality-gate every solved frame against the frozen catalog BEFORE
                // writing, so a weak solution never becomes an oracle other tests trust.
                var quality = new Dictionary<string, (double Rms, int Matches)>(StringComparer.Ordinal);
                foreach (var f in solved)
                {
                    quality[f.Path] = VerifyAgainstCatalog(f.Wcs, catalog, f.Detected, f.Dim);
                }

                var totalDetected = 0;
                var dropped = 0;
                foreach (var panelId in order)
                {
                    var frames = solved.FindAll(f => f.PanelId == panelId
                        && quality[f.Path] is { Rms: <= MaxOracleRmsPx, Matches: >= MinOracleMatches });

                    foreach (var f in solved)
                    {
                        if (f.PanelId == panelId && quality[f.Path] is var q
                            && (q.Rms > MaxOracleRmsPx || q.Matches < MinOracleMatches))
                        {
                            dropped++;
                            output.WriteLine($"  {panelId}/{Path.GetFileName(f.Path)}: DROPPED from the oracle -- " +
                                $"rms {q.Rms:F3} px over {q.Matches} mutual matches " +
                                $"(needs <= {MaxOracleRmsPx:F1} px over >= {MinOracleMatches}); " +
                                $"shape: HFD {f.MedianHfd:F2} px, FWHM {f.MedianFwhm:F2} px, ellipticity {f.MedianEllipticity:F3}");
                        }
                    }

                    if (frames.Count == 0)
                    {
                        output.WriteLine($"  {panelId}: no frame met the oracle bar, panel omitted");
                        continue;
                    }

                    var first = frames[0];
                    writer.WriteStartObject();
                    writer.WriteString("id", panelId);
                    writer.WriteString("target", first.Target);
                    writer.WriteString("session", first.Session);
                    writer.WriteNumber("width", first.Dim.Width);
                    writer.WriteNumber("height", first.Dim.Height);
                    writer.WriteNumber("pixelScaleArcsec", Round(first.Dim.PixelScale, 4));

                    writer.WritePropertyName("frames");
                    writer.WriteStartArray();
                    foreach (var f in frames)
                    {
                        var (rms, matches) = quality[f.Path];

                        writer.WriteStartObject();
                        writer.WriteString("file", Path.GetFileName(f.Path));
                        writer.WriteNumber("hintRA", Round(f.Hint.CenterRA, 7));
                        writer.WriteNumber("hintDec", Round(f.Hint.CenterDec, 6));
                        writer.WriteNumber("verifyRmsPx", Round(rms, 4));
                        writer.WriteNumber("verifyMatches", matches);
                        writer.WriteNumber("medianHfd", Round(f.MedianHfd, 3));
                        writer.WriteNumber("medianFwhm", Round(f.MedianFwhm, 3));
                        writer.WriteNumber("medianEllipticity", Round(f.MedianEllipticity, 4));

                        writer.WritePropertyName("wcs");
                        writer.WriteStartObject();
                        writer.WriteNumber("crval1", Round(f.Wcs.CenterRA * 15.0, 8));
                        writer.WriteNumber("crval2", Round(f.Wcs.CenterDec, 8));
                        writer.WriteNumber("crpix1", Round(f.Wcs.CRPix1, 3));
                        writer.WriteNumber("crpix2", Round(f.Wcs.CRPix2, 3));
                        writer.WriteNumber("cd1_1", f.Wcs.CD1_1);
                        writer.WriteNumber("cd1_2", f.Wcs.CD1_2);
                        writer.WriteNumber("cd2_1", f.Wcs.CD2_1);
                        writer.WriteNumber("cd2_2", f.Wcs.CD2_2);
                        if (f.Wcs.HasSip && f.Wcs.HasInverseSip && f.Wcs.SipA is { } sipA && f.Wcs.SipB is { } sipB
                            && f.Wcs.SipAP is { } sipAP && f.Wcs.SipBP is { } sipBP)
                        {
                            writer.WriteNumber("sipOrder", f.Wcs.SipOrder);
                            WriteCoeffs(writer, "sipA", sipA);
                            WriteCoeffs(writer, "sipB", sipB);
                            WriteCoeffs(writer, "sipAP", sipAP);
                            WriteCoeffs(writer, "sipBP", sipBP);
                        }
                        writer.WriteEndObject();

                        writer.WritePropertyName("detected");
                        writer.WriteStartArray();
                        foreach (var s in f.Detected)
                        {
                            writer.WriteStartArray();
                            writer.WriteNumberValue(Round(s.X, 2));   // 0.01 px = 0.06" -- well under centroid noise
                            writer.WriteNumberValue(Round(s.Y, 2));
                            writer.WriteNumberValue(Math.Round(s.Z));  // integrated ADU; the fraction is meaningless
                            writer.WriteEndArray();
                        }
                        writer.WriteEndArray();
                        writer.WriteEndObject();

                        totalDetected += f.Detected.Length;
                        output.WriteLine($"  {panelId}/{Path.GetFileName(f.Path)}: rms {rms:F3} px " +
                            $"({rms * f.Dim.PixelScale:F2}\") over {matches} mutual matches" +
                            (f.Wcs.HasSip ? $", SIP order {f.Wcs.SipOrder}" : ", LINEAR only") +
                            $"; shape: HFD {f.MedianHfd:F2} px, FWHM {f.MedianFwhm:F2} px, ellipticity {f.MedianEllipticity:F3}");
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();

                output.WriteLine($"TOTAL: {solved.Count - dropped} frames frozen ({dropped} dropped below the oracle bar), " +
                    $"{catalog.Count} catalog stars, {totalDetected} detected stars, " +
                    $"{outStream.Length / 1024.0 / 1024.0:F2} MiB raw JSON");
            }

            // Deflate at max: this lands in the repo as embedded test data.
            outStream.Position = 0;
            await using (var file = File.Create(outPath))
            await using (var gz = new GZipStream(file, CompressionLevel.SmallestSize))
            {
                await outStream.CopyToAsync(gz, cancellationToken);
            }

            var gzLength = new FileInfo(outPath).Length;
            output.WriteLine($"Wrote {outPath}: {gzLength / 1024.0 / 1024.0:F2} MiB gzipped");
            gzLength.ShouldBeGreaterThan(0);
        }

        private async Task<SolvedFrame?> SolveFrameAsync(
            CatalogPlateSolver solver,
            string panelId,
            string framePath,
            System.Threading.CancellationToken cancellationToken)
        {
            if (!File.Exists(framePath))
            {
                output.WriteLine($"  {panelId}: MISSING {framePath}");
                return null;
            }

            if (!Image.TryReadFitsFile(framePath, out var image, out var fileWcs) || fileWcs is not { } hint)
            {
                output.WriteLine($"  {panelId}: unreadable or no RA/DEC header -- {Path.GetFileName(framePath)}");
                return null;
            }

            if (image.GetImageDim() is not { } dim)
            {
                output.WriteLine($"  {panelId}: no FOCALLEN/XPIXSZ -- {Path.GetFileName(framePath)}");
                return null;
            }

            // Solve from the HEADER hint, exactly as production does for an unsolved sub.
            // A frame whose solution the acceptance gate refuses is dropped rather than
            // frozen: the manifest is an oracle, so a doubtful WCS in it would poison
            // every test that trusts it.
            // Detect BEFORE solving so a frame that fails to solve still reports its star
            // shape -- that is the evidence for whether a shape-based pre-pass rejector
            // would have caught it. (The solver detects with these same arguments, and
            // Image caches its star list, so this costs nothing.)
            var stars = await image.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0, cancellationToken: cancellationToken);
            var hfd = stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
            var fwhm = stars.MapReduceStarProperty(SampleKind.FWHM, AggregationMethod.Median);
            var ellipticity = stars.MapReduceStarProperty(SampleKind.Ellipticity, AggregationMethod.Median);
            var shape = $"{stars.Count} stars, HFD {hfd:F2} px, FWHM {fwhm:F2} px, ellipticity {ellipticity:F3}";

            // NO searchRadius override. It sizes the CATALOG QUERY, not the pointing
            // uncertainty, and the solver's default (0.75 x FOV) is what production uses.
            // Passing 1.5 deg here truncated the query to the middle of a 5-degree square
            // field (half-diagonal 3.54 deg), so the projected catalog covered a fraction
            // of the frame and the gate correctly refused the resulting weak lock -- which
            // read as "panel 19 will not solve" when the CLI solves it at 0.39 px.
            var result = await solver.SolveImageAsync(image, dim, searchOrigin: hint, cancellationToken: cancellationToken);
            if (result.Solution is not { } wcs || !wcs.HasCDMatrix)
            {
                output.WriteLine($"  {panelId}: NOT SOLVED (gate rejected or no lock) -- {Path.GetFileName(framePath)}; {shape}");
                return null;
            }

            var detected = new Vector3[stars.Count];
            var i = 0;
            foreach (var s in stars)
            {
                detected[i++] = new Vector3(s.XCentroid, s.YCentroid, s.Flux);
            }

            // Brightest first, matching what the solver's ranked list gives PairRansacLock.
            Array.Sort(detected, static (a, b) => b.Z.CompareTo(a.Z));

            return new SolvedFrame(
                panelId,
                framePath,
                SessionOf(framePath),
                image.ImageMeta.ObjectName ?? "",
                dim,
                hint,
                wcs,
                image.ImageMeta.ExposureStartTime,
                detected,
                hfd,
                fwhm,
                ellipticity);
        }

        /// <summary>
        /// Tangent point and radius covering every panel: the mean pointing, plus the
        /// furthest panel centre, plus a panel's own half-diagonal (the sensor is square,
        /// so that is <c>fov * sqrt(2) / 2</c>), plus <see cref="CatalogMarginDeg"/>.
        /// </summary>
        private static (WCS Centre, double RadiusDeg) MosaicFootprint(List<SolvedFrame> solved)
        {
            // Mean over unit vectors, so an RA wrap or a high Dec cannot skew it.
            double sx = 0, sy = 0, sz = 0;
            foreach (var f in solved)
            {
                var ra = f.Wcs.CenterRA * (Math.PI / 12.0);
                var dec = double.DegreesToRadians(f.Wcs.CenterDec);
                var (sinDec, cosDec) = Math.SinCos(dec);
                var (sinRa, cosRa) = Math.SinCos(ra);
                sx += cosDec * cosRa;
                sy += cosDec * sinRa;
                sz += sinDec;
            }
            var norm = Math.Sqrt(sx * sx + sy * sy + sz * sz);
            var centreRA = Math.Atan2(sy, sx) * (12.0 / Math.PI);
            if (centreRA < 0)
            {
                centreRA += 24.0;
            }
            var centre = new WCS(centreRA, double.RadiansToDegrees(Math.Asin(sz / norm)));

            var maxSep = 0.0;
            var maxHalfDiagonal = 0.0;
            foreach (var f in solved)
            {
                maxSep = Math.Max(maxSep, Separation(centre, f.Wcs));
                var fov = f.Dim.FieldOfView;
                maxHalfDiagonal = Math.Max(maxHalfDiagonal, Math.Sqrt(fov.width * fov.width + fov.height * fov.height) / 2.0);
            }

            return (centre, maxSep + maxHalfDiagonal + CatalogMarginDeg);
        }

        /// <summary>Great-circle separation between two pointings, in degrees.</summary>
        private static double Separation(WCS a, WCS b)
        {
            var ra1 = a.CenterRA * (Math.PI / 12.0);
            var ra2 = b.CenterRA * (Math.PI / 12.0);
            var (sinD1, cosD1) = Math.SinCos(double.DegreesToRadians(a.CenterDec));
            var (sinD2, cosD2) = Math.SinCos(double.DegreesToRadians(b.CenterDec));
            var cos = sinD1 * sinD2 + cosD1 * cosD2 * Math.Cos(ra1 - ra2);
            return double.RadiansToDegrees(Math.Acos(Math.Clamp(cos, -1.0, 1.0)));
        }

        /// <summary>
        /// Residual RMS over MUTUAL nearest-neighbour catalog-to-detected pairs under
        /// <paramref name="wcs"/>. Mutual is load-bearing: with ~10,000 catalog stars
        /// projecting into frame against ~1,600 detected, a one-sided nearest-neighbour
        /// lets several catalog stars claim the same bright detection, and those spurious
        /// claims sit anywhere inside the tolerance -- the first version of this figure
        /// read 1.37 px for a solution whose real residual is ~0.35 px.
        /// </summary>
        private static (double Rms, int Matches) VerifyAgainstCatalog(
            WCS wcs,
            List<(double RA, double Dec, double VMag)> catalog,
            Vector3[] detected,
            ImageDim dim)
        {
            var field = new Vector2[detected.Length];
            for (var i = 0; i < detected.Length; i++)
            {
                field[i] = new Vector2(detected[i].X, detected[i].Y);
            }

            // Project the catalog into frame, then run nearest-neighbour both ways.
            // No origin shift: a solver-built WCS answers in detected-centroid
            // coordinates (see the note in CatalogPlateSolver.CountTightMatches).
            var projected = new List<Vector2>(catalog.Count / 4);
            foreach (var (ra, dec, _) in catalog)
            {
                if (wcs.SkyToPixel(ra, dec) is not { } px)
                {
                    continue;
                }
                var x = (float)px.X;
                var y = (float)px.Y;
                if (x >= 0 && x <= dim.Width - 1 && y >= 0 && y <= dim.Height - 1)
                {
                    projected.Add(new Vector2(x, y));
                }
            }

            var catGrid = new PairRansacLock.PointGrid(projected.ToArray(), dim.Width, dim.Height, VerifyTolerancePx);
            var detGrid = new PairRansacLock.PointGrid(field, dim.Width, dim.Height, VerifyTolerancePx);

            double sumSq = 0;
            var matches = 0;
            foreach (var p in projected)
            {
                if (!detGrid.TryNearest(p.X, p.Y, out var det))
                {
                    continue;
                }
                // Mutual check: that detection's nearest projected star must be p itself.
                if (!catGrid.TryNearest(det.X, det.Y, out var back) || Vector2.DistanceSquared(back, p) > 1e-6f)
                {
                    continue;
                }
                sumSq += Vector2.DistanceSquared(det, p);
                matches++;
            }

            return (matches > 0 ? Math.Sqrt(sumSq / matches) : double.NaN, matches);
        }

        /// <summary>Flattens a SIP coefficient matrix row-major; full precision (they are tiny numbers whose
        /// effect is a pixel-scale correction, so rounding them would defeat the point of freezing them).</summary>
        private static void WriteCoeffs(Utf8JsonWriter writer, string name, double[,] coeffs)
        {
            writer.WritePropertyName(name);
            writer.WriteStartArray();
            for (var i = 0; i < coeffs.GetLength(0); i++)
            {
                for (var j = 0; j < coeffs.GetLength(1); j++)
                {
                    writer.WriteNumberValue(coeffs[i, j]);
                }
            }
            writer.WriteEndArray();
        }

        /// <summary>The archive's session folder, i.e. the directory holding the LIGHT folder.</summary>
        private static string SessionOf(string framePath)
        {
            var light = Path.GetDirectoryName(framePath);
            var session = Path.GetDirectoryName(light);
            return session is null ? "" : Path.GetFileName(session);
        }

        private static double Round(double value, int digits) =>
            double.IsFinite(value) ? Math.Round(value, digits, MidpointRounding.ToEven) : 0.0;
    }
}
