using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// How wrong may the pixel scale we hand the plate solver be?
    /// </summary>
    /// <remarks>
    /// <para>It matters because the prior is usually a GUESS. <see cref="Image.GetImageDim"/> prefers a
    /// declared <c>PIXSCALE</c>, but most frames do not carry one and it falls back to
    /// <c>FOCALLEN</c> -- whatever a human typed into a capture profile. Two real errors are measured
    /// here: a 202.5 mm SV545 entered as 205 (1.2%) and a 130 mm lens entered as its MARKETED 135
    /// (3.9%). The second is not a typo but a systematic, so it recurs across every frame that rig
    /// ever took.</para>
    /// <para>The ladder in <see cref="ReportWhetherACentralCropLocks"/> is the reason this file exists
    /// rather than a note about lens distortion: the full 4.8 degree frame refused to solve while
    /// central crops of the SAME data solved happily, which reads exactly like a field too wide for one
    /// similarity transform -- and is not. It is the scale prior, and the crops only helped because
    /// <c>MinBaselineFraction</c> scales the shortest usable baseline with the frame, letting the
    /// absolute +/-3 px slack in the pair window cover a fractional error it cannot cover at full
    /// size.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class SolverScaleToleranceTests(ITestOutputHelper output)
    {
        /// <summary>
        /// Pixel scale recovered from the stars, in arcsec/px: a ~129.8 mm effective focal length for
        /// these 3.76 um photosites, against a lens marketed as 135 mm. Not a chosen constant -- our
        /// solver returns it from every starting scale inside its window, and ASTAP independently
        /// returns 5.974-5.975 from a hint anywhere between -33% and +50%.
        /// </summary>
        private const double SolvedPixelScale = 5.977;

        /// <summary>
        /// The Horsehead frame solves with the scale its own header implies, marketed focal length and
        /// all.
        /// </summary>
        /// <remarks>
        /// <para>The regression guard for the pair-lock scale tolerance. This frame carries
        /// <c>FOCALLEN = 135</c> and no <c>PIXSCALE</c>, so the solver is handed 5.745 arcsec/px for a
        /// field that is really 5.977 -- 3.9% small. At the old 3% floor it did not solve AT ALL,
        /// despite 3,065 detections and 1,197 Tycho-2 stars inside the frame, because a pair length
        /// outside the window is never admitted rather than merely disfavoured.</para>
        /// <para>The header is deliberately left wrong. A frame whose stated focal length is the
        /// marketed one is the case worth keeping, and sanitising it would retire the only regression
        /// fixture for this class of error.</para>
        /// </remarks>
        [Fact]
        public async Task AMarketedFocalLengthStillSolves()
        {
            var ct = TestContext.Current.CancellationToken;
            var path = await SharedTestData.ExtractGZippedFitsFileAsync("RGGB_frame_bx0_by0_top_down", ct);
            Image.TryReadFitsFile(path, out var image, out var hint);
            image.ShouldNotBeNull();

            var dim = image.GetImageDim();
            dim.ShouldNotBeNull();
            // The premise: the frame really is telling the solver a scale several percent wrong. If a
            // future header fix makes this false, the test below stops guarding anything.
            var error = dim.Value.PixelScale / SolvedPixelScale - 1;
            output.WriteLine($"header-derived {dim.Value.PixelScale:F4}\"/px vs solved {SolvedPixelScale:F3} -> {error:P1}");
            Math.Abs(error).ShouldBeGreaterThan(0.03, "the fixture no longer carries a >3% scale error, so this guards nothing");

            var db = await SharedCatalogDB.InitAsync(ct);
            var solver = new CatalogPlateSolver(db, NullLogger.Instance);
            var result = await solver.SolveImageAsync(image, dim, searchOrigin: hint, cancellationToken: ct);

            result.Solution.ShouldNotBeNull("the frame must solve despite the marketed focal length");
            var wcs = result.Solution.Value;
            output.WriteLine($"solved RA={wcs.CenterRA:F5}h Dec={wcs.CenterDec:F4} scale={wcs.PixelScaleArcsec:F3}\"/px");

            // Known position, from two independent solvers agreeing to the fifth decimal.
            wcs.CenterRA.ShouldBe(5.6774, 0.002);
            wcs.CenterDec.ShouldBe(-2.470, 0.01);
            // And the scale is RECOVERED, not echoed back: it must land on the stars, not on FOCALLEN.
            wcs.PixelScaleArcsec.ShouldBe(SolvedPixelScale, 0.02);
        }

        /// <summary>
        /// Where does ASTAP give up, given the same wrong scale hint?
        /// </summary>
        /// <remarks>
        /// <para>The control for <see cref="ReportTheScaleToleranceCliff"/>. Ours is a pair-based seed
        /// and needs a scale prior; ASTAP matches quads, whose descriptor is pure ratios and therefore
        /// scale-free, so the prediction is that it degrades far more gracefully -- it is given
        /// <c>-fov</c> as a HINT and can search around it, where a pair length outside the window is
        /// simply never admitted.</para>
        /// <para><b>Every sidecar is deleted before each run and the deletion is ASSERTED</b>, because
        /// ASTAP writes its solution to a <c>.ini</c> and a <c>.wcs</c> beside the input: a stale pair
        /// from the previous iteration would be read as a success and the whole sweep would report
        /// that ASTAP never gives up, no matter what it was told.
        /// <c>ExternalProcessPlateSolverBase</c> already clears them, so this is a belt-and-braces
        /// check that the measurement means what it says.</para>
        /// </remarks>
        [Fact]
        public async Task ReportWhereAstapGivesUp()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_TYCHO_PAIRS") == "1",
                "Set TIANWEN_TYCHO_PAIRS=1 to run the ASTAP scale-cliff probe.");

            var ct = TestContext.Current.CancellationToken;
            var source = await SharedTestData.ExtractGZippedFitsFileAsync("RGGB_frame_bx0_by0_top_down", ct);
            Image.TryReadFitsFile(source, out var image, out var hint);
            Assert.NotNull(image);

            var solver = new AstapPlateSolver();
            Assert.SkipUnless(await solver.CheckSupportAsync(ct), "ASTAP is not installed on this box");

            output.WriteLine($"truth {SolvedPixelScale:F3}\"/px; sweeping the -fov hint ASTAP is given");
            output.WriteLine("  scale   error   fov     result");

            foreach (var scale in new[] { 2.99, 4.00, 4.78, 5.38, 5.745, 5.977, 6.57, 7.17, 8.97, 11.95 })
            {
                foreach (var ext in new[] { ".ini", ".wcs" })
                {
                    var sidecar = System.IO.Path.ChangeExtension(source, ext);
                    if (System.IO.File.Exists(sidecar))
                    {
                        System.IO.File.Delete(sidecar);
                    }
                    Assert.False(System.IO.File.Exists(sidecar), $"{ext} sidecar survived deletion; a solve would read the cached answer");
                }

                var dim = new ImageDim(scale, image!.Width, image.Height);
                string verdict;
                try
                {
                    var r = await solver.SolveFileAsync(source, dim, searchOrigin: hint, cancellationToken: ct);
                    verdict = r.Solution is { } w
                        ? $"SOLVED -> RA={w.CenterRA:F5}h Dec={w.CenterDec:F4} scale={w.PixelScaleArcsec:F3}"
                        : "no solution";
                }
                catch (Exception ex)
                {
                    verdict = $"threw {ex.GetType().Name}: {ex.Message}";
                }

                output.WriteLine($"  {scale,6:F3}  {(scale / SolvedPixelScale - 1) * 100,6:F1}%  {dim.FieldOfView.height,5:F2}  {verdict}");
            }
        }

        /// <summary>
        /// How far wrong may the pixel scale we HAND the solver be before it stops solving?
        /// </summary>
        /// <remarks>
        /// <para>This frame states <c>FOCALLEN = 135</c> and no <c>PIXSCALE</c>, so
        /// <see cref="Image.GetImageDim"/> derives 5.745 arcsec/px from the focal length. The lens is
        /// really ~130 mm and the true scale is ~5.977, so the solver is told a scale 4.0% small. The
        /// declared-scale preference in <c>GetImageDim</c> cannot help here -- there is no declared
        /// scale to prefer -- and the case it was built on (205 mm stated for a 202.5 mm SV545) was a
        /// 1.2% error, more than three times smaller.</para>
        /// <para>Why that should matter: <c>PairRansacLock</c> admits a catalog pair for a detected
        /// pair only when its length falls in <c>[dDet/(1+tol) - 3, dDet/(1-tol) + 3]</c> px, with
        /// <c>tol = max(scaleRange, 0.02)</c>. The +/-3 px is ABSOLUTE slack, so it covers a
        /// FRACTIONAL scale error only on SHORT baselines -- and <c>MinBaselineFraction = 0.2</c>
        /// forbids short ones, demanding 601 px on this 3008 px frame. On a wide frame the true pair
        /// is therefore not merely unlikely, it is never admitted at all.</para>
        /// <para>The sweep separates that story from every other one: if the full frame solves the
        /// moment it is handed the right scale, then the field width and the lens distortion are
        /// innocent and the scale prior is the whole bug. ASTAP is the control -- it was given the
        /// SAME wrong 5.745 and solved in 226 ms, because quad shape ratios are scale-invariant where
        /// pair lengths are not.</para>
        /// </remarks>
        [Fact]
        public async Task ReportTheScaleToleranceCliff()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_TYCHO_PAIRS") == "1",
                "Set TIANWEN_TYCHO_PAIRS=1 to run the solver scale-cliff probe.");

            var ct = TestContext.Current.CancellationToken;
            var path = await SharedTestData.ExtractGZippedFitsFileAsync("RGGB_frame_bx0_by0_top_down", ct);
            Image.TryReadFitsFile(path, out var image, out var hint);
            Assert.NotNull(image);

            var db = await SharedCatalogDB.InitAsync(ct);
            var derived = image!.GetImageDim()!.Value.PixelScale;
            output.WriteLine($"header-derived {derived:F4}\"/px from FOCALLEN 135; ASTAP solved the same frame at 5.977\"/px");
            output.WriteLine("  scale   error   result");

            foreach (var scale in new[] { 5.745, 5.80, 5.85, 5.90, 5.93, 5.95, 5.977, 6.00, 6.05, 6.10, 6.20 })
            {
                var dim = new ImageDim(scale, image.Width, image.Height);
                var solver = new CatalogPlateSolver(db, NullLogger.Instance);
                string verdict;
                try
                {
                    var r = await solver.SolveImageAsync(image, dim, searchOrigin: hint, cancellationToken: ct);
                    verdict = r.Solution is { } w
                        ? $"SOLVED -> RA={w.CenterRA:F5}h Dec={w.CenterDec:F4} scale={w.PixelScaleArcsec:F3}"
                        : "no solution";
                }
                catch (Exception ex)
                {
                    verdict = $"threw {ex.GetType().Name}";
                }

                output.WriteLine($"  {scale,6:F3}  {(scale / 5.977 - 1) * 100,5:F1}%  {verdict}");
            }
        }

        /// <summary>
        /// Does our own solver refuse this frame because the FIELD IS TOO WIDE for one similarity
        /// transform, or for some other reason?
        /// </summary>
        /// <remarks>
        /// <para>The full frame reaches pair-lock at 17-18 hits against an accept threshold of 24,
        /// where 24 is <c>ConsensusFloorFraction</c> (0.15) times the 160-star catalog census -- NOT
        /// the chance test, which sits at 2.0, so 17 hits is already ~8.5x chance. That is the
        /// signature of a seed that is real but under-counted, and the obvious way to under-count a
        /// real seed is optical distortion: <c>PairRansacLock</c> verifies a hypothesis with a fixed
        /// pixel tolerance against a SIMILARITY transform, so on a field wide enough for the lens to
        /// bend, only stars near the centre land inside the tolerance and the rest are invisible to
        /// the census.</para>
        /// <para>This isolates that one variable. A 1024 px central crop is the SAME optics, the same
        /// stars, the same catalogue and the same hint -- only the field is 1.63 degrees instead of
        /// 4.80. If the crop locks and the full frame does not, field width is the cause and the fix
        /// belongs in how the census is verified, not in the accept threshold. If the crop ALSO fails,
        /// distortion is not the story and the anchor depth or the hint is.</para>
        /// <para>The crop origin is EVEN on both axes deliberately: the frame is an RGGB mosaic, so an
        /// odd offset rotates the Bayer phase and every downstream colour and detection assumption
        /// with it.</para>
        /// </remarks>
        [Fact]
        public async Task ReportWhetherACentralCropLocks()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_TYCHO_PAIRS") == "1",
                "Set TIANWEN_TYCHO_PAIRS=1 to run the solver field-width probe.");

            var ct = TestContext.Current.CancellationToken;
            var path = await SharedTestData.ExtractGZippedFitsFileAsync("RGGB_frame_bx0_by0_top_down", ct);
            Image.TryReadFitsFile(path, out var full, out var hint);
            Assert.NotNull(full);

            var db = await SharedCatalogDB.InitAsync(ct);

            foreach (var side in new[] { 3008, 2048, 1024, 512 })
            {
                var cropped = CropCentred(full!, side);
                var dim = cropped.GetImageDim();
                var solver = new CatalogPlateSolver(db, new XunitLogger(output));
                output.WriteLine($"\n=== {side}x{side} ({side * (dim?.PixelScale ?? double.NaN) / 3600.0:F2} deg) ===");
                try
                {
                    var r = await solver.SolveImageAsync(cropped, dim, searchOrigin: hint, cancellationToken: ct);
                    output.WriteLine(r.Solution is { } w
                        ? $"  SOLVED RA={w.CenterRA:F5}h Dec={w.CenterDec:F4} scale={w.PixelScaleArcsec:F3}\"/px"
                        : "  NO SOLUTION");
                }
                catch (Exception ex)
                {
                    output.WriteLine($"  THREW {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Centre crop to <paramref name="side"/> px, snapped to an EVEN origin so the RGGB phase is
        /// preserved. Returns the input unchanged when it is already that size.
        /// </summary>
        private static Image CropCentred(Image source, int side)
        {
            if (side >= source.Width && side >= source.Height)
            {
                return source;
            }

            var x0 = ((source.Width - side) / 2) & ~1;
            var y0 = ((source.Height - side) / 2) & ~1;
            var data = new float[side, side];
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    data[y, x] = source[0, y0 + y, x0 + x];
                }
            }

            return new Image([data], source.BitDepth, source.MaxValue, source.MinValue, source.Pedestal, source.ImageMeta);
        }

    }
}
