using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Validation tests for the ported GHS curve (PLAN-ghs.md Phase 2).
/// Asserts the 4-branch math against:
///   - identity at LnD = 0,
///   - boundary conditions (curve passes through (0, 0) and (1, 1)),
///   - continuity at LP / SP / HP,
///   - branch reachability (B == -1, B &lt; 0 / != -1, B == 0, B &gt; 0
///     all evaluate without throwing or producing NaN),
///   - the "lifts dim bg above identity" semantic that the old impl
///     failed (key regression guard for the port).
/// </summary>
[Collection("Imaging")]
public class GhsCurveTests
{
    private static Image MakeRamp(int n)
    {
        var arr = new float[1, n];
        for (var i = 0; i < n; i++) arr[0, i] = (float)i / (n - 1);
        var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(60),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image([arr], BitDepth.Float32, 1.0f, 0f, 0f, meta);
    }

    [Fact]
    public void LnDZero_IsIdentity()
    {
        // exp(0) - 1 == 0 -> no stretch. The LUT should be the
        // diagonal y = x to floating-point precision.
        var ramp = MakeRamp(257);
        var stretched = ramp.GeneralizedHyperbolicStretch(
            lnD: 0.0, b: -1.0, sp: 0.5, lp: 0.0, hp: 1.0);

        var src = ramp.GetChannelSpan(0);
        var dst = stretched.GetChannelSpan(0);
        for (var i = 0; i < src.Length; i++)
            dst[i].ShouldBe(src[i], tolerance: 1e-4f);
    }

    [Fact]
    public void Endpoints_PassThroughZeroAndOne()
    {
        // The reference's coefficient derivation pins (0, 0) and (1, 1)
        // for any non-degenerate (D, B, SP, LP, HP). Probe across all
        // four B regimes to confirm.
        var ramp = MakeRamp(2); // just the two endpoints
        double[] bs = { -1.0, -0.5, 0.0, 1.0, 8.0 };
        foreach (var b in bs)
        {
            var stretched = ramp.GeneralizedHyperbolicStretch(
                lnD: 1.30, b: b, sp: 0.5, lp: 0.0, hp: 1.0);
            var dst = stretched.GetChannelSpan(0);
            dst[0].ShouldBe(0.0f, tolerance: 1e-3f, $"b={b}: y(0) must be 0");
            dst[^1].ShouldBe(1.0f, tolerance: 1e-3f, $"b={b}: y(1) must be 1");
        }
    }

    [Fact]
    public void AllFourBranchesEvaluate_Reachable()
    {
        // The four B regimes (B == -1 logarithmic, B < 0 power,
        // B == 0 exponential, B > 0 hyperbolic) each have their own
        // coefficient setup + per-pixel formula. This test ensures all
        // four are reachable from the new dispatch + produce finite,
        // monotonic output across the full input range.
        var ramp = MakeRamp(101);
        double[] bs = { -1.0, -0.5, 0.0, 1.0, 8.0 };
        foreach (var b in bs)
        {
            var stretched = ramp.GeneralizedHyperbolicStretch(
                lnD: 1.30, b: b, sp: 0.5, lp: 0.0, hp: 1.0);
            var dst = stretched.GetChannelSpan(0);
            var prev = -float.MaxValue;
            for (var i = 0; i < dst.Length; i++)
            {
                var v = dst[i];
                float.IsFinite(v).ShouldBeTrue($"b={b}: y({i / 100.0}) = {v}");
                v.ShouldBeGreaterThanOrEqualTo(prev, $"b={b}: not monotonic at {i / 100.0}");
                prev = v;
            }
        }
    }

