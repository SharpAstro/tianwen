using System;
using System.Collections.Generic;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class IntegratorTests
{
    // 5x3 mono test frames (15 floats). Same shape across all tests so the
    // integrator's Parallel.For-over-rows path gets exercised at every row count.
    private static Image MonoFrame(params float[] values)
    {
        values.Length.ShouldBe(15);
        var arr = new float[3, 5];
        for (var i = 0; i < 15; i++) arr[i / 5, i % 5] = values[i];
        return Image.FromChannel(arr, maxValue: 1f, minValue: 0f);
    }

    private static float[] Flatten(Image image, int channel = 0)
    {
        var arr = new float[image.Height * image.Width];
        for (var h = 0; h < image.Height; h++)
            for (var w = 0; w < image.Width; w++)
                arr[h * image.Width + w] = image[channel, h, w];
        return arr;
    }

    private static float[] Constant(float v) => new[] { v, v, v, v, v, v, v, v, v, v, v, v, v, v, v };

    [Fact]
    public void Integrate_TwoIdenticalFrames_NoNormalization_MeanCombine()
    {
        // Two identical frames, no normalization, no rejection — output is the input.
        var f = MonoFrame(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 0.15f, 0.25f, 0.35f, 0.45f, 0.55f);
        var frames = new List<Image> { f, f };

        var result = Integrator.Integrate(frames,
            new IntegrationOptions(ApplyNormalization: false));

        result.FrameCount.ShouldBe(2);
        result.TotalRejections.ShouldBe(0);
        Flatten(result.Master).ShouldBe(Flatten(f), tolerance: 1e-6);
    }

    [Fact]
    public void Integrate_MeanOfThreeFrames_PixelwiseAverage()
    {
        var f1 = MonoFrame(Constant(0.1f));
        var f2 = MonoFrame(Constant(0.2f));
        var f3 = MonoFrame(Constant(0.3f));

        var result = Integrator.Integrate(new List<Image> { f1, f2, f3 },
            new IntegrationOptions(ApplyNormalization: false));

        foreach (var v in Flatten(result.Master))
        {
            v.ShouldBe(0.2f, tolerance: 1e-6f); // (0.1+0.2+0.3)/3
        }
    }

    [Fact]
    public void Integrate_WithSigmaClipRejector_OutlierExcluded()
    {
        // Five frames: four at 0.5 with tiny noise + one at 99 (cosmic ray).
        // Mean combiner without rejector: ~(4*0.5 + 99)/5 = 20.2 — useless.
        // With SigmaClip rejector: 99 dropped, mean = 0.5.
        var f1 = MonoFrame(Constant(0.50f));
        var f2 = MonoFrame(Constant(0.51f));
        var f3 = MonoFrame(Constant(0.49f));
        var f4 = MonoFrame(Constant(0.50f));
        var outlier = MonoFrame(Constant(99.0f));

        var result = Integrator.Integrate(
            new List<Image> { f1, f2, f3, f4, outlier },
            new IntegrationOptions(
                Rejector: new SigmaClipRejector(3f, 3f, 5),
                ApplyNormalization: false));

        // Master should be ~0.5 (no contamination from 99)
        foreach (var v in Flatten(result.Master))
        {
            v.ShouldBeInRange(0.49f, 0.51f);
        }
        // Each output pixel rejected 1 of 5 frames -> rejection rate 1/5 = 0.2
        foreach (var v in Flatten(result.RejectionMap))
        {
            v.ShouldBe(0.2f, tolerance: 1e-5f);
        }
        result.TotalRejections.ShouldBe(15L); // 1 rejection per output pixel x 15 pixels
        result.MeanRejectionRate.ShouldBe(0.2, tolerance: 1e-6);
    }

    [Fact]
    public void Integrate_WithNormalization_DifferentBrightnessFramesAlignAtTarget()
    {
        // Two frames at different overall brightness, but same spatial pattern
        // (linear gradient). With normalization to target=0.5, the median of
        // each frame lands at 0.5 -> the median pixel in the master sits at
        // 0.5, NOT at the unweighted average of the inputs.
        var bright = MonoFrame(0.50f, 0.55f, 0.60f, 0.65f, 0.70f, 0.50f, 0.55f, 0.60f, 0.65f, 0.70f, 0.50f, 0.55f, 0.60f, 0.65f, 0.70f);
        var dim = MonoFrame(0.10f, 0.11f, 0.12f, 0.13f, 0.14f, 0.10f, 0.11f, 0.12f, 0.13f, 0.14f, 0.10f, 0.11f, 0.12f, 0.13f, 0.14f);

        var result = Integrator.Integrate(new List<Image> { bright, dim },
            new IntegrationOptions(NormalizationTarget: 0.5f));

        // The median index in each frame (index 7 = position [1,2]) should land at 0.5
        // after normalization. Both frames now agree on that pixel -> master[1,2] = 0.5.
        result.Master[0, 1, 2].ShouldBe(0.5f, tolerance: 1e-4f);
    }

    [Fact]
    public void Integrate_NaNBorders_HandledByCombiner()
    {
        // Synthetic post-warp scenario: one frame has NaN at pixel (0,0)
        // (out-of-source after warp). Mean combiner should compute (0.3+0.4)/2
        // for that pixel, treating NaN as if it had keepMask=0.
        var clean1 = MonoFrame(Constant(0.3f));
        var clean2 = MonoFrame(Constant(0.4f));

        var nanPixels = Constant(0.3f);
        nanPixels[0] = float.NaN;
        var withNaN = MonoFrame(nanPixels);

        var result = Integrator.Integrate(new List<Image> { clean1, clean2, withNaN },
            new IntegrationOptions(ApplyNormalization: false));

        // Pixel (0,0): NaN excluded -> mean = (0.3 + 0.4) / 2 = 0.35
        result.Master[0, 0, 0].ShouldBe(0.35f, tolerance: 1e-6f);
        // Other pixels: all 3 contribute -> mean = (0.3 + 0.4 + 0.3) / 3 = 0.333...
        result.Master[0, 1, 1].ShouldBe(0.3333333f, tolerance: 1e-5f);
    }

    [Fact]
    public void Integrate_NoRejector_AllFramesContribute()
    {
        // Without a rejector, the cosmic-ray outlier contaminates the mean —
        // this is the "why rejection matters" baseline test.
        var f1 = MonoFrame(Constant(0.5f));
        var outlier = MonoFrame(Constant(99.0f));

        var result = Integrator.Integrate(new List<Image> { f1, outlier },
            new IntegrationOptions(Rejector: null, ApplyNormalization: false));

        result.TotalRejections.ShouldBe(0);
        result.Master[0, 0, 0].ShouldBe(49.75f, tolerance: 1e-4f); // (0.5 + 99) / 2
    }

    [Fact]
    public void Integrate_ShapeMismatch_Throws()
    {
        var f1 = MonoFrame(new float[15]);
        var f2 = Image.FromChannel(new float[2, 3]);

        Should.Throw<ArgumentException>(() => Integrator.Integrate(new List<Image> { f1, f2 }));
    }

    [Fact]
    public void Integrate_EmptyList_Throws()
    {
        Should.Throw<ArgumentException>(() => Integrator.Integrate(Array.Empty<Image>()));
    }

    [Fact]
    public void Integrate_MultiChannel_EachChannelIntegratedIndependently()
    {
        // 3-channel image: R=0.1, G=0.5, B=0.9 across all pixels. Two frames.
        // Each channel integrates independently; master should preserve the colors.
        var ch0 = new float[3, 5];
        var ch1 = new float[3, 5];
        var ch2 = new float[3, 5];
        for (var h = 0; h < 3; h++)
            for (var w = 0; w < 5; w++)
            {
                ch0[h, w] = 0.1f;
                ch1[h, w] = 0.5f;
                ch2[h, w] = 0.9f;
            }
        var rgb = new Image([ch0, ch1, ch2], BitDepth.Float32, 1f, 0f, 0f, default);

        var result = Integrator.Integrate(new List<Image> { rgb, rgb },
            new IntegrationOptions(ApplyNormalization: false));

        result.Master.ChannelCount.ShouldBe(3);
        result.Master[0, 0, 0].ShouldBe(0.1f, tolerance: 1e-6f);
        result.Master[1, 0, 0].ShouldBe(0.5f, tolerance: 1e-6f);
        result.Master[2, 0, 0].ShouldBe(0.9f, tolerance: 1e-6f);
    }
}
