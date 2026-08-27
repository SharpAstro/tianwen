using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// What each of the two star-mask radii costs, now that they are separate: stars swallowed as
    /// re-detections (a MERGE, charged to the CLAIM radius) and the footprint's own reach against the
    /// star's real above-threshold extent (which is what lets a DUPLICATE escape).
    /// </summary>
    /// <remarks>
    /// <para>One radius, <c>1.5 * HFD</c>, used to do both jobs. It gated the scan (so the whole wing
    /// of a measured star is skipped) AND decided whether a candidate's centroid was a re-detection.
    /// The first job wants to be generous and the second tight, so at <c>1.5 * HFD</c> (about
    /// 1.65 * FWHM, i.e. ~3.9 sigma) the merge side paid. The claim radius is now
    /// <c>max(1, round(0.5 * HFD))</c>, the half-flux radius, and this probe reports the merge count
    /// under BOTH radii from the same star list, so the difference is what the split bought.</para>
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
            // The logger is what reports analyseStarCalls per pass, i.e. what the local-maximum escape
            // on the footprint skip costs.
            var stars = (await image.FindStarsAsync(0, snrMin, maxStars, logger: new XunitLogger(output), cancellationToken: ct)).ToArray();

            var (bg, starLevel, noise, _) = image.Background(0);
            var detectionLevel = Image.FirstPassDetectionLevel(noise, starLevel, float.PositiveInfinity);
            output.WriteLine($"{name}: {stars.Length} stars, bg={bg:F1} noise={noise:F2} starLevel={starLevel:F1} detectionLevel={detectionLevel:F1}");

            // (1) MERGE cost: a second local maximum above the detection level, inside a radius but
            // far enough from the accepted centroid to be a different object. Counted against the
            // CLAIM radius (what actually rejects a candidate today) and against the FOOTPRINT radius
            // (what used to), so the pair of numbers is the size of the split.
            var mergedByClaim = 0;
            var mergedByFootprint = 0;
            var mergedExamples = new List<string>();
            // (2) DUPLICATE exposure: how far the star's own above-threshold pixels actually reach,
            // against the footprint that is supposed to cover them. Where the reach exceeds it, a halo
            // pixel can still trigger -- which is survivable now, because the claim has the final say.
            var maskShortfall = 0;
            var shortfallExamples = new List<string>();

            foreach (var star in stars)
            {
                var cx = (int)MathF.Round(star.XCentroid);
                var cy = (int)MathF.Round(star.YCentroid);
                var footprintRadius = MathF.Round(Image.HfdFactor * star.HFD);
                var claimRadius = Math.Max(1, (int)MathF.Round(Image.ClaimFactor * star.HFD));
                var probeRadius = (int)MathF.Min(Image.BoxRadius, MathF.Max(footprintRadius * 3f, 6f));

                var reach = 0f;
                var secondPeakClaim = -1f;
                var secondPeakFootprint = -1f;
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

                        // A local maximum at least 2 px from the recorded centroid: a candidate that a
                        // radius reaching this far would suppress. 2 px because a single PSF's own
                        // shoulder can wobble by a pixel; beyond that it is a separate peak.
                        if (dist >= 2f && dist <= footprintRadius && IsLocalMax(image, x, y))
                        {
                            if (value > secondPeakFootprint)
                            {
                                secondPeakFootprint = value;
                                secondAt = (x, y);
                            }
                            if (dist <= claimRadius && value > secondPeakClaim)
                            {
                                secondPeakClaim = value;
                            }
                        }
                    }
                }

                if (secondPeakFootprint > 0f)
                {
                    mergedByFootprint++;
                    if (mergedExamples.Count < 5)
                    {
                        mergedExamples.Add(
                            $"({star.XCentroid:F1},{star.YCentroid:F1}) hfd={star.HFD:F2} footprint={footprintRadius:F0}px claim={claimRadius}px" +
                            $" -> second peak at ({secondAt.X},{secondAt.Y}) +{secondPeakFootprint:F0} ADU");
                    }
                }

                if (secondPeakClaim > 0f)
                {
                    mergedByClaim++;
                }

                if (reach > footprintRadius)
                {
                    maskShortfall++;
                    if (shortfallExamples.Count < 5)
                    {
                        shortfallExamples.Add(
                            $"({star.XCentroid:F1},{star.YCentroid:F1}) hfd={star.HFD:F2} footprint={footprintRadius:F0}px reach={reach:F1}px");
                    }
                }
            }

            output.WriteLine($"  MERGED by the old single radius (1.5 * HFD): {mergedByFootprint} of {stars.Length} ({(double)mergedByFootprint / stars.Length:P1})");
            output.WriteLine($"  MERGED by today's claim radius (0.5 * HFD, min 1): {mergedByClaim} of {stars.Length} ({(double)mergedByClaim / stars.Length:P1})");
            foreach (var e in mergedExamples) output.WriteLine($"    {e}");
            output.WriteLine($"  FOOTPRINT TOO SMALL (above-threshold reach exceeds it): {maskShortfall} of {stars.Length} ({(double)maskShortfall / stars.Length:P1})");
            foreach (var e in shortfallExamples) output.WriteLine($"    {e}");

            // (3) What the split is FOR: pairs of ACCEPTED stars close enough that a single radius
            // doing both jobs would have reported one of them only. Counted in bands, because a pair
            // inside the claim radius is genuinely unresolvable while one outside it is a recovery.
            var resolvedPairs = 0;
            var closest = float.MaxValue;
            var resolvedExamples = new List<string>();
            for (var i = 0; i < stars.Length; i++)
            {
                for (var j = i + 1; j < stars.Length; j++)
                {
                    var dx = stars[i].XCentroid - stars[j].XCentroid;
                    var dy = stars[i].YCentroid - stars[j].YCentroid;
                    var d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d < closest)
                    {
                        closest = d;
                    }
                    if (d <= Image.HfdFactor * MathF.Max(stars[i].HFD, stars[j].HFD))
                    {
                        resolvedPairs++;
                        if (resolvedExamples.Count < 8)
                        {
                            resolvedExamples.Add(
                                $"({stars[i].XCentroid:F2},{stars[i].YCentroid:F2}) hfd={stars[i].HFD:F2}" +
                                $" / ({stars[j].XCentroid:F2},{stars[j].YCentroid:F2}) hfd={stars[j].HFD:F2} at {d:F2}px");
                        }
                    }
                }
            }
            output.WriteLine($"  RESOLVED PAIRS (both accepted, closer than the wider star's 1.5 * HFD): {resolvedPairs}, closest pair {closest:F2}px");
            foreach (var e in resolvedExamples) output.WriteLine($"    {e}");

            var hfds = stars.Select(s => s.HFD).Order().ToArray();
            output.WriteLine($"  HFD p5={hfds[hfds.Length / 20]:F2} p50={hfds[hfds.Length / 2]:F2} p95={hfds[hfds.Length * 19 / 20]:F2}" +
                             $" -> footprint p50={MathF.Round(Image.HfdFactor * hfds[hfds.Length / 2]):F0}px" +
                             $" claim p50={Math.Max(1, (int)MathF.Round(Image.ClaimFactor * hfds[hfds.Length / 2]))}px");
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
