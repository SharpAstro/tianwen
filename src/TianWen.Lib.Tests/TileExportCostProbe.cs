using System;
using System.Diagnostics;
using System.Linq;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where one sub's preparation for the tile export spends its time, at a real sensor's size (QHY294, 4164 x 2796,
/// three channels) with the NaN canvas edge a warped sub has. The export prepares nearly every sub of a session
/// (eight per cell over some 300 cells), so a second here is minutes a session. Run in Release, gated:
/// <c>TIANWEN_EXPORT_COST_PROBE=1</c>.
/// </summary>
[Collection("Imaging")]
public class TileExportCostProbe(ITestOutputHelper output)
{
    [Fact]
    public void ReportWhereASubsPreparationGoes()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("TIANWEN_EXPORT_COST_PROBE") != "1", "Set TIANWEN_EXPORT_COST_PROBE=1");
        const int w = 4164, h = 2796, channels = 3;
        var rng = new Random(11);
        var data = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var p = new float[h, w];
            var sky = 0.012f * (c + 1);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    // A warped sub's canvas: a rotated footprint, NaN outside it.
                    var inside = (x + (0.08 * y)) > 60 && (x + (0.08 * y)) < w + 60 && (y - (0.08 * x)) > -300 && (y - (0.08 * x)) < h - 360;
                    p[y, x] = inside ? sky + (float)((rng.NextDouble() - 0.5) * 0.004) : float.NaN;
                }
            }
            data[c] = p;
        }
        var frame = new Image(data, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });

        // Every step is timed as its best of three after a warm-up, so the numbers are the steady state a bake is
        // in after its first sub, not the JIT and the thread pool starting up.
        static double Best(Action step)
        {
            step();
            var best = double.MaxValue;
            for (var i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                step();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        var unit = DatasetTileExporter.ToUnitRange(frame);
        var absent = unit.AbsentPixels();
        var (_, applied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(unit, absent);
        Assert.True(applied && origMin is not null && balances is not null);
        var stretches = Enumerable.Range(0, channels).Select(c => new StretchedNoise.ChannelStretch(balances[c], origMin[c])).ToArray();
        Assert.True(StretchedNoise.TryEstimateCalibration(unit, stretches, absent, out _));

        var tUnit = Best(() => DatasetTileExporter.ToUnitRange(frame));
        var tAbsent = Best(() => unit.AbsentPixels());
        var tStretch = Best(() => ChunkedNafnetRunner.ApplyInputStretch(unit, absent));
        var tEstimate = Best(() => StretchedNoise.TryEstimateCalibration(unit, stretches, absent, out _));

        var plane = unit.GetChannelSpan(0).ToArray().Select(v => float.IsNaN(v) ? 0f : v).ToArray();
        var tBlurFast = Best(() => Image.SeparableGaussianBlur(plane, w, h, StretchedNoise.EstimateHighPassSigmaPx));
        var tBlurPlain = Best(() => GaussianBlurParityTests.PlainLoopBlur(plane, w, h, StretchedNoise.EstimateHighPassSigmaPx));

        var tile = Enumerable.Range(0, channels).Select(_ => Enumerable.Range(0, 256 * 256).Select(_ => 0.25f + (float)((rng.NextDouble() - 0.5) * 0.02)).ToArray()).ToArray();
        var calibration = new LinearDegradation.NoiseCalibration(0, 0.012, 1e-4, 1);
        var tPlane = Best(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                _ = StretchedNoise.Plane(tile, 256, 256, stretches, calibration, 1.0);
            }
        }) / 20;

        output.WriteLine($"per sub ({w}x{h}x{channels}): to unit {tUnit:F0} ms, absent {tAbsent:F0} ms, stretch {tStretch:F0} ms, " +
                         $"noise estimate {tEstimate:F0} ms");
        output.WriteLine($"one channel's sigma-4 blur: {tBlurFast:F0} ms now, {tBlurPlain:F0} ms the plain loop ({tBlurPlain / tBlurFast:F1}x)");
        output.WriteLine($"one tile's plane: {tPlane:F2} ms");
    }
}
