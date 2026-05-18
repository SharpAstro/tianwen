using System;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class SigmaClipRejectorTests
{
    private static int Reject(ReadOnlySpan<float> column, float low = 3f, float high = 3f, int maxIter = 5)
    {
        var mask = new float[column.Length];
        return new SigmaClipRejector(low, high, maxIter).Reject(column, mask);
    }

    private static (int kept, float[] mask) RejectWithMask(ReadOnlySpan<float> column, float low = 3f, float high = 3f, int maxIter = 5)
    {
        var mask = new float[column.Length];
        var kept = new SigmaClipRejector(low, high, maxIter).Reject(column, mask);
        return (kept, mask);
    }

    [Fact]
    public void AllIdentical_NoRejections()
    {
        float[] column = [0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f];

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(8);
        foreach (var m in mask) m.ShouldBe(1f);
    }

    [Fact]
    public void OneFarOutlier_RejectsOutlierKeepsRest()
    {
        // 7 values around 0.5 with realistic noise spread, one cosmic-ray
        // hit at 99.0. Median 0.5, MAD ~0.01 -> 3-sigma bounds ~[0.456, 0.544]
        // -> 99.0 cleanly rejected.
        float[] column = [0.50f, 0.51f, 0.49f, 0.50f, 0.498f, 99.0f, 0.51f, 0.49f];

        var (kept, mask) = RejectWithMask(column, low: 3f, high: 3f);

        kept.ShouldBe(7);
        mask[5].ShouldBe(0f); // index of the 99.0
        // All others kept
        for (var i = 0; i < column.Length; i++)
        {
            if (i != 5) mask[i].ShouldBe(1f);
        }
    }

    [Fact]
    public void TwoFarOutliers_BothRejected()
    {
        // Slight noise on the 8 clean samples so MAD is non-zero (otherwise
        // the rejector breaks out on degenerate-MAD before reaching the bounds
        // check — there's no statistical spread to measure when 8/10 samples
        // are bit-identical).
        float[] column = [0.50f, 0.501f, 0.499f, 0.500f, 0.502f, 100.0f, 0.498f, -50.0f, 0.501f, 0.499f];

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(8);
        mask[5].ShouldBe(0f); // +100
        mask[7].ShouldBe(0f); // -50
    }

    [Fact]
    public void GaussianDistribution_RejectsRoughly1pctAt3Sigma()
    {
        // Generate 1000 N(0, 1) samples deterministically via Box-Muller.
        // 3-sigma rejection should keep ~99.7% (= ~3 rejections out of 1000).
        var column = new float[1000];
        var rng = new Random(42);
        for (var i = 0; i < column.Length; i += 2)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            var r = Math.Sqrt(-2.0 * Math.Log(u1));
            column[i] = (float)(r * Math.Cos(2.0 * Math.PI * u2));
            if (i + 1 < column.Length)
            {
                column[i + 1] = (float)(r * Math.Sin(2.0 * Math.PI * u2));
            }
        }

        var kept = Reject(column, low: 3f, high: 3f);

        // Iterated 3-sigma converges to keep slightly fewer than 99.7% (each
        // iteration tightens std). Expect a small chunk rejected; tolerance
        // is loose because iterated clip is what we want to test, not the
        // exact statistical bound.
        kept.ShouldBeInRange(960, 1000);
    }

    [Fact]
    public void AsymmetricSigma_StrictHighCatchesLooseHighDoesnt()
    {
        // 9 clean samples ~0.5 + 1 borderline-high value at 0.65.
        // With HighSigma=2 (strict): the 0.65 is far enough above median (MAD-scaled)
        // to be rejected. With HighSigma=20 (very loose): 0.65 stays.
        // Validates that LowSigma / HighSigma act independently.
        float[] column = [0.50f, 0.51f, 0.49f, 0.50f, 0.51f, 0.49f, 0.50f, 0.51f, 0.49f, 0.65f];

        var (_, maskStrict) = RejectWithMask(column, low: 10f, high: 2f, maxIter: 1);
        var (_, maskLoose) = RejectWithMask(column, low: 10f, high: 20f, maxIter: 1);

        maskStrict[9].ShouldBe(0f); // strict high rejects 0.65
        maskLoose[9].ShouldBe(1f);  // loose high keeps 0.65
    }

    [Fact]
    public void DegenerateAllZeroVariance_NoRejections()
    {
        // Distribution has zero std after the first iteration -> no further
        // rejection possible. Should not crash (division by zero) and should
        // mark all as kept.
        float[] column = [0.5f, 0.5f, 0.5f];

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(3);
        mask.ShouldAllBe(m => m == 1f);
    }

    [Fact]
    public void TooFewSamples_ReturnsAllKept()
    {
        // < 3 samples -> no rejection (insufficient stats).
        float[] column = [0.5f, 100.0f];

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(2);
        mask.ShouldAllBe(m => m == 1f);
    }

    [Fact]
    public void MaskLengthMismatch_Throws()
    {
        var rejector = new SigmaClipRejector();
        var column = new float[5];
        var mask = new float[4];

        Should.Throw<ArgumentException>(() => rejector.Reject(column, mask));
    }

    [Fact]
    public void IteratesUntilConverged_NotBeyond()
    {
        // After iteration 1 excludes the cosmic ray, the remaining distribution
        // has a MUCH tighter spread. Iteration 2 may then catch the borderline
        // wings (0.55, 0.45) which are ~5x MAD from median. Just verify the
        // process terminates and rejects at least the obvious outlier.
        float[] column = [0.500f, 0.501f, 0.499f, 0.500f, 0.502f, 0.498f, 0.55f, 0.45f, 0.500f, 100.0f];

        var (kept, mask) = RejectWithMask(column, low: 3f, high: 3f, maxIter: 100);

        // The 100.0 outlier must be rejected.
        mask[9].ShouldBe(0f);
        // Loop must terminate, not run forever.
        kept.ShouldBeLessThan(column.Length);
    }

    [Fact]
    public void VectorTailHandling_LengthNotMultipleOfVectorCount()
    {
        // Vector<float>.Count is 4/8/16. Length 17 forces a scalar tail in
        // ComputeMaskedStats for all SIMD widths. Verify it doesn't trip up
        // the masked-sum math.
        var width = Vector<float>.Count;
        width.ShouldBeOneOf(4, 8, 16);

        // 17 values: 16 around 0.5, one outlier at 50.
        var column = new float[17];
        for (var i = 0; i < 16; i++) column[i] = 0.5f + 0.001f * (i - 8);
        column[16] = 50.0f;

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(16);
        mask[16].ShouldBe(0f);
    }
}
