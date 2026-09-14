using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Sources;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Opt-in: run the background map and the source segmentation on a real FITS master named by the
/// <c>TIANWEN_SOURCES_FITS</c> environment variable and print what they find and what they cost. Skips
/// without it, so a bare <c>dotnet test</c> stays green.
/// </summary>
[Collection("Imaging")]
public class SourceSegmentationProbe(ITestOutputHelper output)
{
    [Fact]
    public void SegmentARealMaster()
    {
        var path = Environment.GetEnvironmentVariable("TIANWEN_SOURCES_FITS");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "set TIANWEN_SOURCES_FITS to a FITS master to run this probe");

        Image.TryReadFitsFile(path, out var image, out _).ShouldBeTrue($"could not read {path}");
        var channel = image.ReferenceStarChannel;
        var sw = Stopwatch.StartNew();
        var map = BackgroundMap.Estimate(image, channel);
        var tMap = sw.Elapsed;
        sw.Restart();
        var seg = SourceSegmentation.Detect(image, channel, map);
        var tSeg = sw.Elapsed;

        var compact = seg.Segments.Count(s => s.IsCompact);
        var extended = seg.Segments.Where(s => !s.IsCompact).OrderByDescending(s => s.Area).ToArray();
        output.WriteLine($"{path}: {image.Width}x{image.Height}, channel {channel}");
        output.WriteLine($"background map {map.CellsX}x{map.CellsY} cells of {map.BlockSize} px in {tMap.TotalMilliseconds:F0} ms; global sky {map.GlobalBackground:E3}, rms {map.GlobalRms:E3}");
        output.WriteLine($"segmentation in {tSeg.TotalMilliseconds:F0} ms: {seg.Segments.Length} segments, {compact} compact, {extended.Length} extended");
        foreach (var s in extended.Take(8))
        {
            output.WriteLine($"  extended #{s.Label}: area {s.Area} at ({s.XCentroid:F0},{s.YCentroid:F0}) bbox [{s.X0},{s.Y0}]-[{s.X1},{s.Y1}] peak/rms {s.Peak / map.RmsAt(s.PeakX, s.PeakY):F1} elong {s.Elongation:F2} core {s.CoreFraction:F3} peak/mean {s.PeakToMean:F1}");
        }

        foreach (var s in seg.Segments.Where(s => s.IsCompact).OrderByDescending(s => s.Area).Take(4))
        {
            output.WriteLine($"  largest compact #{s.Label}: area {s.Area} at ({s.XCentroid:F0},{s.YCentroid:F0}) peak/rms {s.Peak / map.RmsAt(s.PeakX, s.PeakY):F1} core {s.CoreFraction:F3} peak/mean {s.PeakToMean:F1}");
        }

        var areas = seg.Segments.Where(s => s.IsCompact).Select(s => s.Area).OrderBy(a => a).ToArray();
        if (areas.Length > 0)
        {
            output.WriteLine($"compact areas p10 {areas[areas.Length / 10]} p50 {areas[areas.Length / 2]} p90 {areas[areas.Length * 9 / 10]}");
        }

        seg.Segments.Length.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The crowded-field question: how the largest segments and the counts move with the threshold and the
    /// minimum area on a real master, and how many of the star detector's stars land inside compact
    /// segments at each setting. Opt-in like the probe above.
    /// </summary>
    [Fact]
    public async Task SweepThresholdsOnARealMaster()
    {
        var path = Environment.GetEnvironmentVariable("TIANWEN_SOURCES_FITS");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "set TIANWEN_SOURCES_FITS to a FITS master to run this probe");

        Image.TryReadFitsFile(path, out var image, out _).ShouldBeTrue($"could not read {path}");
        var channel = image.ReferenceStarChannel;
        var stars = await image.FindStarsAsync(channel, snrMin: 10f, maxStars: 2000, cancellationToken: TestContext.Current.CancellationToken);
        var map = BackgroundMap.Estimate(image, channel);
        output.WriteLine($"{path}: {stars.Count} detector stars at SNR 10; sky {map.GlobalBackground:E3} rms {map.GlobalRms:E3}");
        output.WriteLine($"{"sigma",5} {"minpx",5} {"segments",8} {"compact",8} {"extended",8} {"largest",9} {"p99 area",8} {"stars in compact",16} {"ms",6}");
        foreach (var sigma in new[] { 3f, 4f, 5f })
        {
            foreach (var minPixels in new[] { 5, 9 })
            {
                var sw = Stopwatch.StartNew();
                var seg = SourceSegmentation.Detect(image, channel, map, new SourceDetectionOptions(ThresholdSigma: sigma, MinPixels: minPixels));
                sw.Stop();
                var areas = seg.Segments.Select(s => s.Area).OrderByDescending(a => a).ToArray();
                var inCompact = 0;
                foreach (var star in stars)
                {
                    var x = (int)MathF.Round(star.XCentroid);
                    var y = (int)MathF.Round(star.YCentroid);
                    if (x < 0 || y < 0 || x >= seg.Width || y >= seg.Height)
                    {
                        continue;
                    }

                    var label = seg.LabelAt(x, y);
                    if (label > 0 && seg.Segments[label - 1].IsCompact)
                    {
                        inCompact++;
                    }
                }

                output.WriteLine($"{sigma,5:F1} {minPixels,5} {seg.Segments.Length,8} {seg.Segments.Count(s => s.IsCompact),8} {seg.Segments.Count(s => !s.IsCompact),8} {areas[0],9} {areas[areas.Length / 100],8} {inCompact,8} / {stars.Count,-5} {sw.ElapsedMilliseconds,6}");
            }
        }
    }
}
