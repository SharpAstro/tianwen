using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Workbench;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A file says its samples are on a unit scale (<c>UNITREF</c>, <see cref="ImageMeta.UnitReferred"/>), so an image whose
/// deblurred star stands above 2.0 is still read as unit-referred, and its stars are found (#2504).
/// </summary>
/// <remarks>
/// The numbers are 10P/Tempel 2's: a master peaking at 0.855 on a sky of 0.0068 came back from BlurXTerminator peaking at
/// 2.995, which judged by its peak alone is ADU. Read that way its histogram, its background and its star list were empty,
/// and every step after it in a chain divided the frame by 2.995.
/// </remarks>
[Collection("Imaging")]
public sealed class UnitReferredCardTests(ITestOutputHelper output) : IDisposable
{
    private const int Width = 512;
    private const int Height = 512;
    private const int PlantedStars = 40;
    private const float Sky = 0.0068f;
    private const float LiftedPeak = 2.995f;

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public async Task ADeblurredStarAboveTwoStaysUnitReferredWhenTheFileSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("unitref-").FullName;
        var declared = Path.Combine(folder, "declared.fits");
        var silent = Path.Combine(folder, "silent.fits");
        StarField(LiftedPeak, Meta() with { UnitReferred = true }).WriteToFitsFile(declared);
        StarField(LiftedPeak, Meta()).WriteToFitsFile(silent);

        Image.TryReadFitsFile(declared, out var read).ShouldBeTrue();
        Image.TryReadFitsFile(silent, out var unsaid).ShouldBeTrue();
        read.ShouldNotBeNull();
        unsaid.ShouldNotBeNull();
        output.WriteLine($"declared: peak {read.MaxValue}, UNITREF {read.ImageMeta.UnitReferred}; silent: peak {unsaid.MaxValue}");

        // The peak says what the peak is, and only the card says the scale.
        read.MaxValue.ShouldBe(LiftedPeak, 1e-3f);
        read.HasUnitScalePeak.ShouldBeFalse("a peak of 2.995 is not a unit-scale peak, and the property still says so");
        read.ImageMeta.UnitReferred.ShouldBeTrue("the card round-trips");
        read.IsUnitReferred.ShouldBeTrue("the file says its samples are on a unit scale");
        read.IsUnitScaledFloat.ShouldBeTrue("so the histogram bins it as [0, 1] data");
        unsaid.IsUnitReferred.ShouldBeFalse("premise: without the card the peak decides, and judges this frame ADU (#2504)");

