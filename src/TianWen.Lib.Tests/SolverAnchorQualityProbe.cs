using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Are the stars the pair-lock anchors on actually STARS?
    /// </summary>
    /// <remarks>
    /// <para><c>CatalogPlateSolver</c> ranks detections by FLUX and <c>PairRansacLock</c> takes the top
    /// 48 as its hypothesis anchors. Flux is a fine proxy for "bright star" on a star field and a poor
    /// one on a nebula: an extended blob integrates a lot of flux without being a point source, and the
    /// catalogue holds only point sources, so an anchor set full of blobs cannot form a correspondence
    /// no matter how many hypotheses are tried.</para>
    /// <para>The measurement is the ratio of an anchor's HFD to the frame's MEDIAN HFD. A star sits
    /// near 1. A blob sits far above it, and the catalogue has nothing to match it to. Reported for
    /// the top 48 by flux, which is exactly the set the lock uses.</para>
    /// <para>Run against a frame that SOLVES as the control -- a difference only means something if
    /// the working case looks different. Gated: <c>TIANWEN_ANCHOR_PROBE_FITS</c>.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class SolverAnchorQualityProbe(ITestOutputHelper output)
    {
        [Fact]
        public async Task ReportWhetherTheBrightestDetectionsAreStars()
        {
            var path = Environment.GetEnvironmentVariable("TIANWEN_ANCHOR_PROBE_FITS");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(path), "Set TIANWEN_ANCHOR_PROBE_FITS");

            var ct = TestContext.Current.CancellationToken;
            Image.TryReadFitsFile(path!, out var image, out var wcs);
            Assert.NotNull(image);

            // Same detection parameters the solver uses, so the anchor set is the one it would see.
            var stars = (await image!.FindStarsAsync(
                image.ReferenceStarChannel, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0,
                maxFirstPassNoiseSigma: Image.MaxFirstPassNoiseSigma, cancellationToken: ct)).ToArray();

            var hfds = stars.Select(s => s.HFD).Order().ToArray();
            var median = hfds[hfds.Length / 2];
            output.WriteLine($"{System.IO.Path.GetFileName(path)}: {stars.Length} detections, HFD median {median:F2} " +
                             $"(p5 {hfds[hfds.Length / 20]:F2}, p95 {hfds[hfds.Length * 19 / 20]:F2})");

            // The 48 the lock actually anchors on.
            const int Anchors = 48;
            var byFlux = stars.OrderByDescending(s => s.Flux).Take(Anchors).ToArray();
            var ratios = byFlux.Select(s => s.HFD / median).Order().ToArray();
            output.WriteLine($"  top {byFlux.Length} by FLUX: HFD/median  min {ratios[0]:F2}  " +
                             $"p50 {ratios[ratios.Length / 2]:F2}  max {ratios[^1]:F2}");
            output.WriteLine($"    within 1.5x median: {byFlux.Count(s => s.HFD <= 1.5f * median),3} of {byFlux.Length}");
            output.WriteLine($"    beyond 2.0x median: {byFlux.Count(s => s.HFD > 2.0f * median),3} of {byFlux.Length}");
            output.WriteLine($"    mean ellipticity:   {byFlux.Average(s => s.Ellipticity):F2}");

            // What a star-likeness ranking would pick instead: brightest AMONG those whose size and
            // roundness look stellar. If the two sets are nearly identical the hypothesis is wrong.
            var starLike = stars
                .Where(s => s.HFD <= 1.5f * median && s.HFD >= 0.5f * median && s.Ellipticity <= 0.6f)
                .OrderByDescending(s => s.Flux)
                .Take(Anchors)
                .ToArray();
            output.WriteLine($"  top {starLike.Length} STAR-LIKE: HFD/median " +
                             $"p50 {(starLike.Length > 0 ? starLike.OrderBy(s => s.HFD).ElementAt(starLike.Length / 2).HFD / median : 0):F2}" +
                             $"  mean ellipticity {(starLike.Length > 0 ? starLike.Average(s => s.Ellipticity) : 0):F2}");

            var overlap = byFlux.Count(a => starLike.Any(b => Math.Abs(a.XCentroid - b.XCentroid) < 0.01f
                                                              && Math.Abs(a.YCentroid - b.YCentroid) < 0.01f));
            output.WriteLine($"  the two anchor sets share {overlap} of {Anchors} -- a low number is the hypothesis");
        }
    }
}
