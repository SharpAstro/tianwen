using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for the pure-math stretch primitives shipped in commit 7d8781b
/// without unit coverage: <see cref="Image.FixedMidtonesStretch"/>,
/// <see cref="Image.StarStretch"/>, <see cref="Image.ReduceBackground"/>,
/// <see cref="Image.CompressHighlights"/>,
/// <see cref="Image.EstimateBackgroundPeak"/>, and
/// <see cref="Image.EstimateRisingEdge"/>. All operate per-pixel via a
/// LUT or closed-form curve and underpin the AI sharpen dual-stretch
/// CLI flow; this file backfills the missing safety net.
/// </summary>
[Collection("Imaging")]
public class ImageStretchPrimitivesTests
{
    private const float Eps = 1e-5f;

    private static Image MakeGradient(int width = 256)
    {
        // Single-row image, channel 0 = linear ramp [0, 1]. Cheap probe for
        // curve-shape assertions (endpoint preservation, monotonicity,
        // identity behaviour at known parameter values).
        var arr = new float[1, width];
        for (var x = 0; x < width; x++) arr[0, x] = (float)x / (width - 1);
        return new Image([arr], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, default);
    }

    private static Image MakeConstant(float value, int width = 4, int height = 4)
    {
        var arr = new float[height, width];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                arr[y, x] = value;
        return new Image([arr], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, default);
    }

    // --- FixedMidtonesStretch -------------------------------------------------

    [Fact]
    public void FixedMidtonesStretch_HalfMidtonesIsIdentity()
    {
        // MTF(0.5, x) = (0.5-1)x / ((2*0.5-1)x - 0.5) = -0.5x / -0.5 = x for all x.
        var src = MakeGradient(width: 256);
        var stretched = src.FixedMidtonesStretch(midtones: 0.5);
        for (var x = 0; x < 256; x++)
        {
            stretched[0, 0, x].ShouldBe(src[0, 0, x], tolerance: Eps);
        }
    }

    [Fact]
    public void FixedMidtonesStretch_LowMidtonesLiftsShadows()
    {
        // midtones < 0.5 -> MTF curve sits above identity for x in (0, 1).
        // Endpoints (0 -> 0, 1 -> 1) and monotonicity must still hold.
        var src = MakeGradient(width: 256);
        var stretched = src.FixedMidtonesStretch(midtones: 0.25);
        stretched[0, 0, 0].ShouldBe(0f, tolerance: Eps);
        stretched[0, 0, 255].ShouldBe(1f, tolerance: Eps);
        for (var x = 1; x < 255; x++)
        {
            stretched[0, 0, x].ShouldBeGreaterThan(src[0, 0, x] - Eps);
        }
        for (var x = 1; x < 256; x++)
        {
            stretched[0, 0, x].ShouldBeGreaterThanOrEqualTo(stretched[0, 0, x - 1] - Eps);
        }
    }

    // --- StarStretch (Frank Sackenheim wrapper around FixedMidtonesStretch) ---

    [Fact]
    public void StarStretch_ZeroAmountIsIdentity()
    {
        // amount = 0 -> midtones = 1 / (3^0 + 1) = 0.5 -> identity MTF.
        var src = MakeGradient();
        var stretched = src.StarStretch(amount: 0);
        for (var x = 0; x < 256; x++)
        {
            stretched[0, 0, x].ShouldBe(src[0, 0, x], tolerance: Eps);
        }
    }

    [Theory]
    // For a pixel at x = midtones, the MTF curve evaluates to 0.5 by construction
    // (midtones balance is the input value the curve maps to 0.5). So we feed in
    // midtones-the-value and assert the output is 0.5.
    //
    //   amount  factor=3^amount  midtones=1/(factor+1)
    //   0.0      1.0              0.5
    //   1.0      3.0              0.25
    //   2.0      9.0              0.1
    //   5.0      243.0            0.00410
    [InlineData(1.0, 0.25)]
    [InlineData(2.0, 0.1)]
    [InlineData(5.0, 1.0 / 244.0)]
    public void StarStretch_OutputAtMidtonesIsHalf(double amount, double midtones)
    {
        var src = MakeConstant(value: (float)midtones);
        var stretched = src.StarStretch(amount);
        stretched[0, 0, 0].ShouldBe(0.5f, tolerance: 1e-4f);
    }

    // --- ReduceBackground (5-point cubic Hermite S-curve) ---------------------

    [Fact]
    public void ReduceBackground_MidpointIsIdentity()
    {
        // The S-curve has (0.5, 0.5) as a control point by construction.
        var src = MakeConstant(value: 0.5f);
        var stretched = src.ReduceBackground(backgroundPeak: 0.1, compression: 0.36);
        stretched[0, 0, 0].ShouldBe(0.5f, tolerance: 1e-3f);
    }

    [Fact]
    public void ReduceBackground_AtBackgroundPeakLandsAtBgPeakTimesCompression()
    {
        // The low control point is exactly (bg, bg*c); a pixel at that input
        // should land at that output (cubic Hermite passes through controls).
        const float bgPeak = 0.1f;
        const double compression = 0.36;
        var src = MakeConstant(value: bgPeak);
        var stretched = src.ReduceBackground(backgroundPeak: bgPeak, compression);
        stretched[0, 0, 0].ShouldBe((float)(bgPeak * compression), tolerance: 1e-3f);
    }

