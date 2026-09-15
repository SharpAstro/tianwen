using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="Image.Lanczos3Weights"/> evaluates the six taps of an axis from one fraction through an
/// angle addition instead of calling the window six times, so what has to be pinned is that the algebra
/// is the window, at the accuracy the warp path relies on.
/// </summary>
/// <remarks>
/// <para><b>Every assertion here is against a DOUBLE reference, never against
/// <see cref="Image.Lanczos3"/>.</b> That is the whole design of this file. Judged against the float
/// window, a correct identity and a subtly wrong one are indistinguishable: they disagree, and the
/// disagreement does not say which side moved. Two wrong diagnoses were written down that way before a
/// reference settled it in one run, which is also why the last test asserts the direction of the
/// difference rather than merely bounding it.</para>
/// </remarks>
public class Lanczos3WeightTests
{
    private const int Samples = 10_001;

    /// <summary>The window in double, with the tap offset formed exactly. The truth both forms are
    /// judged against; deliberately a transcription of the definition and nothing clever.</summary>
    private static double Reference(float f, int i)
    {
        var t = (double)f + 2 - i;
        if (t == 0.0)
        {
            return 1.0;
        }

        if (t <= -3.0 || t >= 3.0)
        {
            return 0.0;
        }

        var pt = Math.PI * t;
        return 3.0 * Math.Sin(pt) * Math.Sin(pt / 3.0) / (pt * pt);
    }

    [Fact]
    public void TheIdentityIsTheWindow()
    {
        Span<float> w = stackalloc float[6];
        var worst = 0.0;
        var worstAt = 0f;
        for (var k = 0; k < Samples; k++)
        {
            var f = k / (float)(Samples - 1);
            Image.Lanczos3Weights(f, w);
            for (var i = 0; i < 6; i++)
            {
                var e = Math.Abs(w[i] - Reference(f, i));
                if (e > worst)
                {
                    worst = e;
                    worstAt = f;
                }
            }
        }

        // Float has about 1.2e-7 of relative room at 1, so anything near that is the cast and not the
        // algebra. The bug this bound exists for missed by 2.4e-3, four orders clear of it.
        worst.ShouldBeLessThan(1e-6, $"worst at f = {worstAt}");
    }

    /// <summary>
    /// The sum of the six weights is what the resampler divides by, so an error there moves the OUTPUT
    /// LEVEL of every warped pixel rather than its shape. Worth its own assertion because a per-tap
    /// bound can be met while the errors all lean one way.
    /// </summary>
    [Fact]
    public void TheWeightsSumAsTheWindowDoes()
    {
        Span<float> w = stackalloc float[6];
        var worst = 0.0;
        for (var k = 0; k < Samples; k++)
        {
            var f = k / (float)(Samples - 1);
            Image.Lanczos3Weights(f, w);
            double mine = 0, reference = 0;
            for (var i = 0; i < 6; i++)
            {
                mine += w[i];
                reference += Reference(f, i);
            }

            worst = Math.Max(worst, Math.Abs(mine - reference));
        }

        worst.ShouldBeLessThan(2e-6);
    }

    /// <summary>
    /// A tap at an integer offset is 1 at zero and 0 elsewhere, which is what makes an integer shift a
    /// copy (<c>WarpInterpolationTests.AnIntegerShiftUnderLanczosReturnsThePixelsExactly</c>). Asserted
    /// exactly, since both branches are returned as literals rather than computed.
    /// </summary>
    [Fact]
    public void AZeroFractionIsAPassThrough()
    {
        Span<float> w = stackalloc float[6];
        Image.Lanczos3Weights(0f, w);
        for (var i = 0; i < 6; i++)
        {
            w[i].ShouldBe(i == 2 ? 1f : 0f, $"tap {i}");
        }
    }

    /// <summary>
    /// The identity replaced a form that was already correct, so the bar is not "close enough" but "no
    /// worse", and it clears it: evaluating from the fraction beats evaluating from a float-rounded tap
    /// offset. If a future edit makes this fail, the identity has become a trade rather than a win and
    /// the speed is not worth it on its own.
    /// </summary>
    [Fact]
    public void TheIdentityIsNearerTheWindowThanTheFloatFormItReplaced()
    {
        Span<float> w = stackalloc float[6];
        double worstIdentity = 0, worstDirect = 0;
        for (var k = 0; k < Samples; k++)
        {
            var f = k / (float)(Samples - 1);
            Image.Lanczos3Weights(f, w);
            for (var i = 0; i < 6; i++)
            {
                var reference = Reference(f, i);
                worstIdentity = Math.Max(worstIdentity, Math.Abs(w[i] - reference));
                worstDirect = Math.Max(worstDirect, Math.Abs(Image.Lanczos3(f + 2 - i) - reference));
            }
        }

        worstIdentity.ShouldBeLessThan(worstDirect,
            $"identity {worstIdentity:E2} against the float window's {worstDirect:E2}");
    }
}