    [Fact]
    public void LiftsDimBackground_PrimaryRegressionGuard()
    {
        // THE regression guard for the port. The pre-port BuildGhsLut
        // mapped input 0.05 -> output ~0.0007 (DARKENED) regardless of
        // intensity. With Paul (Polymath Astro)'s case-1 (linear ->
        // display) parameters from his video walkthrough -- B = 8
        // (hyperbolic branch), SP at lift-off, HP = 0.8 -- Cranfield's
        // GHS lifts dim bg substantially. Concretely: input 0.05
        // should map well above identity (closing the gap to the
        // 0.25 target median).
        var ramp = MakeRamp(1001);
        var stretched = ramp.GeneralizedHyperbolicStretch(
            lnD: 1.30, b: 8.0, sp: 0.003, lp: 0.0, hp: 0.8);

        var src = ramp.GetChannelSpan(0);
        var dst = stretched.GetChannelSpan(0);

        // input 0.05 (typical linear bg) -> output should be > 0.20.
        // Hand-derived value with these parameters is ~0.27 (~5.4x lift);
        // assert against >= 0.20 for slack. Old impl produced 0.0007 here.
        var bgIdx = 50;
        var outAtBg = dst[bgIdx];
        outAtBg.ShouldBeGreaterThan(0.20f,
            $"input={src[bgIdx]:F4}, output={outAtBg:F4}; " +
            "Cranfield GHS B=8 SP=0.003 HP=0.8 lnD=1.30 should lift bg above 0.2. " +
            "Pre-port impl crushed bg to ~0.0007; this catches a regression to that behaviour.");

        // input 0.1 and 0.2 also lift well above identity.
        dst[100].ShouldBeGreaterThan(0.30f, "input 0.10 should lift above 0.3");
        dst[200].ShouldBeGreaterThan(0.40f, "input 0.20 should lift above 0.4");
    }

    [Fact]
    public void Continuity_AtLpSpHp()
    {
        // The reference coefficient derivation makes the curve
        // continuous at LP / SP / HP by construction. After porting,
        // the curve evaluated just before and just after each
        // boundary should differ by less than ~1e-3 (loose tolerance
        // since the LUT is sampled at 1/65535 steps -- two adjacent
        // entries can differ by a finite amount, but no abrupt jump).
        const int LutSize = 65536;
        Span<float> lut = new float[LutSize];
        Image.BuildGhsLut(lut, lnD: 1.30, b: -1.0, sp: 0.57143, lp: 0.10, hp: 0.80357);

        int lpIdx = (int)(0.10 * (LutSize - 1));
        int spIdx = (int)(0.57143 * (LutSize - 1));
        int hpIdx = (int)(0.80357 * (LutSize - 1));

        // Compare LUT[lpIdx-1] vs LUT[lpIdx+1] -- single-bin jump must
        // stay small. Same for SP and HP. Tolerance generous enough to
        // accommodate the LUT's natural per-step derivative without
        // hiding a real discontinuity (which would be ~10x larger).
        const float maxStepJump = 0.005f;
        Math.Abs(lut[lpIdx + 1] - lut[lpIdx - 1]).ShouldBeLessThan(maxStepJump, "LP continuity");
        Math.Abs(lut[spIdx + 1] - lut[spIdx - 1]).ShouldBeLessThan(maxStepJump, "SP continuity");
        Math.Abs(lut[hpIdx + 1] - lut[hpIdx - 1]).ShouldBeLessThan(maxStepJump, "HP continuity");
    }

    [Fact]
    public void NegativeB_PowerBranch_Reachable()
    {
        // The B < 0 (B != -1) power branch is the only one the pre-port
        // codebase couldn't reach at all (ThrowIfNegativeOrZero on
        // asymmetry). Verify it now evaluates over a representative
        // input span with a non-extreme negative B.
        var ramp = MakeRamp(101);
        var stretched = ramp.GeneralizedHyperbolicStretch(
            lnD: 1.30, b: -0.5, sp: 0.5, lp: 0.0, hp: 1.0);
        var dst = stretched.GetChannelSpan(0);
        for (var i = 0; i < dst.Length; i++)
            float.IsFinite(dst[i]).ShouldBeTrue($"b=-0.5: y({i / 100.0}) = {dst[i]}");
        // Endpoints still snap to 0 / 1.
        dst[0].ShouldBe(0.0f, tolerance: 1e-3f);
        dst[^1].ShouldBe(1.0f, tolerance: 1e-3f);
    }
}
