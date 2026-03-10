using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

public class ContrastBoostTests(ITestOutputHelper testOutputHelper)
{
    private const string RGGBImage = "RGGB_frame_bx0_by0_top_down";

    /// <summary>
    /// Simulates the GPU applyCurve function on CPU.
    /// Must match the GLSL implementation exactly.
    /// SP is placed slightly above the background so the background gets darkened.
    /// Everything above SP gets boosted (midtones/detail). Above HP is passthrough (star protection).
    /// </summary>
    private static float ApplyCurve(float v, float boost, float bg)
    {
        var hp = 0.85f;
        if (v <= 0f || v >= 1f || bg <= 0f || bg >= hp) return v;

        var sp = bg * (1f + 0.1f * boost);
        sp = MathF.Min(sp, hp - 0.01f);

        if (v <= sp)
        {
            var t = v / sp;
            var darkPower = 1f + boost * 3f;
            return sp * MathF.Pow(t, darkPower);
        }
        else if (v < hp)
        {
            var t = (v - sp) / (hp - sp);
            return sp + (hp - sp) * MathF.Pow(t, 1f / (1f + boost));
        }
        else
        {
            return v;
        }
    }

    /// <summary>
    /// Computes the average pixel value over a square region of a single channel.
    /// </summary>
    private static float AverageRegion(Image image, int channel, int x0, int y0, int size)
    {
        double sum = 0;
        var count = 0;
        for (var y = y0; y < y0 + size && y < image.Height; y++)
        {
            for (var x = x0; x < x0 + size && x < image.Width; x++)
            {
                sum += image[channel, y, x];
                count++;
            }
        }
        return (float)(sum / count);
    }

    /// <summary>
    /// Computes average Rec.709 luminance over a square region.
    /// </summary>
    private static float AverageLuma(Image image, int x0, int y0, int size)
    {
        if (image.ChannelCount < 3)
        {
            return AverageRegion(image, 0, x0, y0, size);
        }
        var r = AverageRegion(image, 0, x0, y0, size);
        var g = AverageRegion(image, 1, x0, y0, size);
        var b = AverageRegion(image, 2, x0, y0, size);
        return 0.2126f * r + 0.7152f * g + 0.0722f * b;
    }

    [Fact]
    public async Task GivenRGGBImage_WhenStretched_MeasureBackgroundAndObjectLevels()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var rawImage = await SharedTestData.ExtractGZippedFitsImageAsync(RGGBImage, cancellationToken: cancellationToken);

        // Debayer and stretch — now returns normalized [0,1] image
        var stretched = await rawImage.StretchUnlinkedAsync(0.1, -5.0, DebayerAlgorithm.AHD, cancellationToken);
        stretched.ChannelCount.ShouldBe(3);
        stretched.MaxValue.ShouldBe(1.0f);

        testOutputHelper.WriteLine($"Stretched image: {stretched.Width}x{stretched.Height}x{stretched.ChannelCount}, MaxValue={stretched.MaxValue:F4}");

        // Scan for background and object regions
        var squareSize = 32;
        var step = squareSize * 4;

        var minLuma = float.MaxValue;
        int bgX = 0, bgY = 0;
        var maxMidLuma = float.MinValue;
        int objX = 0, objY = 0;

        for (var y = 0; y < stretched.Height - squareSize; y += step)
        {
            for (var x = 0; x < stretched.Width - squareSize; x += step)
            {
                var luma = AverageLuma(stretched, x, y, squareSize);
                if (luma < minLuma && luma > 0.001f)
                {
                    minLuma = luma;
                    bgX = x;
                    bgY = y;
                }
                // Object: brighter than background but not a star
                if (luma > 0.25f && luma < 0.7f && luma > maxMidLuma)
                {
                    maxMidLuma = luma;
                    objX = x;
                    objY = y;
                }
            }
        }

        var bgLuma = AverageLuma(stretched, bgX, bgY, squareSize);
        var objLuma = AverageLuma(stretched, objX, objY, squareSize);

