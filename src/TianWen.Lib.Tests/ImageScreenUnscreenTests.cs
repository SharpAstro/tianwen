using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="Image.Screen"/> and <see cref="Image.Unscreen"/>.
/// These are pure pixel-wise math; the corresponding pipeline behaviour
/// is exercised in <see cref="SharpenPipelineTests"/>.
/// </summary>
public class ImageScreenUnscreenTests
{
    private static Image Make(int w, int h, float fill)
    {
        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                plane[y, x] = fill;
        return Image.FromChannel(plane, maxValue: 1.0f, minValue: 0f);
    }

    [Fact]
    public void Screen_OfZeroAndAny_EqualsAny()
    {
        var zero = Make(4, 4, 0f);
        var half = Make(4, 4, 0.5f);
        var result = zero.Screen(half);
        result[0, 0, 0].ShouldBe(0.5f, 1e-6f);
    }

    [Fact]
    public void Screen_OfOneAndAny_EqualsOne()
    {
        // screen(1, x) = 1 - 0 * (1-x) = 1.
        var one = Make(4, 4, 1f);
        var half = Make(4, 4, 0.5f);
        one.Screen(half)[0, 0, 0].ShouldBe(1f, 1e-6f);
        half.Screen(one)[0, 0, 0].ShouldBe(1f, 1e-6f);
    }

    [Fact]
    public void Screen_HalfHalf_EqualsThreeQuarters()
    {
        // screen(0.5, 0.5) = 1 - 0.25 = 0.75.
        var half = Make(4, 4, 0.5f);
        half.Screen(half)[0, 0, 0].ShouldBe(0.75f, 1e-6f);
    }

    [Fact]
    public void Screen_IsCommutative()
    {
        var a = Make(4, 4, 0.3f);
        var b = Make(4, 4, 0.7f);
        a.Screen(b)[0, 0, 0].ShouldBe(b.Screen(a)[0, 0, 0], 1e-6f);
    }

    [Fact]
    public void Unscreen_IsInverseOfScreen()
    {
        // unscreen(screen(a, b), b) == a within float precision.
        var a = Make(4, 4, 0.3f);
        var b = Make(4, 4, 0.5f);
        var screened = a.Screen(b);
        var extracted = screened.Unscreen(b);
        extracted[0, 0, 0].ShouldBe(0.3f, 1e-5f);
    }

    [Fact]
    public void Unscreen_HalfFromThreeQuarters_RecoversHalf()
    {
        // Composite 0.75 = screen(0.5, 0.5), so unscreen(0.75, 0.5) = 0.5.
        var threeQ = Make(4, 4, 0.75f);
        var half = Make(4, 4, 0.5f);
        threeQ.Unscreen(half)[0, 0, 0].ShouldBe(0.5f, 1e-6f);
    }

    [Fact]
    public void Unscreen_OvershootClampsToZero()
    {
        // unscreen(composite, other) where composite < other -> the second
        // layer would have been "negative light"; we clamp to 0.
        var lo = Make(4, 4, 0.2f);
        var hi = Make(4, 4, 0.5f);
        lo.Unscreen(hi)[0, 0, 0].ShouldBe(0f, 1e-6f);
    }

    [Fact]
    public void Unscreen_OtherSaturatedDoesNotProduceInf()
    {
        // other = 1.0 -> denominator (1 - 1) = 0; clamp guards against inf/NaN.
        var src = Make(4, 4, 0.7f);
        var sat = Make(4, 4, 1f);
        var result = src.Unscreen(sat);
        // With denominator clamped at epsilon, the formula collapses to
        // 1 - (1 - 0.7)/epsilon = a huge negative; clamp to [0, 1] -> 0.
        result[0, 0, 0].ShouldBe(0f, 1e-5f);
    }
}
