using System;
using System.Linq;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// E16c probe, not a gate: on the frame <c>TheSupportFindsTheNoiseUnderFilamentsThatFillTheFrame</c> builds (thin
/// filaments of a few sigma over the whole frame), how near the truth does the FINEST starlet scale read the noise,
/// with and without a 3 sigma clip, when its white-noise ratio is replaced by the noise field's own (what a per
/// integration calibration would supply)? Linear, unstretched, a flat sky: it isolates the band's texture leak.
/// </summary>
[Collection("Imaging")]
public class NoiseEstimatorFineBandProbe(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFinestScaleReadsTheNoiseUnderFilaments(bool warped)
    {
        const int size = 512;
        const double sigma = 0.004;
        var rng = new Random(7);
        var filaments = StretchedNoiseTests.FilamentField(size, rng, 900, 3.0 * sigma);
        var shape = warped ? NoiseField.Warped(size, size, 8, rng, 0.5) : NoiseField.White(size, size, rng);
        var shapeSpread = Robust(shape.Select(static v => (double)v).ToArray());
        // The finest scale's ratio for THIS noise, which a calibration per integration kind would supply.
        var kNoise = Robust(Fine([.. shape.Select(v => v / (float)shapeSpread)], size));

        foreach (var filled in new[] { false, true })
        {
            var frame = new float[size * size];
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (float)((filled ? filaments[i] : 0.0) + (sigma * shape[i] / shapeSpread));
            }
            var fine = Fine(frame, size);
            var plain = Robust(fine) / kNoise / sigma;
            var clipped = Clipped(fine) / kNoise / sigma;
            output.WriteLine($"warped {warped}, filaments {filled}: finest-scale k {kNoise:F3}; plain {plain:F3}x, clipped {clipped:F3}x");
        }
    }

    private static double[] Fine(float[] plane, int size)
        => [.. ATrousWaveletTransform.Decompose(plane, size, size, 1).Detail(0).ToArray().Select(static v => (double)v)];

    private static double Robust(double[] values) => 1.4826 * StatisticsHelper.MedianAndMad(values.ToArray().AsSpan()).Mad;

    private static double Clipped(double[] values)
    {
        var s = Robust(values);
        for (var pass = 0; pass < 6; pass++)
        {
            var bound = 3 * s;
            s = Robust([.. values.Where(v => Math.Abs(v) <= bound)]) / 0.9963; // a 3 sigma cut's own shrink of a Gaussian's MAD
        }
        return s;
    }
}
