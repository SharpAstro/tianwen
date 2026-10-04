using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// E16c step 2 (docs/plans/denoiser-training.md): a master's standard-error plane is the scatter of the samples the
/// combine kept, over their count, so it has to predict the master's OWN error, the thing the half pairs measure and every
/// single-frame estimate of it got wrong on structured fields. Frames with a known truth and known noise, a different
/// noise per channel, margins only some frames reach, and outliers a clip removes.
/// </summary>
[Collection("Imaging")]
public class StandardErrorPlaneTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private const int Frames = 40;
    private const float Truth = 0.2f;

    [Fact]
    public void ItIsEveryFiniteSamplesRobustSpreadOverTheKeptCount()
    {
        float[] column = [1f, 2f, 4f, 7f, float.NaN, 100f];
        float[] mask = [1f, 1f, 1f, 1f, 1f, 0f];
        // Finite: 1, 2, 4, 7, 100, median 4, deviations 3, 2, 0, 3, 96, MAD 3: the outlier the clip removed cannot
        // inflate it. Kept and finite: four. SE = 1.4826 * 3 / sqrt 4.
        StandardErrorPlane.Of(column, mask).ShouldBe((float)(1.4826 * 3 / 2), 1e-5f);

        StandardErrorPlane.Of([5f, float.NaN, 9f], [1f, 1f, 0f]).ShouldBe(float.NaN, "one kept sample is no measurement");
        StandardErrorPlane.Of([3f, 3f, 3f], [1f, 1f, 1f]).ShouldBe(0f);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ThePlanePredictsTheMastersOwnError(bool streaming, bool clipped)
    {
        var sigmas = new[] { 0.01, 0.02, 0.005 };
        var rng = new Random(13);
        var images = new List<Image>(Frames);
        for (var f = 0; f < Frames; f++)
        {
            var planes = new float[sigmas.Length][,];
            for (var c = 0; c < sigmas.Length; c++)
            {
                var plane = new float[Size, Size];
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        // Columns 0-3 are reached by one frame only, as a dither margin on a union canvas.
                        if (x < 4 && f > 0)
                        {
                            plane[y, x] = float.NaN;
                            continue;
                        }
                        var v = Truth + (sigmas[c] * Gaussian(rng));
                        // A clipped run carries hot outliers in 3 percent of samples, which the clip must remove.
                        if (clipped && rng.NextDouble() < 0.03)
                        {
                            v += 1.0;
                        }
                        plane[y, x] = (float)v;
                    }
                }
                planes[c] = plane;
            }
            images.Add(new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()));
        }
        var options = new IntegrationOptions(Rejector: clipped ? new SigmaClipRejector() : null, ApplyNormalization: false);

        IntegrationResult result;
        var staged = streaming
            ? images.Select(i => new StagedAlignedFrame(StreamingFrameReader.InMemoryOnly(i), i.ImageMeta, i.MaxValue, i.Pedestal, null, null)).ToList()
            : null;
        try
        {
            result = staged is { } s ? StreamingIntegrator.Integrate(s, options) : Integrator.Integrate(images, options);
        }
        finally
        {
            foreach (var f in staged ?? [])
            {
                f.Dispose();
            }
        }

        output.WriteLine($"streaming {streaming}, clipped {clipped}: rejection rate {result.MeanRejectionRate:P2}");
        var standardError = result.StandardError.ShouldNotBeNull("both integrators measure it");
        standardError.ChannelCount.ShouldBe(sigmas.Length, "one plane per channel, never averaged like coverage");
        for (var c = 0; c < sigmas.Length; c++)
        {
            var z = new List<double>();
            double errorSquares = 0, planeSquares = 0;
            for (var y = 0; y < Size; y++)
            {
                standardError[c, y, 2].ShouldBe(float.NaN, "a pixel one frame reached has no scatter to measure");
                for (var x = 4; x < Size; x++)
                {
                    var se = standardError[c, y, x];
                    var error = result.Master[c, y, x] - Truth;
                    z.Add(error / se);
                    errorSquares += error * error;
                    planeSquares += se * se;
                }
            }
            // What the plane is used for: the noise over a region (it is low-passed before any model reads it), so the
            // master's error variance over the plane's, over the frame, is the measure; the per-pixel quotient's robust
            // spread is reported beside it (its RMS inflates with any per-pixel reading noise, t-like).
            var varianceRatio = Math.Sqrt(errorSquares / planeSquares);
            var sorted = z.Select(Math.Abs).OrderBy(v => v).ToArray();
            var robust = 1.4826 * sorted[sorted.Length / 2];
            output.WriteLine($"streaming {streaming}, clipped {clipped}, channel {c}: the master's error over the plane, RMS over RMS " +
                             $"{varianceRatio:F3}; per pixel robust {robust:F3}, mean {z.Average():F3}, max |z| {sorted[^1]:F1}");
            // Measured 2026-10-05: RMS over RMS 0.993 to 1.024 in every case; per pixel 0.97 to 1.07, a MAD of 40 frames
            // reading one pixel's noise to about 18 percent.
            varianceRatio.ShouldBe(1.0, 0.04, $"channel {c}");
            robust.ShouldBe(1.0, 0.10, $"channel {c}");
        }
    }

    private static double Gaussian(Random rng)
        => Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());
}
