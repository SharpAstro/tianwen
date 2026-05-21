using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pixel-math tests for <see cref="Image.SubtractiveChromaticNoise"/> and
/// <see cref="Image.Lerp"/>. Pipeline-level integration (SCNR-on-stars,
/// AI-blend amounts) is exercised in <c>SharpenPipelineTests</c>.
/// </summary>
public class ImageScnrLerpTests
{
    private static Image Rgb(float r, float g, float b)
    {
        // 2x2 plane filled with the constant per channel -- shape is irrelevant
        // for these tests, we just need pixel-level math.
        var rp = new float[2, 2];
        var gp = new float[2, 2];
        var bp = new float[2, 2];
        for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++)
        {
            rp[y, x] = r;
            gp[y, x] = g;
            bp[y, x] = b;
        }
        return new Image([rp, gp, bp], BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Color });
    }

    // --- SCNR --------------------------------------------------------

    [Fact]
    public void Scnr_PureGreenPixelGoesToZero_AverageMode()
    {
        // R=B=0, G=1 -> m=(0+0)/2=0; excess=1; Gnew = 1 - 1*1 = 0.
        var src = Rgb(0f, 1f, 0f);
        var out_ = src.SubtractiveChromaticNoise(ScnrMode.Average, amount: 1.0f);
        out_[0, 0, 0].ShouldBe(0f, 1e-6f);
        out_[1, 0, 0].ShouldBe(0f, 1e-6f);
        out_[2, 0, 0].ShouldBe(0f, 1e-6f);
    }

    [Fact]
    public void Scnr_NeutralPixelUnchanged()
    {
        // R=G=B=0.5 -> m=0.5; excess=0; Gnew=0.5. R/B untouched.
        var src = Rgb(0.5f, 0.5f, 0.5f);
        var out_ = src.SubtractiveChromaticNoise(ScnrMode.Average, amount: 1.0f);
        out_[0, 0, 0].ShouldBe(0.5f, 1e-6f);
        out_[1, 0, 0].ShouldBe(0.5f, 1e-6f);
        out_[2, 0, 0].ShouldBe(0.5f, 1e-6f);
    }

    [Fact]
    public void Scnr_GreenStarPulledToAverage_AverageMode()
    {
        // Realistic green star: R=B=0.5, G=0.8 -> m=0.5; excess=0.3;
        // Gnew = 0.8 - 1*0.3 = 0.5. Becomes neutral white at amount=1.
        var src = Rgb(0.5f, 0.8f, 0.5f);
        var out_ = src.SubtractiveChromaticNoise(ScnrMode.Average, amount: 1.0f);
        out_[1, 0, 0].ShouldBe(0.5f, 1e-6f);
    }

    [Fact]
    public void Scnr_MaximumModeUsesMaxOfRB()
    {
        // R=0.4, B=0.6, G=0.9 -> Maximum m=max(0.4,0.6)=0.6;
        // excess=0.3; Gnew = 0.9 - 1*0.3 = 0.6.
        var src = Rgb(0.4f, 0.9f, 0.6f);
        var out_ = src.SubtractiveChromaticNoise(ScnrMode.Maximum, amount: 1.0f);
        out_[1, 0, 0].ShouldBe(0.6f, 1e-6f);
    }

    [Fact]
    public void Scnr_AmountZeroIsIdentity()
    {
        var src = Rgb(0.5f, 0.8f, 0.5f);
        var out_ = src.SubtractiveChromaticNoise(ScnrMode.Average, amount: 0.0f);
        out_[1, 0, 0].ShouldBe(0.8f, 1e-6f);
    }

    [Fact]
    public void Scnr_AmountHalfBlends()
    {
        // R=B=0.5, G=0.8 -> m=0.5, excess=0.3, Gnew = 0.8 - 0.5*0.3 = 0.65.
        var src = Rgb(0.5f, 0.8f, 0.5f);
        var out_ = src.SubtractiveChromaticNoise(ScnrMode.Average, amount: 0.5f);
        out_[1, 0, 0].ShouldBe(0.65f, 1e-6f);
    }

    [Fact]
    public void Scnr_NoneIsNoOpButReturnsFreshCopy()
    {
        var src = Rgb(0.5f, 0.8f, 0.5f);
        var out_ = src.SubtractiveChromaticNoise(ScnrMode.None);
        out_[1, 0, 0].ShouldBe(0.8f);
        out_.ShouldNotBeSameAs(src);
    }

    [Fact]
    public void Scnr_MonoPassesThroughUnchanged()
    {
        var mono = Image.FromChannel(new float[2, 2] { { 0.7f, 0.7f }, { 0.7f, 0.7f } });
        var out_ = mono.SubtractiveChromaticNoise(ScnrMode.Average);
        out_.ChannelCount.ShouldBe(1);
        out_[0, 0, 0].ShouldBe(0.7f);
    }

    [Fact]
    public void Scnr_AmountClampedToZeroOne()
    {
        var src = Rgb(0.5f, 0.8f, 0.5f);
        var hi = src.SubtractiveChromaticNoise(ScnrMode.Average, amount: 5f);
        var atOne = src.SubtractiveChromaticNoise(ScnrMode.Average, amount: 1f);
        hi[1, 0, 0].ShouldBe(atOne[1, 0, 0], 1e-6f);

        var lo = src.SubtractiveChromaticNoise(ScnrMode.Average, amount: -1f);
        var atZero = src.SubtractiveChromaticNoise(ScnrMode.Average, amount: 0f);
        lo[1, 0, 0].ShouldBe(atZero[1, 0, 0], 1e-6f);
    }

    // --- Lerp --------------------------------------------------------

    [Fact]
    public void Lerp_AtZero_ReturnsThis()
    {
        var a = Rgb(0.1f, 0.2f, 0.3f);
        var b = Rgb(0.9f, 0.8f, 0.7f);
        var out_ = a.Lerp(b, 0f);
        out_[0, 0, 0].ShouldBe(0.1f, 1e-6f);
        out_[1, 0, 0].ShouldBe(0.2f, 1e-6f);
        out_[2, 0, 0].ShouldBe(0.3f, 1e-6f);
    }

    [Fact]
    public void Lerp_AtOne_ReturnsOther()
    {
        var a = Rgb(0.1f, 0.2f, 0.3f);
        var b = Rgb(0.9f, 0.8f, 0.7f);
        var out_ = a.Lerp(b, 1f);
        out_[0, 0, 0].ShouldBe(0.9f, 1e-6f);
        out_[1, 0, 0].ShouldBe(0.8f, 1e-6f);
        out_[2, 0, 0].ShouldBe(0.7f, 1e-6f);
    }

    [Fact]
    public void Lerp_AtHalf_IsMidpoint()
    {
        var a = Rgb(0.0f, 0.0f, 0.0f);
        var b = Rgb(1.0f, 1.0f, 1.0f);
        var out_ = a.Lerp(b, 0.5f);
        out_[0, 0, 0].ShouldBe(0.5f, 1e-6f);
        out_[1, 0, 0].ShouldBe(0.5f, 1e-6f);
        out_[2, 0, 0].ShouldBe(0.5f, 1e-6f);
    }

    [Fact]
    public void Lerp_ClampsAmount()
    {
        var a = Rgb(0.2f, 0.3f, 0.4f);
        var b = Rgb(0.6f, 0.7f, 0.8f);

        var below = a.Lerp(b, -1f);
        below[0, 0, 0].ShouldBe(0.2f, 1e-6f); // clamped to 0 -> identity with a

        var above = a.Lerp(b, 5f);
        above[0, 0, 0].ShouldBe(0.6f, 1e-6f); // clamped to 1 -> identity with b
    }

    [Fact]
    public void Lerp_RejectsShapeMismatch()
    {
        var a = Rgb(0.5f, 0.5f, 0.5f);
        var b = Image.FromChannel(new float[2, 2]); // mono != 3-channel
        Should.Throw<System.ArgumentException>(() => a.Lerp(b, 0.5f));
    }
}
