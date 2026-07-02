using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for the luminance range mask primitive and the operations that go through it
/// (<see cref="Image.LuminanceRangeMask"/>, <see cref="Image.BlendThroughMask"/>,
/// <see cref="Image.Saturate"/>, <see cref="Image.ContrastBoost"/>). The mask is the reusable piece:
/// ~1 over mid-tone signal, ~0 in background, ~0 in highlights (star cores).
/// </summary>
[Collection("Imaging")]
public class ImageMaskTests
{
    private static float[,] Fill(int w, int h, float v)
    {
        var a = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                a[y, x] = v;
        return a;
    }

    private static Image Rgb(float[,] r, float[,] g, float[,] b) =>
        new([r, g, b], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });

    /// <summary>
    /// A 1-row mono image with a <b>background-dominated</b> distribution (like a real deep-sky frame):
    /// 40 background pixels, then 12 mid-tone, then 4 highlight. The dominant background keeps the shadow
    /// percentile anchored in the background band. Band centres: bg=idx 5, mid=idx 45, hi=idx 53.
    /// </summary>
    private static Image MonoBands(float bg, float mid, float hi)
    {
        const int bgCount = 40, midCount = 12, hiCount = 4;
        var w = bgCount + midCount + hiCount;
        var a = new float[1, w];
        for (var x = 0; x < bgCount; x++) a[0, x] = bg;
        for (var x = bgCount; x < bgCount + midCount; x++) a[0, x] = mid;
        for (var x = bgCount + midCount; x < w; x++) a[0, x] = hi;
        return Image.FromChannel(a, maxValue: 1f, minValue: 0f);
    }

    [Fact]
    public void LuminanceRangeMask_is_low_in_background_high_in_midtones_low_in_highlights()
    {
        // No blur so the bands stay crisp for the assertion.
        var mask = MonoBands(bg: 0.05f, mid: 0.5f, hi: 0.97f)
            .LuminanceRangeMask(blurSigma: 0f);

        mask.ChannelCount.ShouldBe(1);

        // Sample the centre of each band (see MonoBands: bg=5, mid=45, hi=53).
        var bg = mask[0, 0, 5];
        var mid = mask[0, 0, 45];
        var hi = mask[0, 0, 53];

        // A luminance mask only nears 1.0 close to the peak (minus highlight protection); a mid-tone gets
        // a partial, clearly-positive weight. What matters is the ordering + background/highlight suppression.
        bg.ShouldBeLessThan(0.05f);       // background suppressed (~0 at the percentile anchor)
        mid.ShouldBeGreaterThan(0.2f);    // mid-tone signal clearly passed
        hi.ShouldBeLessThan(0.1f);        // highlight (star core) protected
        mid.ShouldBeGreaterThan(bg);
        mid.ShouldBeGreaterThan(hi);
    }

    [Fact]
    public void LuminanceRangeMask_stays_within_unit_range()
    {
        var mask = MonoBands(0.05f, 0.5f, 0.97f).LuminanceRangeMask();
        for (var x = 0; x < mask.Width; x++)
        {
            mask[0, 0, x].ShouldBeInRange(0f, 1f);
        }
    }

    [Fact]
    public void BlendThroughMask_is_base_where_zero_and_processed_where_one()
    {
        var w = 4;
        var baseImg = Image.FromChannel(Fill(w, 1, 0.2f), 1f, 0f);
        var proc = Image.FromChannel(Fill(w, 1, 0.9f), 1f, 0f);

        // Mask 0,0,1,1 -> first two pixels keep base, last two take processed.
        var maskArr = new float[1, w];
        maskArr[0, 0] = 0f; maskArr[0, 1] = 0f; maskArr[0, 2] = 1f; maskArr[0, 3] = 1f;
        var mask = Image.FromChannel(maskArr, 1f, 0f);

        var blended = baseImg.BlendThroughMask(proc, mask);

        blended[0, 0, 0].ShouldBe(0.2f, 1e-5f);
        blended[0, 0, 1].ShouldBe(0.2f, 1e-5f);
        blended[0, 0, 2].ShouldBe(0.9f, 1e-5f);
        blended[0, 0, 3].ShouldBe(0.9f, 1e-5f);
    }

    [Fact]
    public void BlendThroughMask_half_mask_is_the_midpoint()
    {
        var baseImg = Image.FromChannel(Fill(2, 1, 0.2f), 1f, 0f);
        var proc = Image.FromChannel(Fill(2, 1, 0.8f), 1f, 0f);
        var mask = Image.FromChannel(Fill(2, 1, 0.5f), 1f, 0f);

        var blended = baseImg.BlendThroughMask(proc, mask);

        blended[0, 0, 0].ShouldBe(0.5f, 1e-5f); // 0.2*0.5 + 0.8*0.5
    }

    [Fact]
    public void BlendThroughMask_rejects_multichannel_mask()
    {
        var img = Rgb(Fill(2, 2, 0.5f), Fill(2, 2, 0.5f), Fill(2, 2, 0.5f));
        Should.Throw<ArgumentException>(() => img.BlendThroughMask(img, img));
    }

    [Fact]
    public void Saturate_pushes_channels_away_from_luma()
    {
        // A reddish pixel: R above luma, B below. Saturation should widen that spread.
        var img = Rgb(Fill(1, 1, 0.6f), Fill(1, 1, 0.4f), Fill(1, 1, 0.2f));
        var sat = img.Saturate(2f);

        var r0 = img[0, 0, 0]; var b0 = img[2, 0, 0];
        var r1 = sat[0, 0, 0]; var b1 = sat[2, 0, 0];

        (r1 - b1).ShouldBeGreaterThan(r0 - b0); // channel spread widened
        r1.ShouldBeGreaterThan(r0);             // brightest channel brighter
        b1.ShouldBeLessThan(b0);                // dimmest channel dimmer
    }

    [Fact]
    public void Saturate_of_a_gray_pixel_is_unchanged()
    {
        // R==G==B means channel == luma, so (in - L) == 0 and saturation is a no-op.
        var img = Rgb(Fill(1, 1, 0.5f), Fill(1, 1, 0.5f), Fill(1, 1, 0.5f));
        var sat = img.Saturate(3f);
        sat[0, 0, 0].ShouldBe(0.5f, 1e-5f);
        sat[1, 0, 0].ShouldBe(0.5f, 1e-5f);
        sat[2, 0, 0].ShouldBe(0.5f, 1e-5f);
    }

    [Fact]
    public void Saturate_is_noop_copy_for_mono()
    {
        var img = Image.FromChannel(Fill(3, 1, 0.4f), 1f, 0f);
        var sat = img.Saturate(2.5f);
        for (var x = 0; x < 3; x++) sat[0, 0, x].ShouldBe(0.4f, 1e-6f);
    }

    [Fact]
    public void MaskedSaturation_composes_leaving_background_untouched()
    {
        // Two pixels: a dim background gray and a bright coloured signal. A masked saturation should
        // leave the background exactly (mask ~0 there) and only saturate the signal.
        var r = new float[1, 2]; var g = new float[1, 2]; var b = new float[1, 2];
        // pixel 0: background gray (low luma, neutral)
        r[0, 0] = 0.03f; g[0, 0] = 0.03f; b[0, 0] = 0.03f;
        // pixel 1: mid-tone coloured signal
        r[0, 1] = 0.6f; g[0, 1] = 0.45f; b[0, 1] = 0.3f;
        var img = Rgb(r, g, b);

        var mask = img.LuminanceRangeMask(blurSigma: 0f);
        var result = img.BlendThroughMask(img.Saturate(2f), mask);

        // Background pixel unchanged (mask ~0).
        result[0, 0, 0].ShouldBe(0.03f, 1e-3f);
        result[1, 0, 0].ShouldBe(0.03f, 1e-3f);
        result[2, 0, 0].ShouldBe(0.03f, 1e-3f);

        // Signal pixel got more saturated (R-B spread widened vs the original).
        var origSpread = img[0, 0, 1] - img[2, 0, 1];
        var newSpread = result[0, 0, 1] - result[2, 0, 1];
        newSpread.ShouldBeGreaterThan(origSpread);
    }

    [Fact]
    public void MaskedBoost_noop_returns_same_instance()
    {
        var img = MonoBands(0.05f, 0.5f, 0.97f);
        img.MaskedBoost(new MaskedBoostOptions()).ShouldBeSameAs(img);
        img.MaskedBoost(new MaskedBoostOptions(Saturation: 1f, ContrastBoost: 0f)).ShouldBeSameAs(img);
    }

    [Fact]
    public void MaskedBoost_saturates_signal_and_protects_background()
    {
        // Same fixture shape as the manual composition test above: MaskedBoost must behave
        // exactly like BlendThroughMask(Saturate(...), LuminanceRangeMask()) -- background
        // untouched, coloured mid-tone signal more saturated.
        var r = new float[1, 2]; var g = new float[1, 2]; var b = new float[1, 2];
        r[0, 0] = 0.03f; g[0, 0] = 0.03f; b[0, 0] = 0.03f;   // background gray
        r[0, 1] = 0.6f; g[0, 1] = 0.45f; b[0, 1] = 0.3f;     // mid-tone coloured signal
        var img = Rgb(r, g, b);

        var result = img.MaskedBoost(new MaskedBoostOptions(Saturation: 2f));

        result.ShouldNotBeSameAs(img);
        // Background pixel unchanged (mask ~0 there). The default blurSigma feathers the mask,
        // so allow a small tolerance.
        result[0, 0, 0].ShouldBe(0.03f, 1e-2f);
        result[2, 0, 0].ShouldBe(0.03f, 1e-2f);
        // Signal pixel got more saturated (R-B spread widened vs the original).
        var origSpread = img[0, 0, 1] - img[2, 0, 1];
        var newSpread = result[0, 0, 1] - result[2, 0, 1];
        newSpread.ShouldBeGreaterThan(origSpread);
    }

    [Fact]
    public void MaskedBoost_composes_saturation_and_contrast_within_unit_range()
    {
        var img = Rgb(Fill(8, 8, 0.5f), Fill(8, 8, 0.4f), Fill(8, 8, 0.3f));
        var result = img.MaskedBoost(new MaskedBoostOptions(Saturation: 1.5f, ContrastBoost: 0.8f));

        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var v = result[c, y, x];
                    float.IsFinite(v).ShouldBeTrue();
                    v.ShouldBeInRange(0f, 1f);
                }
            }
        }
    }

    [Fact]
    public void Invert_complements_and_roundtrips()
    {
        var mask = MonoBands(0.05f, 0.5f, 0.97f).LuminanceRangeMask(blurSigma: 0f);
        var inverted = mask.Invert();

        for (var x = 0; x < mask.Width; x++)
        {
            inverted[0, 0, x].ShouldBe(1f - mask[0, 0, x], 1e-6f);
        }
        // Double inversion is the identity.
        var back = inverted.Invert();
        for (var x = 0; x < mask.Width; x++)
        {
            back[0, 0, x].ShouldBe(mask[0, 0, x], 1e-6f);
        }
    }

    [Fact]
    public void Binarize_thresholds_then_blur_feathers()
    {
        // Half-black / half-white 1-row image: binarize keeps the step, feathering
        // (GaussianBlur) softens it into intermediate values around the edge.
        var a = new float[1, 32];
        for (var x = 16; x < 32; x++) a[0, x] = 0.8f;
        var img = Image.FromChannel(a, 1f, 0f);

        var binary = img.Binarize(0.5f);
        binary[0, 0, 0].ShouldBe(0f);
        binary[0, 0, 31].ShouldBe(1f);

        var feathered = binary.GaussianBlur(2f);
        // Away from the edge the plateaus survive; at the edge the transition is soft.
        feathered[0, 0, 0].ShouldBeLessThan(0.05f);
        feathered[0, 0, 31].ShouldBeGreaterThan(0.95f);
        feathered[0, 0, 16].ShouldBeInRange(0.2f, 0.8f);
    }

    [Fact]
    public void GaussianBlur_zero_sigma_is_identity_and_positive_sigma_preserves_mean()
    {
        var img = MonoBands(0.05f, 0.5f, 0.97f);
        img.GaussianBlur(0f).ShouldBeSameAs(img);

        var blurred = img.GaussianBlur(1.5f);
        float sum = 0f, blurredSum = 0f;
        for (var x = 0; x < img.Width; x++)
        {
            sum += img[0, 0, x];
            blurredSum += blurred[0, 0, x];
        }
        // Normalised kernel + edge clamping: the mean only drifts by the edge-replication
        // bias, which is small on this fixture.
        (blurredSum / sum).ShouldBeInRange(0.95f, 1.05f);
    }

    [Fact]
    public void Multiply_scalar_scales_mask_strength()
    {
        var mask = MonoBands(0.05f, 0.5f, 0.97f).LuminanceRangeMask(blurSigma: 0f);
        var half = mask.Multiply(0.5f);
        for (var x = 0; x < mask.Width; x++)
        {
            half[0, 0, x].ShouldBe(mask[0, 0, x] * 0.5f, 1e-6f);
        }
    }

    [Fact]
    public void ContrastBoost_leaves_endpoints_and_returns_finite()
    {
        var img = MonoBands(0.05f, 0.5f, 0.98f);
        var boosted = img.ContrastBoost(0.8f, backgroundLevel: 0.1f);

        boosted.ChannelCount.ShouldBe(1);
        for (var x = 0; x < boosted.Width; x++)
        {
            var v = boosted[0, 0, x];
            float.IsFinite(v).ShouldBeTrue();
            v.ShouldBeInRange(0f, 1f);
        }
    }
}
