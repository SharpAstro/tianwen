using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// What the deblender actually did to a REAL frame: how many measurements it split, where the
    /// components landed, and whether any of them is a phantom rather than a star.
    /// </summary>
    /// <remarks>
    /// <para>The synthetic recovery curve (<see cref="StarPairDeblendGroundTruthTests"/>) proves what
    /// is RECOVERED, because there the truth is known. It cannot prove what is INVENTED, because
    /// everything it plants is real. That is this probe's job, and it is the check the reverted
    /// radius-splitting attempt (7ff7a4bc) failed: a phantom at the midpoint of two real stars carried
    /// HFD 12.26 against a frame median of 2.40, and every metric except a human looking at the frame
    /// called it a success.</para>
    /// <para>So the tell it reports is the one that would have caught that: a component whose HFD is
    /// far above the frame's median is not a companion, it is a measurement of two things at once.
    /// Read the ratio, not the count.</para>
    /// <para>Gated: set <c>TIANWEN_DEBLEND_PROBE=1</c>.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class StarDeblendYieldProbe(ITestOutputHelper output)
    {
        [Theory]
        [InlineData("RGGB_frame_bx0_by0_top_down", 10f, 5000)]
        [InlineData("RGGB_frame_bx0_by0_top_down", 30f, 5000)]
        [InlineData("image_file-snr-20_stars-28_1280x960x16", 10f, 500)]
        public async Task ReportWhatTheDeblenderProduced(string name, float snrMin, int maxStars)
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_DEBLEND_PROBE") == "1",
                "Set TIANWEN_DEBLEND_PROBE=1 to run the deblend-yield probe.");

            var ct = TestContext.Current.CancellationToken;
            var image = await SharedTestData.ExtractGZippedFitsImageAsync(name, cancellationToken: ct);
            var stars = (await image.FindStarsAsync(0, snrMin, maxStars, cancellationToken: ct)).ToArray();

            var hfds = stars.Select(s => s.HFD).Order().ToArray();
            var median = hfds[hfds.Length / 2];
            output.WriteLine($"{name} @ SNR {snrMin}: {stars.Length} stars, HFD p50={median:F2} p95={hfds[hfds.Length * 19 / 20]:F2} max={hfds[^1]:F2}");

            // Close pairs are what a deblender produces, so they are where its output has to be judged.
            // Anything closer than the wider star's suppression radius could not have been reported by
            // the pre-deblend detector at all.
            var pairs = new List<(ImagedStar A, ImagedStar B, float D)>();
            for (var i = 0; i < stars.Length; i++)
            {
                for (var j = i + 1; j < stars.Length; j++)
                {
                    var dx = stars[i].XCentroid - stars[j].XCentroid;
                    var dy = stars[i].YCentroid - stars[j].YCentroid;
                    var d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d <= Image.HfdFactor * MathF.Max(stars[i].HFD, stars[j].HFD))
                    {
                        pairs.Add((stars[i], stars[j], d));
                    }
                }
            }

            output.WriteLine($"  RESOLVED PAIRS (closer than the wider star's {Image.HfdFactor} * HFD): {pairs.Count}");
            foreach (var (a, b, d) in pairs.OrderBy(p => p.D).Take(12))
            {
                output.WriteLine(
                    $"    ({a.XCentroid,7:F2},{a.YCentroid,7:F2}) hfd={a.HFD:F2} fwhm={a.StarFWHM:F2} snr={a.SNR,7:F1}" +
                    $" / ({b.XCentroid,7:F2},{b.YCentroid,7:F2}) hfd={b.HFD:F2} fwhm={b.StarFWHM:F2} snr={b.SNR,7:F1} at {d:F2}px");
            }

            // The phantom tell. A merged measurement's HFD is inflated by the SEPARATION of what it
            // merged, so it stands far above the frame's own median; a genuine component does not.
            var suspect = stars.Where(s => s.HFD > 3f * median).ToArray();
            output.WriteLine($"  HFD > 3x median ({3 * median:F2}px), i.e. a measurement of more than one thing: {suspect.Length}");
            foreach (var s in suspect.OrderByDescending(s => s.HFD).Take(10))
            {
                output.WriteLine($"    ({s.XCentroid:F2},{s.YCentroid:F2}) hfd={s.HFD:F2} fwhm={s.StarFWHM:F2} ell={s.Ellipticity:F2} snr={s.SNR:F1}");
            }

            // A deblended component carries the FITTED width, so the two populations are separable and
            // the split ones can be characterised on their own rather than washed out by 3,000 others.
            var ell = stars.Select(s => s.Ellipticity).Order().ToArray();
            output.WriteLine($"  ellipticity p50={ell[ell.Length / 2]:F2} p95={ell[ell.Length * 19 / 20]:F2}," +
                             $" over the pre-gate ({Image.DeblendMinEllipticity}): {stars.Count(s => Image.LooksBlended(s))}");
        }
    }
}
