using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pre-detection binning is PROPOSED by the declared plate scale and vetoed by the sampling the
    /// detector measures. These pin the veto, because the alternative -- a scale gate deciding alone
    /// -- is wrong in a way no output reveals: the solve still answers, from aliased centroids.
    /// </summary>
    [Collection("Astrometry")]
    public class SolverSamplingFloorTests(ITestOutputHelper output)
    {
        /// <summary>
        /// A real frame, told it is finely sampled. Every committed fixture is 2.9-6.0"/px, so none
        /// is a binning candidate on its own header and the veto would never be reached; handing the
        /// solver a fabricated fine <see cref="ImageDim"/> over the SAME pixels is what puts a real
        /// star profile behind a proposal to bin it. The stars then measure what they measure, and
        /// what they measure is a frame that cannot afford it.
        /// </summary>
        [Fact(Timeout = 600_000)]
        public async Task ABinTheStarsCannotAffordIsUndoneBeforeItReachesMatching()
        {
            var ct = TestContext.Current.CancellationToken;
            var image = await SharedTestData.ExtractGZippedFitsImageAsync(
                "Vela_SNR_Panel_8_1-Multi-NB-mono-Hydrogen-alpha-Oxygen_III-crop", isReadOnly: false, cancellationToken: ct);

            var db = new CelestialObjectDB();
            await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
            var solver = new CatalogPlateSolver(db, NullLogger<CatalogPlateSolver>.Instance);

            // 0.6"/px asks for a 3x bin (ceil(1.5 / 0.6)). The pixels are unchanged, so the stars are
            // still the ~1.75 px they were, and 3x would leave them at ~1.2 px -- aliased.
            var pretendFine = new ImageDim(0.6, image.Width, image.Height);
            var hint = new Astrometry.WCS(image.ImageMeta.TargetRA, image.ImageMeta.TargetDec);
            _ = await solver.SolveImageAsync(image, pretendFine, searchOrigin: hint, cancellationToken: ct);

            var binning = solver.LastDetectionBinning;
            output.WriteLine($"proposed {binning.Proposed}x, used {binning.Used}x");

            binning.Proposed.ShouldBe(3, "the fabricated 0.6\"/px scale must actually reach the gate -- "
                + "if this is 1 the test is asserting the veto on a frame that was never a candidate");
            binning.Used.ShouldBe(1, "a 3x bin puts this frame's stars under two pixels per FWHM, so the "
                + "sampling floor must undo it and re-detect at full resolution");

            image.Release();
        }

        /// <summary>
        /// The other half, and the reason the change is safe to ship without an oversampled fixture:
        /// on a frame at its OWN declared scale nothing is proposed, so nothing is vetoed and the
        /// path is byte-identical to the one that shipped.
        /// </summary>
        [Fact(Timeout = 600_000)]
        public async Task AFrameAtItsOwnScaleIsNeverABinningCandidate()
        {
            var ct = TestContext.Current.CancellationToken;
            var image = await SharedTestData.ExtractGZippedFitsImageAsync(
                "Vela_SNR_Panel_8_1-Multi-NB-mono-Hydrogen-alpha-Oxygen_III-crop", isReadOnly: false, cancellationToken: ct);
            var dim = image.GetImageDim();
            Assert.NotNull(dim);

            var db = new CelestialObjectDB();
            await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
            var solver = new CatalogPlateSolver(db, NullLogger<CatalogPlateSolver>.Instance);

            var hint = new Astrometry.WCS(image.ImageMeta.TargetRA, image.ImageMeta.TargetDec);
            var result = await solver.SolveImageAsync(image, dim.Value, searchOrigin: hint, cancellationToken: ct);

            solver.LastDetectionBinning.ShouldBe(new CatalogPlateSolver.DetectionBinning(1, 1));
            result.Solution.ShouldNotBeNull("the frame still solves, unbinned, exactly as before");

            image.Release();
        }

        [Theory]
        [InlineData(new float[] { }, null)]
        [InlineData(new[] { 0f, -1f }, null)]
        [InlineData(new[] { 3f, 1f, 2f }, 2f)]
        [InlineData(new[] { 0f, 3f, 1f }, 3f)]
        public void TheMedianWidthCountsOnlyTheStarsThatCarryOne(float[] widths, float? expected)
        {
            var bag = new ConcurrentBag<ImagedStar>();
            foreach (var w in widths)
            {
                bag.Add(new ImagedStar { StarFWHM = w, HFD = 1f, XCentroid = 1, YCentroid = 1, SNR = 10, Flux = 1 });
            }

            // An unmeasured width is absent, not narrow: counting it would drag the median down and
            // undo a bin that was fine. {0, 3, 1} therefore medians the pair {1, 3} to 3, not 1.
            CatalogPlateSolver.MedianStarFwhm(new StarList(bag)).ShouldBe(expected);
        }
    }
}
