using System;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

public class ZzScratchHotPixelTests(ITestOutputHelper output)
{
    [Fact]
    public void Scratch()
    {
        var rng = new Random(5);
        var frame = new float[200, 200];
        for (var y = 0; y < 200; y++)
            for (var x = 0; x < 200; x++)
                frame[y, x] = (float)(500 + 6 * Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble()));
        frame[100, 100] += 2000f;
        var found = new GuiderCentroidTracker().FindStars(frame);
        output.WriteLine($"found {found.Count}");
        foreach (var f in found) output.WriteLine($"{f}");
        var ok = GuiderCentroidTracker.TryCentroid(frame, 200, 200, 100, 100, 8, out var cx, out var cy, out var flux, out var snr, out var bg, out var sigma);
        output.WriteLine($"ok {ok} c=({cx},{cy}) flux {flux} snr {snr} bg {bg} sigma {sigma}");
        output.WriteLine($"LooksLikeStar {GuiderCentroidTracker.LooksLikeStar(frame, 100, 100, bg, sigma)}");
        for (var dy = -1; dy <= 1; dy++)
            output.WriteLine($"{frame[100 + dy, 99]} {frame[100 + dy, 100]} {frame[100 + dy, 101]}");
        Assert.Fail("scratch output");
    }
}
