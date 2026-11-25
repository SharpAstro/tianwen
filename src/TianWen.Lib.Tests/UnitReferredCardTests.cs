using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
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
    public void AStepReadsAUnitInputAsDeclaredAndItsResultSaysSoWhateverItsPeak()
    {
        var folder = _folders.Create("unitref-step-").FullName;
        var master = Path.Combine(folder, "master.fits");
        var result = Path.Combine(folder, "deblurred.fits");
        StarField(0.855f, Meta()).WriteToFitsFile(master);

        OperationInput.FromFile(master).TryRead(out var input, out var wcs, out var refusal).ShouldBeTrue(refusal);
        input.ShouldNotBeNull();
        input.ImageMeta.UnitReferred.ShouldBeTrue("an input unit-referred by its peak is read as declared");

        // What a deblur does: the same scale, a star lifted past 2.0, the input's metadata carried onto the result.
        var lifted = StarField(LiftedPeak, input.ImageMeta);
        OperationOutput.Write(lifted, result, wcs);

        Image.TryReadFitsFile(result, out var read).ShouldBeTrue();
        read.ShouldNotBeNull();
        read.MaxValue.ShouldBeGreaterThan(2f, "premise: the result's peak alone would say ADU");
        read.IsUnitReferred.ShouldBeTrue("the step's result says it is on its input's scale");
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
    public void AnAduFrameIsNotDeclaredByBeingRead()
    {
        var folder = _folders.Create("unitref-adu-").FullName;
        var path = Path.Combine(folder, "adu.fits");
        StarField(60000f, Meta(), sky: 1200f).WriteToFitsFile(path);

        OperationInput.FromFile(path).TryRead(out var input, out _, out var refusal).ShouldBeTrue(refusal);
        input.ShouldNotBeNull();

        input.ImageMeta.UnitReferred.ShouldBeFalse();
        input.IsUnitReferred.ShouldBeFalse();
    }

    [Fact]
    public void ARescaleClearsTheDeclarationAndAnUnchangedScaleKeepsIt()
    {
        var declared = StarField(LiftedPeak, Meta() with { UnitReferred = true });

        var adu = declared.ScaleFloatValues(ushort.MaxValue);

        adu.ImageMeta.UnitReferred.ShouldBeFalse("multiplied out to ADU, 1.0 is no longer the full scale");
        adu.IsUnitReferred.ShouldBeFalse();
        adu.MaxValue.ShouldBe(LiftedPeak * ushort.MaxValue, LiftedPeak * ushort.MaxValue * 1e-5f, "the full scale maps to 65535, a lifted star above it");
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
        byPeak.NormFactor.ShouldBe(1f / LiftedPeak, 1e-6f, "premise: the peak alone divides the frame by its lifted star");
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

    /// <summary>Lifts every value by a gain, as sharpening lifts a star's peak, and stops at <c>[0, 1]</c>'s ceiling as BlurX does.</summary>
    private sealed class LiftingDeblurrer(float gain) : IImageDeblurrer
    {
        public string Name => "Test/Lifting";

        public Task<Image> EnhanceAsync(Image input, System.Threading.CancellationToken cancellationToken = default)
        {
            var src = input.GetChannelSpan(0);
            var plane = new float[input.Height, input.Width];
            var max = 0f;
            for (var y = 0; y < input.Height; y++)
            {
                for (var x = 0; x < input.Width; x++)
                {
                    plane[y, x] = MathF.Min(1f, gain * src[(y * input.Width) + x]);
                    max = MathF.Max(max, plane[y, x]);
                }
            }
            return Task.FromResult(new Image([plane], BitDepth.Float32, max, 0f, 0f, input.ImageMeta));
        }
    }

    private static ImageMeta Meta() => new("synth", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), FrameType.Light, "",
        3.76f, 3.76f, 400, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
        RowOrder.TopDown, float.NaN, float.NaN);
}