    [Fact]
    public void ReduceBackground_PreservesEndpoints()
    {
        // 0 and 1 are explicit control points of the S-curve.
        var zero = MakeConstant(value: 0f);
        var one = MakeConstant(value: 1f);
        zero.ReduceBackground(0.1, 0.36)[0, 0, 0].ShouldBe(0f, tolerance: 1e-3f);
        one.ReduceBackground(0.1, 0.36)[0, 0, 0].ShouldBe(1f, tolerance: 1e-3f);
    }

    [Fact]
    public void ReduceBackground_Monotonic()
    {
        // Cubic Hermite with finite-difference tangents through monotone-ordered
        // y-values shouldn't introduce reversals, but the test guards against
        // any future change to the spline scheme.
        var src = MakeGradient(width: 256);
        var stretched = src.ReduceBackground(backgroundPeak: 0.1, compression: 0.36);
        for (var x = 1; x < 256; x++)
        {
            stretched[0, 0, x].ShouldBeGreaterThanOrEqualTo(stretched[0, 0, x - 1] - 1e-4f);
        }
    }

    // --- CompressHighlights (Reinhard-style soft knee) ------------------------

    [Fact]
    public void CompressHighlights_IdentityBelowKnee()
    {
        const float knee = 0.7f;
        var src = MakeGradient(width: 256);
        var stretched = src.CompressHighlights(knee, amount: 1.0);
        // Pixels strictly below the knee must be untouched.
        for (var x = 0; x < (int)(256 * 0.69); x++)
        {
            stretched[0, 0, x].ShouldBe(src[0, 0, x], tolerance: Eps);
        }
    }

    [Fact]
    public void CompressHighlights_AmountOneHalvesHeadroom()
    {
        // Per xmldoc: amount = 1 maps v = 1 to knee + (1-knee)/2.
        // For knee = 0.7: output(1) = 0.7 + 0.15 = 0.85.
        const float knee = 0.7f;
        var src = MakeConstant(value: 1f);
        var stretched = src.CompressHighlights(knee, amount: 1.0);
        stretched[0, 0, 0].ShouldBe(knee + (1f - knee) / 2f, tolerance: Eps);
    }

    [Fact]
    public void CompressHighlights_ZeroAmountIsIdentity()
    {
        // amount = 0 -> denominator (1 + 0*t) = 1, formula collapses to v.
        var src = MakeGradient();
        var stretched = src.CompressHighlights(knee: 0.7, amount: 0.0);
        for (var x = 0; x < 256; x++)
        {
            stretched[0, 0, x].ShouldBe(src[0, 0, x], tolerance: Eps);
        }
    }

    [Fact]
    public void CompressHighlights_Monotonic()
    {
        var src = MakeGradient(width: 256);
        var stretched = src.CompressHighlights(knee: 0.7, amount: 1.0);
        for (var x = 1; x < 256; x++)
        {
            stretched[0, 0, x].ShouldBeGreaterThanOrEqualTo(stretched[0, 0, x - 1] - Eps);
        }
    }

    // --- EstimateBackgroundPeak (256-bin histogram mode) ----------------------

    [Fact]
    public void EstimateBackgroundPeak_ConstantImage_ReturnsThatValue()
    {
        // All pixels at 0.3 -> mode bin centred on 0.298828 (idx 76 of 256).
        var src = MakeConstant(value: 0.3f, width: 256, height: 256);
        // 1-bin tolerance (= 1/256 ≈ 0.0039) is the resolution limit of the
        // 256-bin probe.
        src.EstimateBackgroundPeak().ShouldBe(0.3f, tolerance: 1f / 256);
    }

    [Fact]
    public void EstimateBackgroundPeak_BimodalReturnsDominantMode()
    {
        // 80% at 0.2, 20% at 0.8 -> mode should be near 0.2 regardless of the
        // brighter (but rarer) population at 0.8.
        const int Size = 200;
        var arr = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                arr[y, x] = y < (int)(Size * 0.8) ? 0.2f : 0.8f;
            }
        }
        var img = new Image([arr], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, default);
        img.EstimateBackgroundPeak().ShouldBe(0.2f, tolerance: 1f / 256);
    }

    // --- EstimateRisingEdge (4096-bin walk-left-from-peak) --------------------

    [Fact]
    public void EstimateRisingEdge_FindsLiftOffBetweenSparseTailAndPeak()
    {
        // 10% sparse tail at 0.05, 90% dominant peak at 0.4. Walking left from
        // the peak bin until the count drops below 5% of peak should land
        // just left of the peak bin -- the empty bins between tail and peak
        // are all below threshold.
        const int Size = 200;
        var arr = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                arr[y, x] = y < (int)(Size * 0.1) ? 0.05f : 0.4f;
            }
        }
        var img = new Image([arr], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, default);
        var edge = img.EstimateRisingEdge(thresholdFraction: 0.05f);
        // Edge must sit between tail (0.05) and peak (0.4), one bin below peak.
        edge.ShouldBeGreaterThan(0.05f);
        edge.ShouldBeLessThan(0.4f);
        // And specifically close to the peak (one 4096-bin width below).
        edge.ShouldBeGreaterThan(0.39f);
    }

    [Fact]
    public void EstimateRisingEdge_AllNaNReturnsZero()
    {
        // Degenerate channel -> peakCount = 0 -> early return 0.
        const int Size = 16;
        var arr = new float[Size, Size];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                arr[y, x] = float.NaN;
        var img = new Image([arr], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, default);
        img.EstimateRisingEdge().ShouldBe(0f);
    }
}
