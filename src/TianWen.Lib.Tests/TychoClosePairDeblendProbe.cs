using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Extensions;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Does the detector split the close pairs that are REALLY THERE, according to the catalogue?
    /// </summary>
    /// <remarks>
    /// <para>The third leg of the deblending evidence, and the only one measured against sky rather
    /// than against something we made. <c>StarPairDeblendGroundTruthTests</c> plants pairs, so it can
    /// only measure what is RECOVERED; the real-frame counts and the yield probe can only measure what
    /// is INVENTED, because a bare frame carries no truth about how many stars are at a spot. A SOLVED
    /// frame does: project Tycho-2 through the fitted WCS and the catalogue says where the pairs are.
    /// </para>
    /// <para><b>The bar is what this rig can split, not what Tycho-2 lists.</b> At 135 mm with 3.76 um
    /// photosites the frame is SAMPLING-limited, not optics-limited: ~5.7 arcsec/px against a ~2.5
    /// arcsec Rayleigh limit for the aperture, and a measured FWHM of ~1.95 px. Two point sources have
    /// two maxima only beyond about 2*sigma, so below ~2 px no method resolves them and none is
    /// claimed. The probe therefore reports RECOVERY PER SEPARATION BIN and lets the limit show itself
    /// rather than asserting one.</para>
    /// <para>Proper motion is ignored deliberately: Tycho-2 is J2000 and typical motions are ~10
    /// mas/yr, i.e. ~0.05 px over the ~26 years to this frame, two orders below the pair separations
    /// being measured.</para>
    /// <para>Gated: set <c>TIANWEN_TYCHO_PAIRS=1</c>. It plate-solves and walks the whole catalogue, so
    /// it is far too slow for the push path.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class TychoClosePairDeblendProbe(ITestOutputHelper output)
    {
        /// <summary>
        /// Pixel scale recovered from the stars, in arcsec/px, i.e. a ~129.8 mm effective focal length
        /// for the 3.76 um photosites -- the lens is marketed as 135 mm and the frame's FOCALLEN says
        /// so, which is where the 3.9% error comes from. Every scale inside the solver's window
        /// converges here (see <see cref="ReportTheScaleToleranceCliff"/>), so it is a measurement
        /// rather than a constant that had to be chosen.
        /// </summary>
        private const double SolvedPixelScale = 5.977;

        /// <summary>Bridges the solver's own logger into the test output, so a refusal says why.</summary>
        private sealed class OutputLogger(ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => output.WriteLine($"    [{logLevel}] {formatter(state, exception)}");
        }

        [Fact]
        public async Task ReportWhichCatalogPairsAreSplit()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_TYCHO_PAIRS") == "1",
                "Set TIANWEN_TYCHO_PAIRS=1 to run the Tycho close-pair probe.");

            var ct = TestContext.Current.CancellationToken;
            // Read from the FILE, not the cached Image, so the header's own pointing comes with it:
            // CatalogPlateSolver builds its anchor pool from the brightest catalogue stars that project
            // inside the frame FROM THE HINT, so a blind call over a 4.8 degree field has nothing to
            // anchor on and simply returns no solution.
            var path = await SharedTestData.ExtractGZippedFitsFileAsync("RGGB_frame_bx0_by0_top_down", ct);
            Image.TryReadFitsFile(path, out var image, out var hint);
            Assert.NotNull(image);
            var dim = image!.GetImageDim();
            output.WriteLine($"header hint {(hint is { } h ? $"RA={h.CenterRA:F5}h Dec={h.CenterDec:F4}" : "(none)")}");
            var stars = (await image.FindStarsAsync(0, snrMin: 10f, maxStars: 5000, cancellationToken: ct)).ToArray();
            output.WriteLine($"frame {image.Width}x{image.Height}, {stars.Length} detections, imageDim {dim}");

            var db = await SharedCatalogDB.InitAsync(ct);

            // The header's FOCALLEN is 135 for a lens that is really ~130, so GetImageDim derives a
            // scale 3.9% small and pair-lock's usable window is about +/-3% -- it misses by 0.9 of a
            // percentage point and the frame does not solve. That is a SOLVER limitation measured in
            // ReportTheScaleToleranceCliff, not a fact about deblending, so this probe hands the
            // solver the scale recovered from the stars and gets on with its own question.
            var corrected = new ImageDim(SolvedPixelScale, image.Width, image.Height);
            var solver = new CatalogPlateSolver(db, new XunitLogger(output));
            var solved = await solver.SolveImageAsync(image, corrected, searchOrigin: hint, cancellationToken: ct);
            if (solved.Solution is not { } wcs)
            {
                output.WriteLine("NO SOLUTION -- the probe cannot run without a WCS");
                return;
            }

            output.WriteLine($"solved RA={wcs.CenterRA:F5}h Dec={wcs.CenterDec:F4} scale={wcs.PixelScaleArcsec:F3}\"/px");

            // Project every Tycho-2 star and keep the ones that land on the sensor. A linear walk over
            // the catalogue is simpler than a spatial query and costs a fraction of the solve.
            var all = new Tycho2StarLite[db.Tycho2StarCount];
            var copied = db.CopyTycho2Stars(all);
            var inFrame = new List<(double X, double Y, float V)>();
            for (var i = 0; i < copied; i++)
            {
                var s = all[i];
                if (wcs.SkyToPixel(s.RaHours, s.DecDeg) is not { } px)
                {
                    continue;
                }
                // A small margin in, so a pair cannot straddle the edge with one component off-sensor.
                if (px.X >= 16 && px.Y >= 16 && px.X < image.Width - 16 && px.Y < image.Height - 16)
                {
                    inFrame.Add((px.X, px.Y, s.VMag));
                }
            }

            output.WriteLine($"catalog: {copied} Tycho-2 stars, {inFrame.Count} project inside the frame");

            // Catalogue pairs, by projected separation.
            var pairs = new List<(int A, int B, double D)>();
            for (var i = 0; i < inFrame.Count; i++)
            {
                for (var j = i + 1; j < inFrame.Count; j++)
                {
                    var dx = inFrame[i].X - inFrame[j].X;
                    var dy = inFrame[i].Y - inFrame[j].Y;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    // Below half a pixel the two catalogue rows are the SAME object: Tycho-2 lists
                    // components of a multiple at a shared position, and it also carries duplicate
                    // entries. Those cannot be scored at all -- "closer to this one than to the
                    // other" is undefined when the two are identical -- so counting them reads as a
                    // detector failure when the star is plainly detected (the very same position hits
                    // twice in the 4.95 px row). They are not a separation this rig could ever
                    // resolve either, being under a tenth of the 6 arcsec pixel.
                    if (d is > 0.5 and <= 20)
                    {
                        pairs.Add((i, j, d));
                    }
                }
            }

            output.WriteLine($"catalog pairs closer than 20 px: {pairs.Count}");
            output.WriteLine("  sep(px)  sep(\")   Va    Vb   found  detail");

            var bins = new SortedDictionary<int, (int Pairs, int Both, int One, int None)>();
            foreach (var (ai, bi, d) in pairs.OrderBy(p => p.D))
            {
                var a = inFrame[ai];
                var b = inFrame[bi];
                var na = NearestDetection(stars, a.X, a.Y, b.X, b.Y, d);
                var nb = NearestDetection(stars, b.X, b.Y, a.X, a.Y, d);
                var found = (na >= 0 ? 1 : 0) + (nb >= 0 ? 1 : 0);

                var bin = (int)Math.Floor(d);
                bins.TryGetValue(bin, out var t);
                bins[bin] = (t.Pairs + 1, t.Both + (found == 2 ? 1 : 0), t.One + (found == 1 ? 1 : 0), t.None + (found == 0 ? 1 : 0));

                output.WriteLine(
                    $"  {d,6:F2}  {d * wcs.PixelScaleArcsec,6:F1}  {a.V,5:F2} {b.V,5:F2}   {found}     " +
                    $"({a.X,7:F1},{a.Y,7:F1}) {(na >= 0 ? $"hit {na:F2}px" : "MISS")} / " +
                    $"({b.X,7:F1},{b.Y,7:F1}) {(nb >= 0 ? $"hit {nb:F2}px" : "MISS")}");
            }

            output.WriteLine("\n  separation bin -> pairs, both split, one found, neither");
            foreach (var (bin, t) in bins)
            {
                output.WriteLine($"    {bin,2}-{bin + 1,2} px ({bin * wcs.PixelScaleArcsec,5:F1}-{(bin + 1) * wcs.PixelScaleArcsec,5:F1}\"): " +
                                 $"{t.Pairs,3} pairs, both {t.Both,3}, one {t.One,3}, neither {t.None,3}");
            }
        }

        /// <summary>
        /// Distance to the nearest detection that belongs to this component rather than its companion:
        /// within tolerance AND closer to it than to the other, so one merged detection sitting between
        /// the two can never be credited to either. Returns -1 when nothing qualifies.
        /// </summary>
        private static double NearestDetection(ImagedStar[] stars, double tx, double ty, double ox, double oy, double separation)
        {
            // Half the separation keeps a midpoint detection out; the 2 px floor absorbs the solve
            // residual and centroid error on the widest pairs, where half the separation is generous.
            var tol = Math.Max(separation * 0.5, 2.0);
            var best = -1.0;
            foreach (var s in stars)
            {
                var d = Math.Sqrt((s.XCentroid - tx) * (s.XCentroid - tx) + (s.YCentroid - ty) * (s.YCentroid - ty));
                var dOther = Math.Sqrt((s.XCentroid - ox) * (s.XCentroid - ox) + (s.YCentroid - oy) * (s.YCentroid - oy));
                if (d <= tol && d < dOther && (best < 0 || d < best))
                {
                    best = d;
                }
            }
            return best;
        }
    }
}
