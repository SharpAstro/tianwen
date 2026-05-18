using System;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class WinsorizedSigmaClipRejectorTests
{
    private static (int kept, float[] mask) RejectWithMask(ReadOnlySpan<float> column, float low = 3f, float high = 3f, int maxIter = 5)
    {
        var mask = new float[column.Length];
        var kept = new WinsorizedSigmaClipRejector(low, high, maxIter).Reject(column, mask);
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
        float[] column = [0.50f, 0.51f, 0.49f, 0.50f, 0.498f, 99.0f, 0.51f, 0.49f];

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(7);
        mask[5].ShouldBe(0f);
        for (var i = 0; i < column.Length; i++)
        {
            if (i != 5) mask[i].ShouldBe(1f);
        }
    }

    [Fact]
    public void LessAggressiveThanPlainSigmaClip_OnDitheredStarColumn()
    {
        // Synthetic column that mimics a dithered star pixel: half the
        // frames hit the PSF core (~0.9), the other half hit the wing
        // (~0.1), with mild Gaussian-ish noise. Plain sigma clip eats
        // the bright tail because the kept-set sigma collapses around
        // 0.1 after iteration 1. Winsorized should keep more frames.
        var column = new float[20];
        for (var i = 0; i < 10; i++) column[i] = 0.10f + 0.01f * i;
        for (var i = 10; i < 20; i++) column[i] = 0.85f + 0.01f * (i - 10);

        var (keptWin, _) = RejectWithMask(column);
        var plainMask = new float[column.Length];
        var keptPlain = new SigmaClipRejector(3f, 3f, 5).Reject(column, plainMask);

        keptWin.ShouldBeGreaterThanOrEqualTo(keptPlain);
    }

    [Fact]
    public void AsymmetricSigma_StrictHighLooseLow()
    {
        // 9 clean + 1 high outlier. Strict high rejects, loose high keeps.
        float[] column = [0.50f, 0.51f, 0.49f, 0.50f, 0.51f, 0.49f, 0.50f, 0.51f, 0.49f, 0.65f];

        var (_, maskStrict) = RejectWithMask(column, low: 10f, high: 2f, maxIter: 1);
        var (_, maskLoose) = RejectWithMask(column, low: 10f, high: 20f, maxIter: 1);

        maskStrict[9].ShouldBe(0f);
        maskLoose[9].ShouldBe(1f);
    }

    [Fact]
    public void TooFewSamples_ReturnsAllKept()
    {
        float[] column = [0.5f, 100.0f];

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(2);
        foreach (var m in mask) m.ShouldBe(1f);
    }

    [Fact]
    public void DegenerateAllZeroVariance_NoRejections()
    {
        float[] column = [0.5f, 0.5f, 0.5f];

        var (kept, mask) = RejectWithMask(column);

        kept.ShouldBe(3);
        foreach (var m in mask) m.ShouldBe(1f);
    }

    [Fact]
    public void MaskLengthMismatch_Throws()
    {
        var rejector = new WinsorizedSigmaClipRejector();
        var column = new float[5];
        var mask = new float[4];

        Should.Throw<ArgumentException>(() => rejector.Reject(column, mask));
    }
}
