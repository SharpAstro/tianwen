using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A capture's statistics saved once read back whole (<see cref="PlanetaryCaptureStatistics.SaveAsync"/>,
/// <see cref="PlanetaryCaptureStatistics.TryLoadAsync"/>), wherever a read's buffer happens to end inside them: #1281's
/// 2,600-frame statistics were saved and then could not be read back at frame limb 208 of 650.
/// </summary>
public sealed class CaptureStatisticsFileTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    // A frame limb as a real capture's: a converged fit whose standard errors are unmeasured (NaN, a named literal in the file).
    private static FrameLimb Limb(int frame) => new FrameLimb(frame, 0.03 + (frame * 1e-5), 6.9 + (frame * 1e-4), new LimbFit(
        158.89 + (frame * 1e-3), 161.31, 119.34, 22.31, 0.989, 1.666, 0.671, 0.0062, 1, 0.0123, 39573,
        [.. Enumerable.Repeat(double.NaN, 13)], 12, true, 0.202, -0.474, 0.0022, 202.31, 0.4995, 6.53));

    private static CaptureStatistics Statistics(int limbs) => new CaptureStatistics(
        limbs * 4, 29.9, 7, 158.6, 158.8, 114.6, [], [], [], [], 0.35, 0.01, 0.02, [], 0.023, 0.0026, 14.0, 13.1,
        null, null, [.. Enumerable.Range(0, limbs).Select(i => Limb(i * 4))], 0.36, 1.1, 0.05, 0, [0.38, 0.16, 0.021, double.NaN],
        new WarpStatistics(163, 1.06, 3, WarpLengthBound.AtMost, double.NaN, []), [], [0.97, 0.99, 1, 1.02, 1.06], 0.84,
        [new BandNoise(1, 0.57, 4.87)], new CameraEstimate(255, 1.25, 0.65, 113, 1.09, 0.54, 1.09));

    [Fact(Timeout = 120_000)]
    public async Task StatisticsSavedAreReadBackWhereverAReadsBufferEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var statistics = Statistics(40);
        var path = Path.Combine(_folders.Create("capstats").FullName, "capture.real");
        // The key leads the file, so lengthening it walks every later byte past the read's buffer boundaries: across the
        // longest frame limb's length, each of its bytes lies at a boundary once.
        var oneLimb = System.Text.Json.JsonSerializer.Serialize(statistics.FrameLimbs[0], PlanetaryStatisticsJsonContext.Default.FrameLimb).Length;
        for (var pad = 0; pad <= oneLimb + 16; pad++)
        {
            var key = new string('k', pad + 1);
            await PlanetaryCaptureStatistics.SaveAsync(statistics, key, path, ct);
            var read = await PlanetaryCaptureStatistics.TryLoadAsync(path, key, ct);
            read.ShouldNotBeNull($"padding {pad}");
            read.FrameLimbs.Length.ShouldBe(40, $"padding {pad}");
            read.FrameLimbs[39].Fit.ShouldNotBeNull();
            read.FrameLimbs[39].Fit!.Value.CenterX.ShouldBe(statistics.FrameLimbs[39].Fit!.Value.CenterX);
            read.FrameLimbs[39].Fit!.Value.StandardErrors.ShouldAllBe(e => double.IsNaN(e));
            double.IsNaN(read.Halo[3]).ShouldBeTrue();
        }
    }
}
