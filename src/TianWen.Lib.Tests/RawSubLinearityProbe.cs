using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.IO;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Env-gated: whether a sensor's response stays linear up to the clip, read off single raw subs (WFC3's breakpoint method,
/// docs/architecture/star-removal-literature/saturation-in-stacks.md). Each isolated star's peak (the brightest pixel within
/// 1.5 px of its maximum, above the local sky) is set against the light in its 2.5 to 5 px annulus, which stays far below
/// the clip for any star the test reaches. A linear sensor keeps peak over annulus constant until the peak meets the clip;
/// a soft one bends it down before. Separates a sensor's own knee from one the stack makes, which a master cannot.
/// Writes one row per star: the sub, the position, the sky, the peak, the annulus light, the 5 to 8 px annulus light, and
/// the frame's brightest pixel (its clip where any star reached it).
/// <para>Set <c>TIANWEN_LINEARITY_SUBS</c> (a folder of raw subs, not recursive, or semicolon-separated files),
/// <c>TIANWEN_LINEARITY_OUT</c> (the CSV) and optionally <c>TIANWEN_LINEARITY_MAX</c> (subs at most, default 12).</para>
/// </summary>
[Collection("Imaging")]
public sealed class RawSubLinearityProbe(ITestOutputHelper output)
{
    private const int SkyInner = 10;
    private const int SkyOuter = 14;

    [Fact]
    public async Task ReadEachStarsPeakAgainstItsAnnulusInSingleSubs()
    {
        var subs = Environment.GetEnvironmentVariable("TIANWEN_LINEARITY_SUBS");
        var outPath = Environment.GetEnvironmentVariable("TIANWEN_LINEARITY_OUT");
        Assert.SkipWhen(subs is null || outPath is null, "TIANWEN_LINEARITY_* not set");
        var max = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_LINEARITY_MAX"), out var m) ? m : 12;
        var ct = TestContext.Current.CancellationToken;
        var files = Directory.Exists(subs)
            ? FileEnumeration.EnumerateFiles(subs, [".fits", ".fit"], recursive: false).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : subs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        // Spread over the session, not its first few subs.
        var step = Math.Max(1, files.Count / max);
        var chosen = files.Where((_, i) => i % step == 0).Take(max).ToList();

        var csv = new StringBuilder("sub,x,y,sky,sigma,peak,annulus,outer,frame_max,gain,offset,sensor\n");
        foreach (var file in chosen)
        {
            Assert.True(Image.TryReadFitsFile(file, out var image), $"{file}: unreadable");
            var (_, width, height) = image.Shape;
            var plane = image.GetChannelSpan(0).ToArray();
            var frameMax = plane.Where(float.IsFinite).Max();
            var sample = plane.Where((v, i) => i % 7 == 0 && float.IsFinite(v)).ToArray();
            var (median, mad) = StatisticsHelper.UpperMedianAndMad(sample);
            var sigma = Math.Max(1e-6, 1.4826 * mad);
            var meta = image.ImageMeta;

            // Local maxima over 3 x 3 standing 15 sigma above the frame's median, the brightest within 10 px of each other only.
            var peaks = new List<(int X, int Y, float V)>();
            for (var y = SkyOuter + 1; y < height - SkyOuter - 1; y++)
            {
                for (var x = SkyOuter + 1; x < width - SkyOuter - 1; x++)
                {
                    var v = plane[(y * width) + x];
                    if (!(v > median + (15 * sigma)))
                    {
                        continue;
                    }
                    var top = true;
                    for (var dy = -1; dy <= 1 && top; dy++)
                    {
                        for (var dx = -1; dx <= 1 && top; dx++)
                        {
                            var w = plane[((y + dy) * width) + x + dx];
                            // A flat top's first pixel in raster order stands for it.
                            top = (dx == 0 && dy == 0) || w < v || (w == v && (dy > 0 || (dy == 0 && dx > 0)));
                        }
                    }
                    // A raw sub is uncalibrated: a hot pixel is a maximum with dark neighbours, a star at 1.5 px FWHM or more
                    // lights its four at a fifth of its peak or more.
                    var neighbours = (plane[(y * width) + x - 1] + plane[(y * width) + x + 1] + plane[((y - 1) * width) + x] + plane[((y + 1) * width) + x]) / 4f;
                    if (top && neighbours - median > 0.2f * (v - median))
                    {
                        peaks.Add((x, y, v));
                    }
                }
            }
            var isolated = peaks.Where(p => !peaks.Any(o => o != p && Math.Abs(o.X - p.X) <= 12 && Math.Abs(o.Y - p.Y) <= 12 && o.V >= 0.05f * p.V)).ToList();

            foreach (var (px, py, _) in isolated)
            {
                var ring = new List<float>();
                double peak = double.NegativeInfinity, annulus = 0, outer = 0;
                for (var y = py - SkyOuter; y <= py + SkyOuter; y++)
                {
                    for (var x = px - SkyOuter; x <= px + SkyOuter; x++)
                    {
                        var d = Math.Sqrt(((x - px) * (x - px)) + ((y - py) * (y - py)));
                        var v = plane[(y * width) + x];
                        if (d >= SkyInner && d <= SkyOuter)
                        {
                            ring.Add(v);
                        }
                    }
                }
                var sky = StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(ring), ring.Count / 2);
                for (var y = py - 8; y <= py + 8; y++)
                {
                    for (var x = px - 8; x <= px + 8; x++)
                    {
                        var d = Math.Sqrt(((x - px) * (x - px)) + ((y - py) * (y - py)));
                        var v = plane[(y * width) + x] - sky;
                        if (d <= 1.5)
                        {
                            peak = Math.Max(peak, v);
                        }
                        else if (d >= 2.5 && d < 5.0)
                        {
                            annulus += v;
                        }
                        else if (d >= 5.0 && d < 8.0)
                        {
                            outer += v;
                        }
                    }
                }
                csv.Append(CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(file)},{px},{py},{sky:G6},{sigma:G4},{peak:G6},{annulus:G6},{outer:G6},{frameMax:G6},{meta.Gain},{meta.Offset},{meta.SensorType}\n");
            }
            output.WriteLine($"{Path.GetFileName(file)}: {isolated.Count} isolated stars of {peaks.Count}, frame max {frameMax}, gain {meta.Gain}, offset {meta.Offset}");
            image.Release();
        }
        await File.WriteAllTextAsync(outPath, csv.ToString(), ct);
    }
}
