using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using TianWen.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Would the pair lock's own anchor pool verify the TRUE transform?
    /// </summary>
    /// <remarks>
    /// <para>A lock that reports "no seed" has two completely different causes and says the same
    /// thing about both: the hypothesis space does not CONTAIN a correct pairing, or it contains one
    /// the scan failed to find. <see cref="FrameWcsAgreementProbe"/> answers a question one level
    /// further out -- do the pixels agree with the catalogue at all -- and can pass while this one
    /// fails, because the lock does not match against the frame, it matches against a projected
    /// ANCHOR POOL that is a small, brightness-truncated, hint-dependent subset of it.</para>
    /// <para>So this builds the pool the seed actually uses, via the production projection rather
    /// than a copy of it, then constructs the transform the lock is SUPPOSED to find -- from the
    /// pool's own stars to where the frame's WCS says they are -- and counts hits under it. If the
    /// truth verifies, the pool is fine and the scan is at fault. If the truth does not verify,
    /// searching harder cannot help, and neither can refining: there is nothing to refine onto.</para>
    /// <para>Gated: <c>TIANWEN_SEED_POOL_FITS</c> must name a FITS that already carries a CD matrix,
    /// which is what supplies the truth.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class SeedAnchorPoolProbe(ITestOutputHelper output)
    {
        [Fact]
        public async Task ReportWhetherTheTrueTransformVerifiesAgainstTheSeedPool()
        {
            var path = Environment.GetEnvironmentVariable("TIANWEN_SEED_POOL_FITS");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(path), "Set TIANWEN_SEED_POOL_FITS to a solved FITS");

            var ct = TestContext.Current.CancellationToken;
            Image.TryReadFitsFile(path!, out var image, out var fileWcs);
            Assert.NotNull(image);
            Assert.NotNull(fileWcs);
            // A frame's OWN CD matrix is not automatically the truth -- ldn1089_n2n.fits carries one
            // that misses its own stars by ~74 px. Prefer a solver's .wcs sidecar when there is one,
            // and say which was used, because every number below is measured against it.
            var truth = fileWcs!.Value;
            var truthSource = "the frame's own CD matrix";
            var sidecar = System.IO.Path.ChangeExtension(path!, ".wcs");
            if (System.IO.File.Exists(sidecar))
            {
                using var wcsFits = new nom.tam.fits.Fits(sidecar);
                if (WCS.FromFits(wcsFits) is { HasCDMatrix: true } solved)
                {
                    truth = solved;
                    truthSource = "the .wcs sidecar";
                }
            }
            Assert.True(truth.HasCDMatrix, "the probe needs a CD matrix as its ground truth");
            output.WriteLine($"truth from {truthSource}: RA={truth.CenterRA:F5}h Dec={truth.CenterDec:F4} scale={truth.PixelScaleArcsec:F4}\"/px");

            // Exactly the solver's own detection path, downsample included -- the anchor ranks
            // depend on it, and a probe that detects differently is measuring a different pool.
            var dim = image!.GetImageDim(truth)!.Value;
            var detectionImage = image;
            var detectionScale = 1;
            var pixelScaleX10 = (int)Math.Round(dim.PixelScale * 10);
            if (pixelScaleX10 > 0 && pixelScaleX10 < 15)
            {
                detectionScale = (15 + pixelScaleX10 - 1) / pixelScaleX10;
                if (detectionScale > 1)
                {
                    detectionImage = image.Downsample(detectionScale);
                }
            }
            var detected = (await detectionImage.FindStarsAsync(
                detectionImage.ReferenceStarChannel, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0,
                maxFirstPassNoiseSigma: Image.MaxFirstPassNoiseSigma, cancellationToken: ct)).ToList();
            var halfBlock = detectionScale / 2.0f - 0.5f;
            var detPts = detected
                .OrderByDescending(s => s.Flux)
                .Select(s => new Vector2(
                    s.XCentroid * detectionScale + halfBlock,
                    s.YCentroid * detectionScale + halfBlock))
                .ToArray();

            output.WriteLine($"{System.IO.Path.GetFileName(path)}: {image.Width}x{image.Height}, " +
                $"{dim.PixelScale:F4}\"/px, detection bin {detectionScale}x, {detPts.Length} detections");

            // Control: detections made at full resolution, so the downsample step can be ablated.
            Vector2[] fullResPts = [];
            if (detectionScale > 1)
            {
                fullResPts = (await image.FindStarsAsync(
                    image.ReferenceStarChannel, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0,
                    maxFirstPassNoiseSigma: Image.MaxFirstPassNoiseSigma, cancellationToken: ct))
                    .Select(s => new Vector2(s.XCentroid, s.YCentroid))
                    .ToArray();
            }

            var db2 = await SharedCatalogDB.InitAsync(ct);
            var solver = new CatalogPlateSolver(db2, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            var origin = new WCS(truth.CenterRA, truth.CenterDec);
            var catalogCoords = solver.QueryCatalogStarsInRegion(origin, 2.0, 0.0);
            var pixelScaleRad = double.DegreesToRadians(dim.PixelScale / 3600.0);
            var cx = image.Width / 2.0;
            var cy = image.Height / 2.0;
            output.WriteLine($"catalog in search region: {catalogCoords.Count}");

            foreach (var xSign in new[] { 1.0, -1.0 })
            {
                foreach (var margin in new[] { 0f, 0.1f })
                {
                    var pool = CatalogPlateSolver.ProjectCatalogStars(
                        catalogCoords, origin, pixelScaleRad, cx, cy, dim, xSign, margin);
                    var anchors = pool.Take(160).ToArray();
                    if (anchors.Length < 8)
                    {
                        output.WriteLine($"xSign={xSign:+0;-0} margin={margin:P0}: only {anchors.Length} anchors");
                        continue;
                    }

                    // The transform the lock is supposed to find: anchor as PROJECTED (what the
                    // pool holds) onto anchor as the frame's WCS PLACES it (where a detection of
                    // it must be). Both sides are the same stars, so this is the truth by
                    // construction -- no fitting to detections, nothing centred on the answer.
                    var src = new List<Vector2>(anchors.Length);
                    var dst = new List<Vector2>(anchors.Length);
                    // catalogCoords carries RA in HOURS (QueryCatalogStarsInRegion), which is
                    // what SkyToPixel wants -- a /15 here reads as a 1000 px projection residual.
                    foreach (var a in anchors)
                    {
                        if (truth.SkyToPixel(a.RA, a.Dec) is { } px)
                        {
                            src.Add(new Vector2(a.Pixel.XCentroid, a.Pixel.YCentroid));
                            dst.Add(new Vector2((float)px.X, (float)px.Y));
                        }
                    }
                    if (Matrix3x2.FitSimilarityTransform(src.ToArray(), dst.ToArray()) is not { } trueM)
                    {
                        output.WriteLine($"xSign={xSign:+0;-0} margin={margin:P0}: truth not fittable");
                        continue;
                    }

                    var scale = MathF.Sqrt(trueM.M11 * trueM.M22 - trueM.M12 * trueM.M21);
                    var rotDeg = MathF.Atan2(trueM.M12, trueM.M11) * 180f / MathF.PI;
                    var resid = src.Select((s, i) => Vector2.Distance(Vector2.Transform(s, trueM), dst[i])).ToArray();

                    var grid4 = new PairRansacLock.PointGrid(detPts, image.Width, image.Height, 4f);
                    var grid12 = new PairRansacLock.PointGrid(detPts, image.Width, image.Height, 12f);
                    int h4 = 0, h12 = 0;
                    foreach (var s in src)
                    {
                        var t = Vector2.Transform(s, trueM);
                        if (grid4.HasWithin(t.X, t.Y)) { h4++; }
                        if (grid12.HasWithin(t.X, t.Y)) { h12++; }
                    }

                    output.WriteLine(
                        $"xSign={xSign:+0;-0} margin={margin:P0}: {anchors.Length} anchors, " +
                        $"truth is scale {scale:F4} rot {rotDeg:F2} deg, " +
                        $"projection residual median {resid.Order().ElementAt(resid.Length / 2):F2} px / max {resid.Max():F2} px");
                    output.WriteLine(
                        $"    the TRUE transform scores {h4}/{src.Count} at 4 px, {h12}/{src.Count} at 12 px " +
                        $"-- accept threshold is 24");

                    if (resid.Max() > 1f)
                    {
                        continue;   // wrong parity; the breakdown below would be meaningless
                    }

                    // WHERE in the pool the matchable stars are. The lock takes a brightness
                    // PREFIX, so a pool that matches well overall but not at its bright end is a
                    // pool the lock cannot use, and the two look identical from the outside.
                    // ProjectedCatalogStar drops VMag, but the pool preserves catalogCoords'
                    // brightest-first order, so rank IS brightness.
                    var whole = pool
                        .Select(a => new Vector2(a.Pixel.XCentroid, a.Pixel.YCentroid))
                        .ToArray();
                    foreach (var prefix in new[] { 20, 50, 160, 500, whole.Length })
                    {
                        var n = Math.Min(prefix, whole.Length);
                        var hit = 0;
                        for (var i = 0; i < n; i++)
                        {
                            var t = Vector2.Transform(whole[i], trueM);
                            if (grid4.HasWithin(t.X, t.Y)) { hit++; }
                        }
                        output.WriteLine($"      brightest {n,5}: {hit,5} match at 4 px ({(double)hit / n:P1})");
                    }

                    // What a ROTATION-INVARIANT pool would score. The box test keeps stars whose
                    // north-up projection is in frame; under an unknown field rotation about the
                    // tangent point, the only region guaranteed to STAY in frame is the inscribed
                    // disc. A subset of the box, so it can be measured without changing anything.
                    var safeR = Math.Min(image.Width, image.Height) / 2.0;
                    var disc = whole
                        .Where(p => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) <= safeR * safeR)
                        .Take(160)
                        .ToArray();
                    var discHits = disc.Count(p =>
                    {
                        var t = Vector2.Transform(p, trueM);
                        return grid4.HasWithin(t.X, t.Y);
                    });
                    output.WriteLine($"      ROTATION-INVARIANT disc pool: {discHits}/{disc.Length} match at 4 px " +
                        $"({(disc.Length > 0 ? (double)discHits / disc.Length : 0):P1}) -- threshold 24");

                    // Is the pool even ORDERED by brightness? TrySeedPairLock documents that it is.
                    var mags = catalogCoords.Select(c => c.VMag).ToArray();
                    var sortedMags = mags.Order().ToArray();
                    output.WriteLine($"      catalogCoords VMag order: first 8 = " +
                        $"{string.Join(", ", mags.Take(8).Select(v => v.ToString("F2")))}" +
                        $"  (sorted would be {string.Join(", ", sortedMags.Take(8).Select(v => v.ToString("F2")))})");

                    // Are the pool's coordinates the ones Tycho-2 holds? FrameWcsAgreementProbe
                    // matches raw Tycho2StarLite against this same frame and finds most of them,
                    // so a pool that does NOT agree with Tycho-2 is a pool in a different epoch,
                    // frame or unit -- and the projection cannot tell, because it uses the same
                    // numbers on both sides of the fit.
                    var tycho = new Tycho2StarLite[db2.Tycho2StarCount];
                    var tychoN = db2.CopyTycho2Stars(tycho);
                    var offsets = new List<double>();
                    foreach (var a in anchors.Take(60))
                    {
                        var best = double.MaxValue;
                        for (var i = 0; i < tychoN; i++)
                        {
                            var dRa = (tycho[i].RaHours - a.RA) * 15.0 * Math.Cos(double.DegreesToRadians(a.Dec));
                            var dDec = tycho[i].DecDeg - a.Dec;
                            var d = Math.Sqrt(dRa * dRa + dDec * dDec) * 3600.0;
                            if (d < best) { best = d; }
                        }
                        offsets.Add(best);
                    }
                    offsets.Sort();
                    output.WriteLine($"      anchor -> nearest Tycho-2 star: p10 {offsets[6]:F2}\", " +
                        $"p50 {offsets[30]:F2}\", p90 {offsets[54]:F2}\" " +
                        $"({offsets.Count(o => o < 2.0)}/60 within 2\")");

                    // For anchors the truth places INSIDE the frame, how far is the nearest
                    // detection? Near zero means the pool is fine and something else is wrong;
                    // tens of pixels means the pool's coordinates do not describe these pixels.
                    var inFrameNearest = new List<double>();
                    for (var i = 0; i < dst.Count; i++)
                    {
                        if (dst[i].X < 0 || dst[i].Y < 0 || dst[i].X >= image.Width || dst[i].Y >= image.Height)
                        {
                            continue;
                        }
                        var best = double.MaxValue;
                        foreach (var d in detPts)
                        {
                            var dd = Vector2.Distance(d, dst[i]);
                            if (dd < best) { best = dd; }
                        }
                        inFrameNearest.Add(best);
                    }
                    inFrameNearest.Sort();
                    if (inFrameNearest.Count > 0)
                    {
                        output.WriteLine($"      in-frame anchors, nearest detection: " +
                            $"p10 {inFrameNearest[inFrameNearest.Count / 10]:F1} px, " +
                            $"p50 {inFrameNearest[inFrameNearest.Count / 2]:F1} px, " +
                            $"p90 {inFrameNearest[inFrameNearest.Count * 9 / 10]:F1} px");
                    }

                    // The SAME question against detections made WITHOUT the solver's downsample.
                    // If these agree, the pool is at fault; if only the binned ones miss, the
                    // downsample-and-rescale step is displacing every centroid it produces.
                    if (detectionScale > 1 && fullResPts is { Length: > 0 })
                    {
                        var fullNearest = new List<double>();
                        for (var i = 0; i < dst.Count; i++)
                        {
                            if (dst[i].X < 0 || dst[i].Y < 0 || dst[i].X >= image.Width || dst[i].Y >= image.Height)
                            {
                                continue;
                            }
                            var best = double.MaxValue;
                            foreach (var d in fullResPts)
                            {
                                var dd = Vector2.Distance(d, dst[i]);
                                if (dd < best) { best = dd; }
                            }
                            fullNearest.Add(best);
                        }
                        fullNearest.Sort();
                        output.WriteLine($"      SAME anchors vs FULL-RES detections ({fullResPts.Length}): " +
                            $"p10 {fullNearest[fullNearest.Count / 10]:F1} px, " +
                            $"p50 {fullNearest[fullNearest.Count / 2]:F1} px, " +
                            $"p90 {fullNearest[fullNearest.Count * 9 / 10]:F1} px");
                    }

                    var centre = Vector2.Transform(new Vector2((float)cx, (float)cy), trueM);
                    var dstInFrame = dst.Count(d => d.X >= 0 && d.Y >= 0 && d.X < image.Width && d.Y < image.Height);
                    output.WriteLine($"      frame centre ({cx:F0},{cy:F0}) maps to ({centre.X:F1},{centre.Y:F1}); " +
                        $"{dstInFrame}/{dst.Count} anchors land INSIDE the frame under the truth");

                    output.WriteLine("      the 12 brightest anchors, projected -> true, and what is actually there:");
                    for (var i = 0; i < Math.Min(12, whole.Length); i++)
                    {
                        var t = Vector2.Transform(whole[i], trueM);
                        output.WriteLine($"        projected ({whole[i].X,8:F1},{whole[i].Y,8:F1})");
                        var best = double.MaxValue;
                        foreach (var d in detPts)
                        {
                            var dd = Vector2.Distance(d, t);
                            if (dd < best) { best = dd; }
                        }
                        output.WriteLine($"        rank {i,3} at ({t.X,8:F1},{t.Y,8:F1})  nearest detection {best,9:F1} px");
                    }
                }
            }
        }
    }
}
