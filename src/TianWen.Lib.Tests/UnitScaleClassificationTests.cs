using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A float image is unit-referred or it is not, and ONE pixel must not decide it.
/// </summary>
/// <remarks>
/// <para>Found on a real integrated master read from a tile-compressed <c>.fz</c>: exactly one pixel
/// of 24.9 million exceeded 1.0, by 15 ulps, because a quantized float decode is 1-ulp noisy. That was
/// enough to make <see cref="Image.Histogram"/> bin [0,1] samples at face value -- into two buckets --
/// so <see cref="Image.Background"/> reported a background of 0 and <c>FindStarsAsync</c> took its
/// "abnormal file" path and returned an EMPTY list. No error, no warning.</para>
/// <para>The outlier sits in a DIFFERENT channel from the one being measured, deliberately:
/// <c>MaxValue</c> is image-wide, so on the real frame a single blue pixel changed how the red channel
/// was measured. A test that puts the outlier in the measured channel would pass for the wrong reason.
/// </para>
/// </remarks>
[Collection("Imaging")]
public class UnitScaleClassificationTests
{
    private const int Size = 160;
    private const float Sky = 0.002f;      // a real master's background sits here, not at 0.5
    private const float Sigma = 1.6f;
    private const float Amplitude = 0.30f;
    private const float NoiseSpan = 0.0012f;

    private static readonly (float X, float Y)[] Stars =
    [
        (40.3f, 50.7f), (100.6f, 40.2f), (120.4f, 110.8f), (60.5f, 120.3f), (80.2f, 80.6f),
        (30.7f, 90.4f), (140.1f, 70.9f), (70.8f, 30.5f), (110.3f, 140.2f), (50.9f, 20.4f),
    ];

