using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The star detector's trigger reads the LOCAL sky (a mesh background and noise map) rather than one histogram
/// level for the frame. Compared here with the frame-wide trigger it replaced, in one process, on the real frames
/// the counts are pinned on.
/// </summary>
[Collection("Imaging")]
public sealed class StarDetectionSkyTriggerTests(ITestOutputHelper output)
{
    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? float.NaN : sorted[sorted.Length / 2];
    }

    private static (List<ImagedStar> Shared, List<ImagedStar> OnlyFirst) Split(IReadOnlyList<ImagedStar> first, IReadOnlyList<ImagedStar> second, float radius)
    {
        var shared = new List<ImagedStar>();
        var onlyFirst = new List<ImagedStar>();
        foreach (var s in first)
        {
            var match = second.Any(o => MathF.Abs(o.XCentroid - s.XCentroid) < radius && MathF.Abs(o.YCentroid - s.YCentroid) < radius);
            (match ? shared : onlyFirst).Add(s);
        }
        return (shared, onlyFirst);
    }

    [Fact]
    public async Task OnTheRealRggbFixtureTheSkyTriggerAddsStarsAndLosesNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync("RGGB_frame_bx0_by0_top_down", cancellationToken: ct);
        var mono = await image.DebayerAsync(DebayerAlgorithm.BilinearMono, cancellationToken: ct);

        var global = (await mono.DetectStarsAsync(0, 10f, 5000, 5000, 2, float.PositiveInfinity, null, SpikeGuard.PeakShare, ct, skyTrigger: false)).ToList();
        var sky = (await mono.DetectStarsAsync(0, 10f, 5000, 5000, 2, float.PositiveInfinity, null, SpikeGuard.PeakShare, ct, skyTrigger: true)).ToList();

        var (shared, onlySky) = Split(sky, global, 1.5f);
        var (_, onlyGlobal) = Split(global, sky, 1.5f);
        output.WriteLine($"global {global.Count}, sky {sky.Count}, shared {shared.Count}, sky only {onlySky.Count}, global only {onlyGlobal.Count}");
        output.WriteLine($"FWHM median: shared {Median(shared.Select(s => s.StarFWHM)):F2}, sky only {Median(onlySky.Select(s => s.StarFWHM)):F2}, global only {Median(onlyGlobal.Select(s => s.StarFWHM)):F2}");
        output.WriteLine($"SNR median: shared {Median(shared.Select(s => s.SNR)):F1}, sky only {Median(onlySky.Select(s => s.SNR)):F1}, global only {Median(onlyGlobal.Select(s => s.SNR)):F1}");
        output.WriteLine($"HFD median: shared {Median(shared.Select(s => s.HFD)):F2}, sky only {Median(onlySky.Select(s => s.HFD)):F2}, global only {Median(onlyGlobal.Select(s => s.HFD)):F2}");

        // Measured 2026-10-12: global 3,065, sky 3,616; 3,062 shared, 554 found only by the sky trigger, 3 only by
        // the global one. What it adds are stars, not noise: the same median FWHM as the shared ones (1.95 px
        // against 1.95), at a lower SNR (25 against 58), which is what fainter stars look like; noise and hot
        // pixels show in the width first.
        shared.Count.ShouldBeGreaterThanOrEqualTo((int)(0.995 * global.Count), "the sky trigger keeps what the frame-wide one found");
        onlySky.Count.ShouldBeGreaterThan(global.Count / 10, "and finds the field's fainter stars on top");
        var sharedFwhm = Median(shared.Select(s => s.StarFWHM));
        Median(onlySky.Select(s => s.StarFWHM)).ShouldBeInRange(0.85f * sharedFwhm, 1.15f * sharedFwhm, "what it adds is as wide as the stars it shares");
    }
}
