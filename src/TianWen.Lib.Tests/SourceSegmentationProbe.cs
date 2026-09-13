using System;
using System.Diagnostics;
using System.Linq;
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
}
