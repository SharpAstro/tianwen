using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Dumps every detection plus the real peak structure around one spot, so a suspected merge seen in
    /// the viewer can be checked against what the detector actually recorded.
    /// </summary>
    /// <remarks>
    /// <para>Reads the MONO DEBAYER, not the mosaic, because that is what detection measures on: a raw
    /// RGGB frame carries CFA modulation, so half its "local maxima" are just the green photosites and
    /// any peak map built on it is fiction.</para>
    /// <para>Gated: set <c>TIANWEN_MERGE_PROBE=x,y</c> (mosaic coordinates, e.g. <c>1561,1194</c>).</para>
    /// </remarks>
    [Collection("Imaging")]
    public class StarMergeNeighbourhoodProbe(ITestOutputHelper output)
    {
        [Fact]
        public async Task ReportTheNeighbourhood()
        {
            var spec = Environment.GetEnvironmentVariable("TIANWEN_MERGE_PROBE");
            Assert.SkipUnless(!string.IsNullOrWhiteSpace(spec), "Set TIANWEN_MERGE_PROBE=x,y to run.");

            var parts = spec!.Split(',');
            var cx = float.Parse(parts[0]);
            var cy = float.Parse(parts[1]);
            const int window = 26;

            var ct = TestContext.Current.CancellationToken;
            var image = await SharedTestData.ExtractGZippedFitsImageAsync("RGGB_frame_bx0_by0_top_down", cancellationToken: ct);
            var stars = (await image.FindStarsAsync(0, 10f, 5000, cancellationToken: ct)).ToArray();

            output.WriteLine($"=== detections within {window} px of ({cx}, {cy}) ===");
            foreach (var t in stars
                .Select(s => (Star: s, D: MathF.Sqrt((s.XCentroid - cx) * (s.XCentroid - cx) + (s.YCentroid - cy) * (s.YCentroid - cy))))
                .Where(t => t.D <= window)
                .OrderBy(t => t.D))
            {
                var claim = Math.Max(1, (int)MathF.Round(0.5f * t.Star.HFD)); // Image.ClaimFactor, inlined so this probe also builds against the pre-split detector
                output.WriteLine(
                    $"  ({t.Star.XCentroid,8:F2},{t.Star.YCentroid,8:F2}) d={t.D,5:F2}px hfd={t.Star.HFD,6:F2} fwhm={t.Star.StarFWHM,5:F2}" +
                    $" snr={t.Star.SNR,8:F1} flux={t.Star.Flux,10:F0} ell={t.Star.Ellipticity:F2}" +
                    $" -> footprint={MathF.Round(Image.HfdFactor * t.Star.HFD),2:F0}px claim={claim}px");
            }

            // The mono debayer is the grid detection actually measures on.
            var mono = await image.DebayerAsync(DebayerAlgorithm.BilinearMono, cancellationToken: ct);
            var (bg, starLevel, noise, _) = mono.Background(0);
            // 3.5 sigma, NOT the histogram-derived first-pass level: that level is ~78 sigma on this
            // frame, so it hides every companion and the map would show one blob where there are two.
            var level = 3.5f * noise;
            output.WriteLine($"\n=== mono debayer peaks (bg={bg:F0} noise={noise:F1} peakThreshold=3.5sigma={level:F0}) ===");
            output.WriteLine("  strict 3x3 maxima above 3.5 sigma, brightest first:");

            var mx = (int)MathF.Round(cx - Image.BilinearMonoGridOffset);
            var my = (int)MathF.Round(cy - Image.BilinearMonoGridOffset);
            var peaks = new System.Collections.Generic.List<(int X, int Y, float V)>();
            for (var y = my - window; y <= my + window; y++)
            {
                for (var x = mx - window; x <= mx + window; x++)
                {
                    if (x < 1 || y < 1 || x >= mono.Width - 1 || y >= mono.Height - 1)
                    {
                        continue;
                    }
                    var v = mono[0, y, x];
                    if (v - bg <= level)
                    {
                        continue;
                    }
                    var isMax = true;
                    for (var dy = -1; dy <= 1 && isMax; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if ((dx | dy) != 0 && mono[0, y + dy, x + dx] >= v)
                            {
                                isMax = false;
                                break;
                            }
                        }
                    }
                    if (isMax)
                    {
                        peaks.Add((x, y, v));
                    }
                }
            }

            foreach (var pk in peaks.OrderByDescending(p => p.V))
            {
                // Back to mosaic coordinates, the space every detection is reported in.
                var px = pk.X + Image.BilinearMonoGridOffset;
                var py = pk.Y + Image.BilinearMonoGridOffset;
                var nearest = stars
                    .Select(s => (s, d: MathF.Sqrt((s.XCentroid - px) * (s.XCentroid - px) + (s.YCentroid - py) * (s.YCentroid - py))))
                    .OrderBy(t => t.d)
                    .First();
                output.WriteLine(
                    $"    peak ({px,7:F1},{py,7:F1}) +{pk.V - bg,8:F0} ADU" +
                    $" -> nearest detection ({nearest.s.XCentroid:F2},{nearest.s.YCentroid:F2}) at {nearest.d:F2}px, hfd={nearest.s.HFD:F2}");
            }

            // An intensity map of what is actually there, in log steps of the noise, so a companion
            // three magnitudes down is still visible next to a saturated core.
            output.WriteLine("\n=== mono debayer, log intensity in sigma above background ===");
            output.WriteLine("  . <3   : 3-10   - 10-30   + 30-100   * 100-300   # 300-700   @ >700 (saturated)");
            const int mapR = 16;
            output.WriteLine("        " + string.Concat(Enumerable.Range(-mapR, 2 * mapR + 1)
                .Select(dx => ((mx + dx) % 10).ToString())));
            for (var y = my - mapR; y <= my + mapR; y++)
            {
                var row = new System.Text.StringBuilder();
                for (var x = mx - mapR; x <= mx + mapR; x++)
                {
                    if ((uint)x >= (uint)mono.Width || (uint)y >= (uint)mono.Height)
                    {
                        row.Append(' ');
                        continue;
                    }
                    var sigma = (mono[0, y, x] - bg) / noise;
                    row.Append(sigma switch
                    {
                        < 3f => '.',
                        < 10f => ':',
                        < 30f => '-',
                        < 100f => '+',
                        < 300f => '*',
                        < 700f => '#',
                        _ => '@'
                    });
                }
                output.WriteLine($"  {y + Image.BilinearMonoGridOffset,6:F1} {row}");
            }

            output.WriteLine("\n  recorded centroids in this window, marked on the same grid:");
            foreach (var t in stars.Where(s => MathF.Abs(s.XCentroid - cx) <= mapR && MathF.Abs(s.YCentroid - cy) <= mapR))
            {
                output.WriteLine($"    ({t.XCentroid:F2}, {t.YCentroid:F2}) hfd={t.HFD:F2} ell={t.Ellipticity:F2} fwhm={t.StarFWHM:F2}");
            }

            var med = stars.Select(s => s.HFD).Order().ToArray();
            output.WriteLine($"\n  frame HFD median {med[med.Length / 2]:F2}, p95 {med[med.Length * 19 / 20]:F2}, max {med[^1]:F2}");
        }
    }
}
