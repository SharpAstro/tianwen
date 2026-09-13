using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The stats that POSITION a stretch curve must carry every transform the shader applies BEFORE that
/// curve. The white balance half was always threaded through; background neutralisation was not, and
/// the omission is invisible until a frame has both a non-neutral calibration and a background that
/// is not at zero.
/// <para>
/// Numbers are the Sag Triplet HOO composite's: an Astro Pixel Processor master whose three channel
/// backgrounds are already equal to six digits, SPCC fitting (1.443, 1.000, 1.228) from 2805 stars.
/// Rendered with the neutralisation known to the shader but not to the curve, it came out a solid
/// crimson field -- 169/0/33 against a neutral 25/25/25 -- on the GPU and the CPU mirror alike.
/// </para>
/// </summary>
public class StretchBackgroundNeutralizationTests
{
    private const float Bg = 0.065827f;
    private const float Mad = 9.4126e-5f;

    private static readonly (float R, float G, float B) Spcc = (1.4429f, 1.0000f, 1.2284f);

    [Theory]
    [InlineData(StretchMode.Linked)]
    [InlineData(StretchMode.Unlinked)]
    public void ACalibratedSkyStillRendersGrey(StretchMode mode)
    {
        var u = Solve(mode, out var gains);

        // The gains exist to make the POST-WB background equal in all three channels; that is the
        // property under test, so assert it on the RENDER rather than on the gains themselves.
        var r = Image.StretchChannelCpu(Bg, 0, u);
        var g = Image.StretchChannelCpu(Bg, 1, u);
        var b = Image.StretchChannelCpu(Bg, 2, u);

        r.ShouldBe(g, 0.01f, $"R must match G; gains {gains}, uniforms shadows {u.Shadows}");
        b.ShouldBe(g, 0.01f, $"B must match G; gains {gains}, uniforms shadows {u.Shadows}");

        // And grey rather than uniformly black or blown: a curve anchored in the wrong place clamps
        // the dim channel to 0 and saturates the bright one, which "all three agree" alone would miss
        // if they ever agreed at an extreme.
        g.ShouldBeInRange(0.02f, 0.6f, "the sky should land in the low midtones, not at either rail");
    }

    /// <summary>
    /// The neutralisation must reach the curve, not merely the shader. Pinned separately because the
    /// render assertion above would also pass if the gains were silently dropped everywhere.
    /// </summary>
    [Fact]
    public void TheGainsReachTheUniforms()
    {
        var u = Solve(StretchMode.Linked, out var gains);

        u.BackgroundNeutralization.ShouldBe(gains);
        // Equal channel backgrounds under an unequal WB: the shared Linked curve can only sit on one
        // level, so the neutralisation is what makes that level the right one for all three.
        u.Shadows.R.ShouldBe(u.Shadows.G, 1e-6f);
        u.Shadows.B.ShouldBe(u.Shadows.G, 1e-6f);
    }

    /// <summary>With no calibration in play nothing is neutralised, and the render is unchanged.</summary>
    [Fact]
    public void AnUncalibratedFrameIsUntouched()
    {
        var stats = Stats();
        var u = StretchSolver.ComputeStretchUniforms(
            StretchMode.Linked, new StretchParameters(0.1, -5.0), stats, null, 1f);

        u.BackgroundNeutralization.ShouldBe((1f, 1f, 1f));
        u.WhiteBalance.ShouldBe((1f, 1f, 1f));
    }

    /// <summary>
    /// GreenPivot, NOT the Mean default, and the choice is load-bearing. Under Mean with equal
    /// channel backgrounds the pivot level IS the mean of the WB-scaled medians, which is exactly
    /// what Linked's joint anchor already computes -- so a Linked case built on Mean passes whether
    /// or not the neutralisation reaches the curve, and pins nothing. (That coincidence is also why
    /// Linked looked correct in the first measurements while Unlinked was visibly broken.) GreenPivot
    /// puts the neutralised level at green's own background, 0.0658 against the joint anchor's
    /// 0.0804, about 130 MADs apart, so the Linked case now fails if the fix is removed.
    /// </summary>
    private static StretchUniforms Solve(StretchMode mode, out (float R, float G, float B) gains)
    {
        Span<float> perChannelBg = [Bg, Bg, Bg];
        gains = BackgroundNeutralization.ComputeGains(perChannelBg, BackgroundNeutralizationMethod.GreenPivot, Spcc);

        return StretchSolver.ComputeStretchUniforms(
            mode,
            new StretchParameters(0.1, -5.0),
            Stats(),
            lumaStats: null,
            imageMaxValue: 1f,
            whiteBalance: Spcc,
            lumaWeights: null,
            shaderWhiteBalance: Spcc,
            backgroundNeutralization: gains);
    }

    private static ChannelStretchStats[] Stats() =>
    [
        new ChannelStretchStats(0f, Bg, Mad),
        new ChannelStretchStats(0f, Bg, Mad),
        new ChannelStretchStats(0f, Bg, Mad),
    ];
}