        var stars = await read.FindStarsAsync(0, snrMin: 10f, minStars: 20, logger: new XunitLogger(output), cancellationToken: ct);
        output.WriteLine($"found {stars.Count} of {PlantedStars + 1} stars");
        stars.Count.ShouldBeGreaterThan(PlantedStars / 2, "a frame read as unit-referred finds its stars; read as ADU it found none");
    }

    [Fact]
    public async Task TheDeblurStepWritesTheCardAndIsVersionTwo()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("unitref-deblur-").FullName;
        var master = Path.Combine(folder, "master.fits");
        var result = Path.Combine(folder, "deblurred.fits");
        StarField(0.855f, Meta()).WriteToFitsFile(master);
        var deblur = new DeblurOperation(new LiftingDeblurrer(3.5f));

        var outcome = await deblur.RunJointAsync(new DeblurSettings(), [OperationInput.FromFile(master)], [new StepOutputs(result)], output.WriteLine, ct);

        outcome.Failure.ShouldBe(OperationFailure.None, outcome.Message);
        deblur.Description.Version.ShouldBe(2, "a version 1 result, which every reader took for ADU, runs again");
        Image.TryReadFitsFile(result, out var read).ShouldBeTrue();
        read.ShouldNotBeNull();
        read.MaxValue.ShouldBeGreaterThan(2f, "premise: the lifted star stands above where the peak alone says unit scale");
        read.ImageMeta.UnitReferred.ShouldBeTrue("the file the step wrote carries UNITREF");
        SkyOf(read).ShouldBe(Sky, Sky * 0.05f, "the deblur leaves the sky on its input's scale");
    }

    [Theory]
    [InlineData(GradientBackend.Classical)]
    [InlineData(GradientBackend.Graxpert)]
    public async Task TheGradientStepOnADeblurredFileKeepsItsScaleAndTheCard(GradientBackend backend)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("unitref-flatten-").FullName;
        var deblurred = Path.Combine(folder, "deblurred.fits");
        var result = Path.Combine(folder, "flattened.fits");
        StarField(LiftedPeak, Meta() with { UnitReferred = true }).WriteToFitsFile(deblurred);
        // A model that refuses a peak above 1.5, as GraXpert's background model and TianWen's ONNX models do.
        var flatten = new GradientOperation(new ClassicalBackgroundExtractor(), new GuardedCorrector());

        var outcome = await flatten.RunAsync(new GradientSettings { Backend = backend }, [OperationInput.FromFile(deblurred)], result, output.WriteLine, ct);

        outcome.Failure.ShouldBe(OperationFailure.None, outcome.Message);
        Image.TryReadFitsFile(result, out var read).ShouldBeTrue();
        read.ShouldNotBeNull();
        output.WriteLine($"{backend}: peak {read.MaxValue}, sky {SkyOf(read)}, UNITREF {read.ImageMeta.UnitReferred}");
        read.ImageMeta.UnitReferred.ShouldBeTrue("the step carries its input's card onto its result");
        read.MaxValue.ShouldBeGreaterThan(2f, "the lifted star is still on the input's scale");
        SkyOf(read).ShouldBe(Sky, Sky * 0.25f, $"the sky stays at {Sky}, not {Sky / LiftedPeak} as when the step divided the frame by its lifted star");
    }

    [Fact]
    public async Task ACropOfADeblurredFileKeepsTheCard()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("unitref-crop-").FullName;
        var deblurred = Path.Combine(folder, "deblurred.fits");
        var result = Path.Combine(folder, "cropped.fits");
        StarField(LiftedPeak, Meta() with { UnitReferred = true }).WriteToFitsFile(deblurred);

        var outcome = await new CropOperation().RunAsync(new CropSettings { X = 32, Y = 32, Width = 448, Height = 448 },
            [OperationInput.FromFile(deblurred)], result, output.WriteLine, ct);

        outcome.Failure.ShouldBe(OperationFailure.None, outcome.Message);
        Image.TryReadFitsFile(result, out var read).ShouldBeTrue();
        read.ShouldNotBeNull();
        read.Width.ShouldBe(448);
        read.MaxValue.ShouldBeGreaterThan(2f, "premise: the lifted star is inside the crop");
        read.IsUnitReferred.ShouldBeTrue("the crop carries the card");
    }

    [Fact]
    public void AnEnhanceTakesADeclaredImageIntoItsModelsRangeAndLeavesAnyOtherAsItIs()
    {
        var declared = StarField(LiftedPeak, Meta() with { UnitReferred = true });
        var plain = StarField(0.855f, Meta());

        var into = declared.ScaleFloatValuesIntoModelRange();

        declared.ModelRangeDivisor.ShouldBe(declared.MaxValue);
        into.MaxValue.ShouldBeLessThanOrEqualTo(1f + 1e-6f, "a model refusing a peak above 1.5 takes it");
        into.ImageMeta.UnitReferred.ShouldBeFalse("divided by its peak, 1.0 is no longer its full scale");
        plain.ModelRangeDivisor.ShouldBe(1f);
        plain.ScaleFloatValuesIntoModelRange().ShouldBeSameAs(plain, "an image the deblur never touched reaches the models as before");
    }

    [Fact]
    public void AFileNothingLiftedSaysNothingAndItsPeakDecides()
    {
        var folder = _folders.Create("unitref-plain-").FullName;
        var master = Path.Combine(folder, "master.fits");
        var adu = Path.Combine(folder, "adu.fits");
        StarField(0.855f, Meta()).WriteToFitsFile(master);
        StarField(60000f, Meta(), sky: 1200f).WriteToFitsFile(adu);

        OperationInput.FromFile(master).TryRead(out var unit, out _, out var refusal).ShouldBeTrue(refusal);
        OperationInput.FromFile(adu).TryRead(out var counts, out _, out refusal).ShouldBeTrue(refusal);
        unit.ShouldNotBeNull();
        counts.ShouldNotBeNull();

        unit.ImageMeta.UnitReferred.ShouldBeFalse("a step's file and its verb's stay byte for byte the same where nothing lifted");
        unit.IsUnitReferred.ShouldBeTrue("its peak says it");
        counts.ImageMeta.UnitReferred.ShouldBeFalse();
        counts.IsUnitReferred.ShouldBeFalse();
    }

    [Fact]
    public async Task ADeblurInMemorySaysItsResultIsOnItsInputsUnitScale()
    {
        var ct = TestContext.Current.CancellationToken;
        var master = StarField(0.855f, Meta());

        // BlurX lifted 10P's brightest star about 3.5 times; the default headroom keeps it under the deblurrer's ceiling.
        var (deblurred, atCeiling) = await NarrowbandCombination.DeblurAsync([master], new LiftingDeblurrer(3.5f), cancellationToken: ct);

        atCeiling.ShouldBe(0);
        deblurred[0].MaxValue.ShouldBeGreaterThan(2f, "premise: the lifted star stands above where the peak alone says unit scale");
        deblurred[0].ImageMeta.UnitReferred.ShouldBeTrue("the viewer and image sharpen never read it through a step, so the routine says it");
        deblurred[0].IsUnitReferred.ShouldBeTrue();
    }

    [Fact]
    public void ARescaleClearsTheDeclarationAndAnUnchangedScaleKeepsIt()
    {
        var declared = StarField(LiftedPeak, Meta() with { UnitReferred = true });

        var adu = declared.ScaleFloatValues(ushort.MaxValue);

        adu.ImageMeta.UnitReferred.ShouldBeFalse("multiplied out to ADU, 1.0 is no longer the full scale");
        adu.IsUnitReferred.ShouldBeFalse();
        adu.MaxValue.ShouldBe(declared.MaxValue * ushort.MaxValue, declared.MaxValue * ushort.MaxValue * 1e-5f, "the full scale maps to 65535, a lifted star above it");
        declared.ImageMeta.Rescale(1f).UnitReferred.ShouldBeTrue("a factor of one is no rescale");
        declared.ScaleFloatValuesToUnit().ShouldBeSameAs(declared, "a declared unit image is on its scale already and is not divided by its peak");
    }

    [Fact]
    public void TheStretchReadsADeclaredImageAtFaceValueAsItsHistogramDoes()
    {
        var declared = StarField(LiftedPeak, Meta() with { UnitReferred = true });
        var stats = StretchSolver.CollectPerChannelStats(declared, 1);

        var asDeclared = StretchSolver.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default, stats, null,
            declared.MaxValue, imageUnitReferred: declared.IsUnitReferred);
        var byPeak = StretchSolver.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default, stats, null, declared.MaxValue);

        asDeclared.NormFactor.ShouldBe(1f);
        byPeak.NormFactor.ShouldBe(1f / declared.MaxValue, 1e-6f, "premise: the peak alone divides the frame by its lifted star");
    }

    // A unit-scale sky with planted Gaussian stars, the brightest lifted to `peak` as a deblur lifts one.
    private static Image StarField(float peak, ImageMeta meta, float sky = Sky)
    {
        var plane = new float[Height, Width];
        var rng = new Random(42);
        var noise = sky * 0.03f;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                plane[y, x] = sky + noise * (float)(rng.NextDouble() - 0.5);
            }
        }

        var step = Width / 8;
        var planted = 0;
        for (var gy = 1; gy < 8 && planted <= PlantedStars; gy++)
        {
            for (var gx = 1; gx < 8 && planted <= PlantedStars; gx++)
            {
                // The first star is the lifted one; the rest sit well inside the full scale.
                var amplitude = planted == 0 ? peak - sky : (peak / LiftedPeak) * (0.05f + 0.25f * (float)rng.NextDouble());
                for (var dy = -5; dy <= 5; dy++)
                {
                    for (var dx = -5; dx <= 5; dx++)
                    {
                        plane[(gy * step) + dy, (gx * step) + dx] += amplitude * MathF.Exp(-((dx * dx) + (dy * dy)) / 4f);
                    }
                }
                planted++;
            }
        }

        var max = float.NegativeInfinity;
        var min = float.PositiveInfinity;
        foreach (var v in plane)
        {
            max = MathF.Max(max, v);
            min = MathF.Min(min, v);
        }

        return new Image([plane], BitDepth.Float32, max, min, 0f, meta);
    }

    // The sky: the median of the frame, which is all sky but for the planted stars.
    private static float SkyOf(Image image)
    {
        var samples = image.GetChannelSpan(0).ToArray();
        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    /// <summary>A gradient model with GraXpert's guard: it refuses a peak above 1.5, and otherwise hands back a copy.</summary>
    private sealed class GuardedCorrector : IGradientCorrector
    {
        public string Name => "Test GraXpert";

        public Task<Image> EnhanceAsync(Image input, System.Threading.CancellationToken cancellationToken = default)
        {
            if (input.MaxValue > 1.5f)
            {
                throw new ArgumentException($"requires input normalised to ~[0, 1], got MaxValue={input.MaxValue}");
            }

            return Task.FromResult(input.Clone());
        }
    }

    /// <summary>Lifts every value's height above the sky by a gain, as sharpening lifts a star's peak and leaves the sky, and
    /// stops at <c>[0, 1]</c>'s ceiling as BlurX does.</summary>
    private sealed class LiftingDeblurrer(float gain) : IImageDeblurrer
    {
        public string Name => "Test/Lifting";

        public Task<Image> EnhanceAsync(Image input, System.Threading.CancellationToken cancellationToken = default)
        {
            var src = input.GetChannelSpan(0);
            var sky = SkyOf(input);
            var plane = new float[input.Height, input.Width];
            var max = 0f;
            var min = float.PositiveInfinity;
            for (var y = 0; y < input.Height; y++)
            {
                for (var x = 0; x < input.Width; x++)
                {
                    var v = src[(y * input.Width) + x];
                    plane[y, x] = MathF.Min(1f, sky + (gain * (v - sky)));
                    max = MathF.Max(max, plane[y, x]);
                    min = MathF.Min(min, plane[y, x]);
                }
            }
            return Task.FromResult(new Image([plane], BitDepth.Float32, max, min, 0f, input.ImageMeta));
        }
    }

    private static ImageMeta Meta() => new("synth", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), FrameType.Light, "",
        3.76f, 3.76f, 400, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
        RowOrder.TopDown, float.NaN, float.NaN);
}
