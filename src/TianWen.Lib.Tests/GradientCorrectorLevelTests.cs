using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <b>A gradient corrector removes the gradient's SHAPE and leaves every plane's LEVEL alone.</b>
/// <para>
/// There are two implementations of <see cref="IGradientCorrector"/> --
/// <see cref="ClassicalBackgroundExtractor"/> and <see cref="OnnxBackgroundExtractor"/> (GraXpert) --
/// and they are chosen between at runtime by which model files are installed, so they MUST agree
/// about what the role does. They did not. The ONNX one added back one mean averaged over all three
/// channels, which lands every channel on that mean: not level preservation but background
/// NEUTRALISATION, which is a separate step belonging to the display path.
/// </para>
/// <para>
/// It was invisible for as long as it shipped because nothing downstream re-reads the level, and it
/// surfaced only as colour: a white balance solved on the INPUT (what
/// <c>InheritColorCalibration</c> and the dataset gallery's shared solve both do) became a gross
/// over-correction on the output. Measured on the SV605CC Small Magellanic Cloud master, whose
/// channel medians are genuinely R/G 0.332 and B/G 0.552, the corrected master came back at 1.004
/// and 1.003 and rendered flat red.
/// </para>
/// <para>
/// The input here is deliberately COLOURED -- three channels at different levels, which is the
/// normal state of an OSC or narrowband frame. A grey test plate cannot fail this test, which is
/// why the existing smoke tests never caught it.
/// </para>
/// </summary>
[Collection("Imaging")]
public class GradientCorrectorLevelTests
{
    private const int Size = 96;

    /// <summary>Per-channel level, with the gradient it carries, as a corrector must find it.</summary>
    private static readonly float[] ChannelLevels = [0.0400f, 0.1200f, 0.0660f];

    [Fact]
    public async Task TheClassicalCorrectorLeavesEachPlanesLevelWhereItFoundIt()
        => await AssertPreservesPerChannelLevel(new ClassicalBackgroundExtractor());

    /// <summary>
    /// The GraXpert path, gated on the model being installed so a fresh clone still runs green.
    /// This is the one that was wrong; see the class remarks.
    /// </summary>
    [Fact]
    public async Task TheGraXpertCorrectorLeavesEachPlanesLevelWhereItFoundIt()
    {
        var resolver = new ModelResolver();
        Assert.SkipUnless(resolver.TryResolve(OnnxBackgroundExtractor.ModelName, out _),
            "graxpert_bge.onnx not installed; run tools/tianwen-ai-models-fetch.ps1 to enable this test.");

        using var extractor = new OnnxBackgroundExtractor(resolver, NullLogger<OnnxBackgroundExtractor>.Instance);
        await AssertPreservesPerChannelLevel(extractor);
    }

    private static async Task AssertPreservesPerChannelLevel(IGradientCorrector corrector)
    {
        var input = ColouredPlateWithGradient();
        var before = ChannelMedians(input);

        var corrected = await corrector.EnhanceAsync(input);
        var after = ChannelMedians(corrected);

        // The RATIOS are the thing. An absolute level can move a little with the model's own
        // smoothing; what must not happen is the three collapsing onto one another, because that is
        // what silently rewrites the colour.
        for (var c = 0; c < 3; c++)
        {
            after[c].ShouldBe(before[c], tolerance: 0.25 * before[c],
                $"channel {c} level moved by more than a quarter: {before[c]:F5} -> {after[c]:F5}");
        }

        var beforeRg = before[0] / before[1];
        var afterRg = after[0] / after[1];
        var beforeBg = before[2] / before[1];
        var afterBg = after[2] / after[1];

        afterRg.ShouldBe(beforeRg, tolerance: 0.1 * beforeRg,
            $"R/G went {beforeRg:F4} -> {afterRg:F4}; a value near 1.0 means the channels were equalised");
        afterBg.ShouldBe(beforeBg, tolerance: 0.1 * beforeBg,
            $"B/G went {beforeBg:F4} -> {afterBg:F4}; a value near 1.0 means the channels were equalised");

        corrected.Release();
    }

    /// <summary>
    /// The level the ONNX corrector restores is the MEDIAN of its model per plane, the statistic the
    /// classical corrector restores, not the mean. On a smooth model the two are close and the level
    /// test above cannot tell them apart; on a skewed one (a bright nebula the model partly absorbed)
    /// they are not, and which corrector a machine has installed would then decide where the sky
    /// lands. Pinned on the helper directly, since the GraXpert weights are not on every machine.
    /// </summary>
    [Fact]
    public void TheOnnxAddBackIsTheModelsMedianPerPlane()
    {
        // 90 percent of the plane at the sky, 10 percent absorbed nebula at a hundred times it, per
        // channel at different skies: median is the sky, mean is a tenth of the way to the nebula.
        var planes = new float[3][,];
        var sky = new[] { 0.010f, 0.020f, 0.015f };
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    plane[y, x] = x < Size / 10 ? 100f * sky[c] : sky[c];
                }
            }
            planes[c] = plane;
        }
        var model = new Image(planes, BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta());

        var restored = OnnxBackgroundExtractor.ChannelMedians(model);

        for (var c = 0; c < 3; c++)
        {
            restored[c].ShouldBe(sky[c], tolerance: 1e-6f,
                $"channel {c}: the median is the sky ({sky[c]}); the mean would be {sky[c] * (0.9f + 10f):F4}");
        }
    }

    /// <summary>Three channels at genuinely different levels, each carrying the same smooth
    /// horizontal gradient, which is what the corrector is supposed to take out.</summary>
    private static Image ColouredPlateWithGradient()
    {
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var gradient = 0.25f * ChannelLevels[c] * (x / (float)(Size - 1));
                    plane[y, x] = ChannelLevels[c] + gradient;
                }
            }
            planes[c] = plane;
        }
        return new Image(planes, BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta());
    }

    private static double[] ChannelMedians(Image image)
    {
        var medians = new double[image.ChannelCount];
        for (var c = 0; c < image.ChannelCount; c++)
        {
            var span = image.GetChannelSpan(c);
            var copy = span.ToArray();
            Array.Sort(copy);
            medians[c] = copy[copy.Length / 2];
        }
        return medians;
    }
}
