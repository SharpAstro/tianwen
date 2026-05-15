using System;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class LinearFitClipRejectorTests
{
    private static (int kept, float[] mask) RejectWithMask(ReadOnlySpan<float> column, float low = 3f, float high = 3f, int maxIter = 5)
    {
        var mask = new float[column.Length];
        var kept = new LinearFitClipRejector(low, high, maxIter).Reject(column, mask);
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
        // 9 values on a near-uniform line + 1 cosmic-ray-style outlier.
        // After sort the outlier sits at rank 9 (top), residual = (99 - fit) is huge.
        float[] column = [0.50f, 0.51f, 0.49f, 0.50f, 0.498f, 99.0f, 0.51f, 0.49f, 0.502f, 0.495f];

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(9);
        mask[5].ShouldBe(0f);
        for (var i = 0; i < column.Length; i++)
        {
            if (i != 5) mask[i].ShouldBe(1f);
        }
    }

    [Fact]
    public void GaussianBulkPlusHighOutlier_RejectsTheOutlier()
    {
        // LFC's actual sweet spot: Gaussian-distributed bulk (mu=0.5,
        // sigma=0.05) plus one well-separated high outlier. When sorted,
        // the bulk forms an S-curve whose middle is approximately linear,
        // so LSQ tracks the bulk well and the outlier at the top tail has
        // a residual well above the MAD-derived sigma. Deterministic seed
        // so the test is reproducible.
        var rng = new Random(42);
        var column = new float[21];
        for (var i = 0; i < 20; i += 2)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            var r = Math.Sqrt(-2.0 * Math.Log(u1));
            column[i] = (float)(0.5 + 0.05 * r * Math.Cos(2.0 * Math.PI * u2));
            column[i + 1] = (float)(0.5 + 0.05 * r * Math.Sin(2.0 * Math.PI * u2));
        }
        column[20] = 1.5f; // outlier ~20 sigma above the bulk mean

        var (kept, mask) = RejectWithMask(column);

        mask[20].ShouldBe(0f);
        kept.ShouldBeLessThanOrEqualTo(20);
        kept.ShouldBeGreaterThanOrEqualTo(18);
    }

    [Fact]
    public void LinearRamp_KeepsAll()
    {
        // Perfectly linear ramp -- residuals are all zero -> degenerate-MAD
        // break-out means no rejections. Crucial regression test: a 244-frame
        // column with steady transparency drift should NOT be eaten by LFC.
        var column = new float[20];
        for (var i = 0; i < column.Length; i++) column[i] = 0.1f + 0.04f * i;

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(column.Length);
        foreach (var m in mask) m.ShouldBe(1f);
    }

    [Fact]
    public void AsymmetricSigma_StrictHighOnly()
    {
        // 11 clean ramp samples + 1 modest high outlier. Strict high (1
        // sigma) rejects it; loose high (20 sigma) keeps it.
        var column = new float[12];
        for (var i = 0; i < 11; i++) column[i] = 0.1f + 0.04f * i;
        column[11] = 0.85f; // sits well above the LSQ line that fits ranks 0..10

        var (_, maskStrict) = RejectWithMask(column, low: 10f, high: 1f, maxIter: 1);
        var (_, maskLoose) = RejectWithMask(column, low: 10f, high: 20f, maxIter: 1);

        maskStrict[11].ShouldBe(0f);
        maskLoose[11].ShouldBe(1f);
    }

    [Fact]
    public void TooFewSamples_ReturnsAllKept()
    {
        // < MinSamples (5) -> no rejection.
        float[] column = [0.5f, 100.0f, 0.5f, 0.5f];

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(4);
        foreach (var m in mask) m.ShouldBe(1f);
    }

    [Fact]
    public void MaskLengthMismatch_Throws()
    {
        var rejector = new LinearFitClipRejector();
        var column = new float[5];
        var mask = new float[4];

        Should.Throw<ArgumentException>(() => rejector.Reject(column, mask));
    }
}