    private static ImageMeta Meta() => new ImageMeta(
        "synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(150),
        FrameType.Light, "", 2.9f, 2.9f, 1180, -1, Filter.None, 1, 1,
        float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

    /// <summary>Three unit-scaled channels; <paramref name="blueOutlier"/> is stamped into the BLUE
    /// channel's corner, which is what drives the image-wide <see cref="Image.MaxValue"/>.</summary>
    private static Image Build(float blueOutlier)
    {
        var twoSigmaSq = 2f * Sigma * Sigma;
        // Fixed seed: the detector estimates its own threshold from the noise, so the frame needs
        // some -- a noiseless background gives it nothing to measure and it finds nothing at all,
        // which looks exactly like the bug under test and is why this is stated rather than omitted.
        var rng = new Random(42);
        var planes = new float[3][,];
        var max = 0f;
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var v = Sky + (float)(rng.NextDouble() - 0.5) * NoiseSpan;
                    foreach (var (sx, sy) in Stars)
                    {
                        var dx = x - sx;
                        var dy = y - sy;
                        v += Amplitude * MathF.Exp(-(dx * dx + dy * dy) / twoSigmaSq);
                    }
                    planes[c][y, x] = v;
                    if (v > max)
                    {
                        max = v;
                    }
                }
            }
        }

        planes[2][0, 0] = blueOutlier;
        return new Image(planes, BitDepth.Float32, MathF.Max(max, blueOutlier), 0f, 0f, Meta());
    }

    [Fact]
    public async Task ASinglePixelAHairOverOneDoesNotCostTheFrameEveryStar()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1.0000018f is what the real .fz master decoded to -- not a round number, because the point
        // is that it is decode noise rather than a different scale.
        var overshoot = Build(blueOutlier: 1.0000018f);
        overshoot.MaxValue.ShouldBeGreaterThan(1.0f, "the fixture must actually exceed one");

        var stars = await overshoot.FindStarsAsync(
            channel: 0, snrMin: 10f, maxStars: 2000, cancellationToken: ct);

        stars.Count.ShouldBe(Stars.Length);
    }

    [Fact]
    public async Task TheOvershootFrameFindsTheSameStarsAsACleanOne()
    {
        var ct = TestContext.Current.CancellationToken;

        var clean = await Build(blueOutlier: Sky).FindStarsAsync(
            channel: 0, snrMin: 10f, maxStars: 2000, cancellationToken: ct);
        var overshoot = await Build(blueOutlier: 1.0000018f).FindStarsAsync(
            channel: 0, snrMin: 10f, maxStars: 2000, cancellationToken: ct);

        // Not merely "some stars": one pixel in an unmeasured channel must change NOTHING about the
        // measured one.
        clean.Count.ShouldBe(Stars.Length);
        overshoot.Count.ShouldBe(clean.Count);
    }

    [Fact]
    public async Task AnIntegratedMastersSaturatedCoresDoNotEmptyTheStarList()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1.025 is not decode noise, it is what a real 135-frame stack measured: saturated star cores
        // sit at the top of the scale and per-frame normalisation lifts them a little past one. The
        // old 1e-3 bound called that ADU, so the histogram binned [0,1] data at face value, Background
        // reported 0, and FindStarsAsync returned an empty list -- which surfaced only as the stacking
        // pipeline's plate solve failing with a warning and no WCS.
        var master = Build(blueOutlier: 1.025f);
        master.MaxValue.ShouldBeGreaterThan(1.0f, "the fixture must actually exceed one");
        master.HasUnitScalePeak.ShouldBeTrue("a 2.5% overshoot is still unit-referred, not ADU");

        var clean = await Build(blueOutlier: Sky).FindStarsAsync(
            channel: 0, snrMin: 10f, maxStars: 2000, cancellationToken: ct);
        var stars = await master.FindStarsAsync(
            channel: 0, snrMin: 10f, maxStars: 2000, cancellationToken: ct);

        stars.Count.ShouldBe(clean.Count);
    }

    [Theory]
    // Unit-referred: at one, a hair over from decode noise, and comfortably under.
    [InlineData(1.0f, true)]
    [InlineData(1.0000018f, true)]
    [InlineData(0.4f, true)]
    // Flat-division overshoot on a real integrated master: a saturated pixel normalises to exactly
    // 1.0, and dividing it by a vignetted flat below one must exceed one. Measured 1.025 on a
    // 135-frame stack, at saturated stars off axis (flat 0.949 there, 1.018 at the centre where
    // nothing overshoots). Classifying that as ADU emptied the star list on re-read.
    [InlineData(1.0009f, true)]
    [InlineData(1.01f, true)]
    [InlineData(1.025f, true)]
    // The band's far edge, and past it. 2.0 is two orders of magnitude below the nearest competing
    // scale, so the bound can be this generous without ever mistaking 8-bit data for unit-referred.
    [InlineData(2.0f, true)]
    [InlineData(2.5f, false)]
    // ADU scale, which must keep binning at face value.
    [InlineData(255f, false)]
    [InlineData(65535f, false)]
    public void TheScaleClassificationToleratesNoiseButNotADifferentScale(float maxValue, bool expected)
    {
        var planes = new float[1][,] { new float[2, 2] };
        planes[0][0, 0] = maxValue;
        var image = new Image(planes, BitDepth.Float32, maxValue, 0f, 0f, Meta());

        image.IsUnitScaledFloat.ShouldBe(expected);
        // The two must stay complements -- them drifting apart is the bug this guards.
        image.HasUnitScalePeak.ShouldBe(expected);
    }

    [Fact]
    public void AnAlreadyUnitReferredImageIsNotCopiedInOrderToNormaliseIt()
    {
        var image = Build(blueOutlier: 1.0000018f);

        // Reference equality, not pixel equality: the early return is what keeps a 25 MP master from
        // paying a full pass to divide by something indistinguishable from 1. Dividing anyway would
        // still be "correct" and would still cost the pass, so only identity catches the regression.
        image.ScaleFloatValuesToUnit().ShouldBeSameAs(image);
        image.ScaleFloatValuesToUnitInPlace().ShouldBeSameAs(image);
    }

    [Fact]
    public void TheShaderNormFactorAgreesWithTheImageAboutWhatCountsAsNormalised()
    {
        // StretchSolver is handed the peak on its own and produces the shader's NormFactor, while the
        // histogram picks the divisor the CPU statistics are expressed in. If those two disagree the
        // display no longer matches its own histogram, which is why the static form exists at all.
        const float DecodeNoisePeak = 1.0000018f;

        Image.IsUnitScalePeak(DecodeNoisePeak).ShouldBeTrue();
        Build(blueOutlier: DecodeNoisePeak).HasUnitScalePeak.ShouldBeTrue();
    }

    [Fact]
    public void AnIntegerImageIsNeverUnitScaledFloatHoweverSmallItsPeak()
    {
        var planes = new float[1][,] { new float[2, 2] };
        planes[0][0, 0] = 1.0f;
        var image = new Image(planes, BitDepth.Int16, 1.0f, 0f, 0f, Meta());

        // Int16 data whose observed peak happens to be 1 ADU is ADU data, not unit-referred, and must
        // not be binned into 65535 buckets.
        image.IsUnitScaledFloat.ShouldBeFalse();
    }

    /// <summary>
    /// The other half of the rule above: an integer container whose importer ALREADY normalised its
    /// samples is unit-referred, and only the importer can know that.
    /// </summary>
    /// <remarks>
    /// These two facts used to share <see cref="Image.BitDepth"/>, so the test above was enforcing
    /// "never" over a population that includes every PNG, JPEG and 8/16-bit TIFF -- all of which
    /// silently detected zero stars. See <see cref="Image.SamplesAreUnitReferred"/> and the
    /// end-to-end <c>UnitReferredImportStarDetectionTests</c>.
    /// </remarks>
    [Fact]
    public void AnIntegerContainerThatWasNormalisedOnImportIsUnitScaled()
    {
        var planes = new float[1][,] { new float[2, 2] };
        planes[0][0, 0] = 1.0f;
        var image = new Image(planes, BitDepth.Int16, 1.0f, 0f, 0f, Meta(), samplesAreUnitReferred: true);

        image.SamplesAreUnitReferred.ShouldBeTrue();
        image.IsUnitScaledFloat.ShouldBeTrue();
        // The container width is still the SOURCE width -- that is what CarriesDisplayDataOnly and the
        // GPU upload format read, so the flag must not have quietly rewritten it.
        image.BitDepth.ShouldBe(BitDepth.Int16);
    }

    /// <summary>A peak well past the tolerance is ADU data whatever an importer claims: the flag says
    /// "1.0 is my full scale", and a sample at 255 contradicts that outright.</summary>
    [Fact]
    public void TheFlagDoesNotOverrideAPeakThatIsPlainlyNotUnitScale()
    {
        var planes = new float[1][,] { new float[2, 2] };
        planes[0][0, 0] = 255f;
        var image = new Image(planes, BitDepth.Int8, 255f, 0f, 0f, Meta(), samplesAreUnitReferred: true);

        image.IsUnitScaledFloat.ShouldBeFalse();
    }

    /// <summary>Normalising an ADU image yields unit-referred data by construction, so it says so
    /// rather than relying on the Float32 stamp that happens to accompany it.</summary>
    [Fact]
    public void NormalisingStampsTheScaleFactAndNotOnlyTheContainer()
    {
        var planes = new float[1][,] { new float[2, 2] };
        planes[0][0, 0] = 4000f;
        var adu = new Image(planes, BitDepth.Int16, 4000f, 0f, 0f, Meta());
        adu.SamplesAreUnitReferred.ShouldBeFalse();

        var unit = adu.ScaleFloatValuesToUnit();
        unit.ShouldNotBeSameAs(adu);
        unit.SamplesAreUnitReferred.ShouldBeTrue();
        unit.IsUnitScaledFloat.ShouldBeTrue();
    }
}
