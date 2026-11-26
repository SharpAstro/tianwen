using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Workbench;
using Xunit;
using static TianWen.Lib.Tests.UnitScaleFakes;

namespace TianWen.Lib.Tests;

/// <summary>
/// The enhance retrofit's second step (the owner's choices, 2026-11-26): the enhance and the single steps run one routine
/// per step. The deblur keeps a lifted star's light, the star removal is the guarded split, the denoise is the gated passes,
/// and SCNR on the stars is a step of its own.
/// </summary>
[Collection("Imaging")]
public sealed class EnhanceOneRoutinePerStepTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public async Task TheEnhancesDeblurKeepsALiftedStarsLightAsImageDeblurDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        // The whole-frame program: the deblur, then a denoiser with the ONNX models' guard (it refuses a peak above 1.5).
        var pipeline = new SharpenPipeline(denoiser: new GuardedDenoiser(), deblurrer: new LiftingDeblurrer(3.5f));

        var result = await pipeline.ProcessAsync(new SharpenRequest(Frame(0.855f), [new DeblurStep(), new DenoiseFrameStep()]),
            EnhanceOptions.Default, cancellationToken: ct);

        var final = result.Final.ShouldNotBeNull();
        output.WriteLine($"final peak {final.MaxValue}, UNITREF {final.ImageMeta.UnitReferred}");
        // At the frame's own peak the deblurrer lifted 0.855 to 2.97 and stopped at its ceiling, 1.0: the star lost its light.
        final.MaxValue.ShouldBeGreaterThan(2f, "the deblur went in with headroom and the star kept its light");
        final.IsUnitReferred.ShouldBeTrue("and the result says it is on the frame's unit scale");
    }

    [Fact]
    public async Task TheEnhancesStarRemovalIsTheGuardedSplit()
    {
        var ct = TestContext.Current.CancellationToken;
        var frame = Frame(0.855f);
        // A remover that overshoots its input, as RC-Astro's StarXTerminator does a little around a star (#2433).
        var pipeline = new SharpenPipeline(starRemover: new OvershootingRemover());

        var result = await pipeline.ProcessAsync(new SharpenRequest(frame, [new RemoveStarsStep()]), EnhanceOptions.Default, cancellationToken: ct);

        var starless = result.Starless.ShouldNotBeNull();
        var stars = result.StarsOnly.ShouldNotBeNull();
        var input = frame.GetChannelSpan(0);
        var plate = starless.GetChannelSpan(0);
        var taken = stars.GetChannelSpan(0);
        var over = 0;
        var negative = 0;
        for (var i = 0; i < input.Length; i++)
        {
            over += plate[i] > input[i] + 1e-6f ? 1 : 0;
            negative += taken[i] < -1e-6f ? 1 : 0;
        }

        over.ShouldBe(0, "the plate is held at or under its input, as image remove-stars holds it");
        negative.ShouldBe(0, "so the stars, the input less the plate, are never negative");
    }

    [Fact]
    public async Task TheSingleDenoiseRunsTheEnhancesGatedPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        // Each pass takes half the noise off, more than the gate's 35 %, so every pass is kept up to the most there are.
        var halving = new DenoisePassGateTests.ScalingDenoiser(0.5f, 0.5f, 0.5f, 0.5f);
        await NarrowbandCombination.DenoiseAsync([DenoisePassGateTests.Plate()], halving, EnhanceOptions.Default, ct);
        halving.Calls.ShouldBe(SharpenPipeline.MaxDenoisePasses, "image denoise runs the passes the enhance runs, where it ran one");

        // A second pass that takes a fifth off is run, measured, and discarded, and the passes stop.
        var weak = new DenoisePassGateTests.ScalingDenoiser(0.5f, 0.8f, 0.5f);
        await NarrowbandCombination.DenoiseAsync([DenoisePassGateTests.Plate()], weak, EnhanceOptions.Default, ct);
        weak.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task TheScnrStepRunsOnBalancedColourAndRefusesAnyOtherPlate()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("scnr-").FullName;
        var balanced = Path.Combine(folder, "balanced.fits");
        var unbalanced = Path.Combine(folder, "unbalanced.fits");
        var mono = Path.Combine(folder, "mono.fits");
        GreenStars(applied: true).WriteToFitsFile(balanced);
        GreenStars(applied: false).WriteToFitsFile(unbalanced);
        Frame(0.855f).WriteToFitsFile(mono);
        var scnr = new ScnrOperation();
        var settings = new ScnrSettings();

        var done = await scnr.RunAsync(settings, [OperationInput.FromFile(balanced)], Path.Combine(folder, "scnr.fits"), output.WriteLine, ct);
        var notBalanced = await scnr.RunAsync(settings, [OperationInput.FromFile(unbalanced)], Path.Combine(folder, "no.fits"), output.WriteLine, ct);
        var notColour = await scnr.RunAsync(settings, [OperationInput.FromFile(mono)], Path.Combine(folder, "mono-scnr.fits"), output.WriteLine, ct);

        done.Failure.ShouldBe(OperationFailure.None, done.Message);
        Image.TryReadFitsFile(Path.Combine(folder, "scnr.fits"), out var result).ShouldBeTrue();
        result.ShouldNotBeNull();
        result.GetChannelSpan(1)[0].ShouldBe(0.2f, 1e-6f, "green is taken down to the mean of red and blue, 0.2, from 0.5");
        notBalanced.Failure.ShouldBe(OperationFailure.Input);
        notBalanced.Message.ShouldContain("not balanced");
        notColour.Failure.ShouldBe(OperationFailure.Input);
        notColour.Message.ShouldContain("not colour");
        scnr.Refusal(new ScnrSettings { Mode = ScnrMode.None }).ShouldNotBeNull();
        scnr.Refusal(new ScnrSettings { Amount = 0f }).ShouldNotBeNull();
        scnr.Refusal(settings).ShouldBeNull();
    }

    // A mono frame on a unit scale: a sky of 0.0068 with a little noise and one star peaking at `peak`.
    private static Image Frame(float peak)
    {
        const int size = 128;
        const float sky = 0.0068f;
        var plane = new float[size, size];
        var random = new Random(3);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - 64;
                var dy = y - 64;
                plane[y, x] = sky + (0.0002f * (float)(random.NextDouble() - 0.5)) + ((peak - sky) * MathF.Exp(-((dx * dx) + (dy * dy)) / 8f));
            }
        }

        return new Image([plane], BitDepth.Float32, peak, 0f, 0f, new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
    }

    // A colour plate of green stars: red and blue 0.2, green 0.5, with the colour balanced in its pixels or not.
    private static Image GreenStars(bool applied)
    {
        const int size = 16;
        float[][,] planes = [new float[size, size], new float[size, size], new float[size, size]];
        float[] level = [0.2f, 0.5f, 0.2f];
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    planes[c][y, x] = level[c];
                }
            }
        }

        return new Image(planes, BitDepth.Float32, 0.5f, 0.2f, 0f,
            new ImageMeta { ColourCalibration = new ColourCalibration(1f, 1f, 1f, ColourCalibrationSource.Spcc, Applied: applied) });
    }

    /// <summary>A remover whose plate stands a little over its input everywhere, as a remover's can around a star.</summary>
    private sealed class OvershootingRemover : IStarRemover
    {
        public string Name => "Test/Overshooting";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input.Affine(1.1, 0.01));
    }
}
