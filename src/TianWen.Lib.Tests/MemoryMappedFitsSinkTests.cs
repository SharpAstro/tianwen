using System.Collections.Generic;
using System.IO;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class MemoryMappedFitsSinkTests
{
    // Helpers mirror IntegratorTests so both suites exercise the same shapes.
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

    /// <summary>Round-trip writes through the sink and read them back via
    /// <see cref="MemoryMappedFitsSink.FinaliseAsImage"/>. Tests the bare
    /// sink API without the integrator on top.</summary>
    [Fact]
    public void GetRow_WriteThenFinalise_RoundTripsPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mmf-sink-test-{System.Guid.NewGuid():N}.bin");
        Image image;
        using (var sink = new MemoryMappedFitsSink(path, channelCount: 2, width: 5, height: 3))
        {
            sink.Shape.ShouldBe((2, 5, 3));

            for (var ch = 0; ch < 2; ch++)
            {
                for (var row = 0; row < 3; row++)
                {
                    var span = sink.GetRow(ch, row);
                    span.Length.ShouldBe(5);
                    for (var col = 0; col < 5; col++)
                    {
                        span[col] = ch * 100f + row * 10f + col;
                    }
                }
            }

            image = sink.FinaliseAsImage(BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, meta: default);
        }

        // Scratch file deleted on dispose.
        File.Exists(path).ShouldBeFalse();

        image.ChannelCount.ShouldBe(2);
        image.Width.ShouldBe(5);
        image.Height.ShouldBe(3);
        for (var ch = 0; ch < 2; ch++)
        {
            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 5; col++)
                {
                    image[ch, row, col].ShouldBe(ch * 100f + row * 10f + col);
                }
            }
        }
    }

    /// <summary>The point of step 2: run <see cref="Integrator.Integrate"/>
    /// through <see cref="ArraySink"/> (default) and
    /// <see cref="MemoryMappedFitsSink"/> with the same inputs + options;
    /// assert the resulting master is byte-identical pixel-for-pixel. This is
    /// the byte-diff guarantee the Phase 10 selector relies on when it
    /// auto-switches between sinks under memory pressure.</summary>
    [Fact]
    public void Integrator_WithMmfSink_ProducesIdenticalMasterToArraySink()
    {
        var f1 = MonoFrame(0.10f, 0.20f, 0.30f, 0.40f, 0.50f, 0.60f, 0.70f, 0.80f, 0.90f, 1.00f, 0.15f, 0.25f, 0.35f, 0.45f, 0.55f);
        var f2 = MonoFrame(0.12f, 0.22f, 0.32f, 0.42f, 0.52f, 0.62f, 0.72f, 0.82f, 0.92f, 1.00f, 0.17f, 0.27f, 0.37f, 0.47f, 0.57f);
        var f3 = MonoFrame(0.11f, 0.21f, 0.31f, 0.41f, 0.51f, 0.61f, 0.71f, 0.81f, 0.91f, 0.95f, 0.16f, 0.26f, 0.36f, 0.46f, 0.56f);
        var frames = new List<Image> { f1, f2, f3 };

        var options = new IntegrationOptions(
            Rejector: new SigmaClipRejector(),
            ApplyNormalization: true);

        // Reference path: today's ArraySink behaviour via default parameters.
        var refResult = Integrator.Integrate(frames, options);

        // Test path: identical inputs but with MemoryMappedFitsSink for the
        // master canvas. Reject sink stays ArraySink -- it's 1-channel and tiny,
        // not the target of Phase 10's mmap optimisation.
        var mmfPath = Path.Combine(Path.GetTempPath(), $"mmf-integrator-test-{System.Guid.NewGuid():N}.bin");
        var mmfSink = new MemoryMappedFitsSink(mmfPath, channelCount: 1, width: 5, height: 3);
        var mmfResult = Integrator.Integrate(frames, options, masterSink: mmfSink);

        // Sink ownership transferred -- dispose happens inside Integrate.
        File.Exists(mmfPath).ShouldBeFalse("scratch file should be deleted on sink dispose");

        // Pixel-equivalent master + rejection map + aggregate stats.
        Flatten(mmfResult.Master).ShouldBe(Flatten(refResult.Master));
        Flatten(mmfResult.RejectionMap).ShouldBe(Flatten(refResult.RejectionMap));
        mmfResult.FrameCount.ShouldBe(refResult.FrameCount);
        mmfResult.TotalRejections.ShouldBe(refResult.TotalRejections);
        mmfResult.MeanRejectionRate.ShouldBe(refResult.MeanRejectionRate);
    }
}
