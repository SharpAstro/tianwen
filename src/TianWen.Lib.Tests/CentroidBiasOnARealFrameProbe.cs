using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Is there a systematic offset between where detection says a star is and where its light actually
    /// is, on a REAL mosaic frame? Measured against the CFA photosites directly, so it depends on no
    /// debayer and on no coordinate convention but the mosaic's own.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists beside <c>BayerCentroidGroundTruthTests</c>.</b> That test is synthetic:
    /// it plants Gaussians at known positions and is the anchor for the convention (an integer index is
    /// a pixel CENTRE, which is also what the viewer's <c>+0.5</c> screen mapping assumes). What it
    /// cannot do is confirm the convention survives a real frame -- undersampled stars, a real PSF, CFA
    /// gains that differ per filter rather than per a chosen constant. This asks the same question of
    /// 3,000 real stars.</para>
    /// <para><b>The reference is a per-CFA-channel flux centroid.</b> For RGGB at BayerOffset (0,0) the
    /// R photosites sit at (even row, even column) and their mosaic coordinates are exactly those
    /// integers, so a flux-weighted mean over them is a position in mosaic coordinates that no debayer
    /// touched. Each channel is measured separately and then averaged, because a single centroid over
    /// the raw mosaic is pulled by whichever filter is brighter.</para>
    /// <para>Gated: <c>TIANWEN_CENTROID_BIAS=1</c>.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class CentroidBiasOnARealFrameProbe(ITestOutputHelper output)
    {
        [Fact]
        public async Task ReportSystematicCentroidOffset()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_CENTROID_BIAS") == "1",
                "Set TIANWEN_CENTROID_BIAS=1 to run.");

            var ct = TestContext.Current.CancellationToken;
            var image = await SharedTestData.ExtractGZippedFitsImageAsync("RGGB_frame_bx0_by0_top_down", cancellationToken: ct);
            var stars = (await image.FindStarsAsync(0, 10f, 5000, cancellationToken: ct)).ToArray();
            var (bg, _, noise, _) = image.Background(0);

            output.WriteLine($"{stars.Length} stars, bg={bg:F0} noise={noise:F1}");

            const int box = 4;      // +/- 4 px, so 4 samples per axis per CFA channel
            const float floor = 3f; // sigma above background to count as signal

            var dxs = new List<float>();
            var dys = new List<float>();
            var skipped = 0;

            foreach (var star in stars)
            {
                // Clean and isolated only: a saturated core has a flat top whose centroid is
                // meaningless, and a neighbour inside the box drags the reference as surely as it
                // drags the detector.
                if (star.HFD is < 1.5f or > 4f || star.SNR is < 20f or > 300f)
                {
                    skipped++;
                    continue;
                }
                // ImagedStar is a record STRUCT, so identity is by value here, not by reference: the
                // position comparison is what excludes the star from its own neighbour search.
                if (stars.Any(o => (o.XCentroid != star.XCentroid || o.YCentroid != star.YCentroid)
                        && MathF.Abs(o.XCentroid - star.XCentroid) < 15f
                        && MathF.Abs(o.YCentroid - star.YCentroid) < 15f))
                {
                    skipped++;
                    continue;
                }

                var cx = (int)MathF.Round(star.XCentroid);
                var cy = (int)MathF.Round(star.YCentroid);
                if (cx - box < 0 || cy - box < 0 || cx + box >= image.Width || cy + box >= image.Height)
                {
                    skipped++;
                    continue;
                }

                // One centroid per CFA phase, each over its own sub-lattice.
                var phaseDx = new List<float>();
                var phaseDy = new List<float>();
                for (var phase = 0; phase < 4; phase++)
                {
                    var py = phase >> 1;
                    var px = phase & 1;
                    float sum = 0, sumX = 0, sumY = 0;
                    for (var y = cy - box; y <= cy + box; y++)
                    {
                        if ((y & 1) != py)
                        {
                            continue;
                        }
                        for (var x = cx - box; x <= cx + box; x++)
                        {
                            if ((x & 1) != px)
                            {
                                continue;
                            }
                            var v = image[0, y, x] - bg;
                            if (v <= floor * noise)
                            {
                                continue;
                            }
                            sum += v;
                            sumX += v * x;
                            sumY += v * y;
                        }
                    }
                    if (sum > 0f)
                    {
                        phaseDx.Add(star.XCentroid - sumX / sum);
                        phaseDy.Add(star.YCentroid - sumY / sum);
                    }
                }

                if (phaseDx.Count < 4)
                {
                    skipped++;
                    continue;
                }

                dxs.Add(phaseDx.Average());
                dys.Add(phaseDy.Average());
            }

            if (dxs.Count == 0)
            {
                output.WriteLine("no clean isolated stars matched the filters");
                return;
            }

            var ox = dxs.OrderBy(v => v).ToArray();
            var oy = dys.OrderBy(v => v).ToArray();
            output.WriteLine($"\n{dxs.Count} clean isolated stars measured ({skipped} skipped)");
            output.WriteLine($"  detection MINUS CFA reference, in mosaic px:");
            output.WriteLine($"    dx mean={dxs.Average():+0.0000;-0.0000} median={ox[ox.Length / 2]:+0.0000;-0.0000}" +
                             $" p5={ox[ox.Length / 20]:+0.0000;-0.0000} p95={ox[ox.Length * 19 / 20]:+0.0000;-0.0000}");
            output.WriteLine($"    dy mean={dys.Average():+0.0000;-0.0000} median={oy[oy.Length / 2]:+0.0000;-0.0000}" +
                             $" p5={oy[oy.Length / 20]:+0.0000;-0.0000} p95={oy[oy.Length * 19 / 20]:+0.0000;-0.0000}");
            output.WriteLine("\n  A mean near 0 means the reported position IS where the light is, so an overlay");
            output.WriteLine("  drawn off-centre is the renderer's mapping, not the detector's. A mean near");
            output.WriteLine("  -0.5 or +/-1.0 would be the mosaic grid offset applied wrongly.");
        }
    }
}