        testOutputHelper.WriteLine($"Background ({bgX},{bgY}): luma = {bgLuma:F6}");
        testOutputHelper.WriteLine($"Object ({objX},{objY}): luma = {objLuma:F6}");

        // The background should be noticeably dimmer than the object
        bgLuma.ShouldBeLessThan(objLuma);

        // Now test the curves boost
        testOutputHelper.WriteLine($"\n--- Curves boost test (bg level = {bgLuma:F4}) ---");

        foreach (var boost in ViewerState.CurvesBoostPresets)
        {
            if (boost == 0f) continue;

            var boostedBg = ApplyCurve(bgLuma, boost, bgLuma);
            var boostedObj = ApplyCurve(objLuma, boost, bgLuma);

            testOutputHelper.WriteLine($"Boost {boost:F2}: bg {bgLuma:F4} -> {boostedBg:F4} (delta={boostedBg - bgLuma:+0.0000;-0.0000}), " +
                $"obj {objLuma:F4} -> {boostedObj:F4} (delta={boostedObj - objLuma:+0.0000;-0.0000})");

            // Background should get DARKER
            boostedBg.ShouldBeLessThan(bgLuma,
                $"Background should get darker with boost={boost}, but went from {bgLuma:F4} to {boostedBg:F4}");

            // Contrast should INCREASE (this is the key requirement)
            var contrastBefore = objLuma - bgLuma;
            var contrastAfter = boostedObj - boostedBg;
            contrastAfter.ShouldBeGreaterThan(contrastBefore,
                $"Contrast should increase with boost={boost}: was {contrastBefore:F4}, now {contrastAfter:F4}");
        }
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.50f)]
    [InlineData(1.00f)]
    [InlineData(1.50f)]
    public void ApplyCurve_AtHighlight_IsPassthrough(float boost)
    {
        var bg = 0.15f;
        ApplyCurve(0.90f, boost, bg).ShouldBe(0.90f);
        ApplyCurve(0.95f, boost, bg).ShouldBe(0.95f);
        ApplyCurve(1.00f, boost, bg).ShouldBe(1.00f);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.50f)]
    [InlineData(1.00f)]
    public void ApplyCurve_WellBelowBackground_GetsDarker(float boost)
    {
        var bg = 0.20f;
        var sp = bg * (1f + 0.1f * boost);

        // Value well below SP should get darker
        var val = sp * 0.3f;
        var boosted = ApplyCurve(val, boost, bg);
        boosted.ShouldBeLessThan(val,
            $"Value {val:F4} well below SP should get darker, got {boosted:F4}");
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.50f)]
    [InlineData(1.00f)]
    public void ApplyCurve_IsContinuous(float boost)
    {
        var bg = 0.15f;
        var sp = bg * (1f + 0.1f * boost);
        var hp = 0.85f;

        // At SP boundary — function is continuous (both sides → sp),
        // but the midtone zone has steep slope (pow(t, <1) has infinite derivative at 0),
        // so we use a very small epsilon.
        var spEps = 0.0001f;
        var belowSp = ApplyCurve(sp - spEps, boost, bg);
        var atSp = ApplyCurve(sp, boost, bg);
        var aboveSp = ApplyCurve(sp + spEps, boost, bg);
        Math.Abs(belowSp - atSp).ShouldBeLessThan(0.01f, $"Discontinuity at SP: below={belowSp:F6}, at={atSp:F6}");
        Math.Abs(aboveSp - atSp).ShouldBeLessThan(0.02f, $"Discontinuity at SP: above={aboveSp:F6}, at={atSp:F6}");

        // At HP boundary
        var hpEps = 0.001f;
        var belowHp = ApplyCurve(hp - hpEps, boost, bg);
        var aboveHp = ApplyCurve(hp + hpEps, boost, bg);
        Math.Abs(belowHp - aboveHp).ShouldBeLessThan(0.01f, $"Discontinuity at HP: below={belowHp:F6}, above={aboveHp:F6}");
    }
}
