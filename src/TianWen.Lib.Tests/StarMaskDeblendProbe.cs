using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// How much the star-area mask costs, in both directions: stars swallowed inside another star's
    /// mask (a MERGE) and the mask's own footprint against the star's real above-threshold extent
    /// (which is what lets a DUPLICATE escape).
    /// </summary>
    /// <remarks>
    /// <para>The mask radius is <c>1.5 * HFD</c> and it does two jobs at once: it stops the loop
    /// re-triggering on a star it already recorded, and -- as a side effect nobody chose -- it forbids
    /// any OTHER star inside that radius, because a candidate whose centroid lands in the mask is
    /// rejected by <c>CentroidAlreadyClaimed</c> and its trigger pixels are skipped outright. HFD is a
    /// FLUX radius, so for a saturated star it understates the footprint (duplicates escape) while for
    /// an ordinary one 1.5 * HFD is about 1.65 * FWHM, i.e. ~3.9 sigma (companions vanish).</para>
    /// <para>A probe rather than a test: it measures and reports, it asserts nothing about the numbers,
    /// and it is gated so a bare <c>dotnet test</c> does not pay for it. Run it with
    /// <c>TIANWEN_MASK_PROBE=1</c>.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class StarMaskDeblendProbe(ITestOutputHelper output)
    {
        [Theory]
        [InlineData("RGGB_frame_bx0_by0_top_down", 10f, 5000)]
        [InlineData("image_file-snr-20_stars-28_1280x960x16", 10f, 500)]
        public async Task ReportWhatTheMaskSwallows(string name, float snrMin, int maxStars)
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_MASK_PROBE") == "1",
                "Set TIANWEN_MASK_PROBE=1 to run the star-mask probe.");

            var ct = TestContext.Current.CancellationToken;
            var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: ct);
            var stars = (await image.FindStarsAsync(0, snrMin, maxStars, cancellationToken: ct)).ToArray();

            var (bg, starLevel, noise, _) = image.Background(0);
            var detectionLevel = Image.FirstPassDetectionLevel(noise, starLevel, float.PositiveInfinity);
            output.WriteLine($"{name}: {stars.Length} stars, bg={bg:F1} noise={noise:F2} starLevel={starLevel:F1} detectionLevel={detectionLevel:F1}");

            // (1) MERGE cost: a second local maximum above the detection level, inside an accepted
            // star's mask but far enough from its centroid to be a different object. These are the
            // companions the mask silently ate -- nothing downstream can know they were there.
            var swallowed = 0;
            var swallowedExamples = new List<string>();
            // (2) DUPLICATE exposure: how far the star's own above-threshold pixels actually reach,
            // against the radius the mask covers. Where the reach exceeds the mask, a halo pixel can
            // still trigger, which is the geometry the surviving duplicate pair lives in.
            var maskShortfall = 0;
            var shortfallExamples = new List<string>();

            foreach (var star in stars)
            {
                var cx = (int)MathF.Round(star.XCentroid);
                var cy = (int)MathF.Round(star.YCentroid);
                var maskRadius = MathF.Round(Image.HfdFactor * star.HFD);
                var probeRadius = (int)MathF.Min(Image.BoxRadius, MathF.Max(maskRadius * 3f, 6f));

                var reach = 0f;
                var secondPeak = -1f;
                var secondAt = (X: 0, Y: 0);

                for (var dy = -probeRadius; dy <= probeRadius; dy++)
                {
                    for (var dx = -probeRadius; dx <= probeRadius; dx++)
                    {
                        var x = cx + dx;
                        var y = cy + dy;
                        if ((uint)x >= (uint)image.Width || (uint)y >= (uint)image.Height)
                        {
                            continue;
                        }

                        var value = image[0, y, x] - bg;
                        if (value <= detectionLevel)
                        {
                            continue;
                        }

                        var dist = MathF.Sqrt(dx * dx + dy * dy);
                        if (dist > reach)
                        {
                            reach = dist;
                        }

                        // A local maximum at least 2 px from the recorded centroid, inside the mask: a
                        // candidate the mask suppressed. 2 px because a single PSF's own shoulder can
                        // wobble by a pixel; beyond that it is a separate peak.
                        if (dist >= 2f && dist <= maskRadius && IsLocalMax(image, x, y))
                        {
                            if (value > secondPeak)
                            {
                                secondPeak = value;
                                secondAt = (x, y);
                            }
                        }
                    }
                }

                if (secondPeak > 0f)
                {
                    swallowed++;
                    if (swallowedExamples.Count < 5)
                    {
                        swallowedExamples.Add(
                            $"({star.XCentroid:F1},{star.YCentroid:F1}) hfd={star.HFD:F2} mask={maskRadius:F0}px" +
                            $" -> second peak at ({secondAt.X},{secondAt.Y}) +{secondPeak:F0} ADU");
                    }
                }

                if (reach > maskRadius)
                {
                    maskShortfall++;
                    if (shortfallExamples.Count < 5)
                    {
                        shortfallExamples.Add(
                            $"({star.XCentroid:F1},{star.YCentroid:F1}) hfd={star.HFD:F2} mask={maskRadius:F0}px reach={reach:F1}px");
                    }
                }
            }

            output.WriteLine($"  MERGED (a suppressed second peak inside the mask): {swallowed} of {stars.Length} ({(double)swallowed / stars.Length:P1})");
            foreach (var e in swallowedExamples) output.WriteLine($"    {e}");
            output.WriteLine($"  MASK TOO SMALL (above-threshold reach exceeds mask): {maskShortfall} of {stars.Length} ({(double)maskShortfall / stars.Length:P1})");
            foreach (var e in shortfallExamples) output.WriteLine($"    {e}");

            var hfds = stars.Select(s => s.HFD).Order().ToArray();
            output.WriteLine($"  HFD p5={hfds[hfds.Length / 20]:F2} p50={hfds[hfds.Length / 2]:F2} p95={hfds[hfds.Length * 19 / 20]:F2}" +
                             $" -> mask radius p50={MathF.Round(Image.HfdFactor * hfds[hfds.Length / 2]):F0}px");
        }

        /// <summary>
        /// A STRICT maximum over a 5x5 neighbourhood: every neighbour must be lower, not merely not
        /// higher.
        /// </summary>
        /// <remarks>
        /// Both halves matter, and the first version of this probe had neither. A saturated star has a
        /// FLAT TOP -- many pixels at the identical clipped value -- so a 3x3 test using <c>&gt;</c>
        /// reports every plateau pixel as its own maximum, and the probe then counts a bright star's own
        /// core as a swallowed companion. That is where the first run's <c>+63029 ADU</c> rows came from,
        /// at exactly the clipping level. Requiring strictly-lower neighbours disqualifies a plateau by
        /// construction, and widening to 5x5 stops a two-pixel plateau slipping through.
        /// </remarks>
        private static bool IsLocalMax(Image image, int x, int y)
        {
            var v = image[0, y, x];
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }
                    var nx = x + dx;
                    var ny = y + dy;
                    if ((uint)nx >= (uint)image.Width || (uint)ny >= (uint)image.Height)
                    {
                        continue;
                    }
                    if (image[0, ny, nx] >= v)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
