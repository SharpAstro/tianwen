using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Linked-vs-Unlinked contract, which is what decides whether a white balance is VISIBLE.
///
/// <para>Both modes hand the shader the same per-channel uniform slots, so the whole difference
/// between them lives in what <see cref="StretchSolver.ComputeStretchUniforms"/> writes into those
/// slots -- there is no branch in the GLSL and none in <see cref="Image.StretchChannelCpu"/>. That
/// makes this the only place the distinction can be pinned, and the only place it can silently
/// collapse.</para>
///
/// <para>It did collapse: Linked used to replicate channel 0's STATS and then scale each copy by
/// that channel's own WB multiplier, which produces three different curves whose anchors track the
/// multipliers exactly and divide them back out. A photometric calibration then had no effect on a
/// linked render at all. These tests assert the industry behaviour instead -- PixInsight's linked
/// STF shares ONE curve across R/G/B -- and, just as importantly, assert that Unlinked KEEPS
/// absorbing the auto calibration, because that is what an unlinked stretch is for.</para>
/// </summary>
public class StretchLinkedWhiteBalanceTests
{
    // A background-dominated astro frame: tiny median, tiny MAD, already unit-scaled.
    private static readonly StretchParameters Params = new(0.25, -2.8);

    private static ChannelStretchStats[] FlatStats(float median = 0.01f, float mad = 0.002f)
        => [new(0f, median, mad), new(0f, median, mad), new(0f, median, mad)];

    private static StretchUniforms Solve(
        StretchMode mode,
        ChannelStretchStats[] stats,
        (float R, float G, float B)? autoWb = null,
        (float R, float G, float B)? shaderWb = null)
        => StretchSolver.ComputeStretchUniforms(
            mode, Params, stats, lumaStats: null, imageMaxValue: 1f,
            whiteBalance: autoWb, lumaWeights: null, shaderWhiteBalance: shaderWb);

    // Renders one sample through each channel's own uniform slot, which is exactly what the shader
    // and the CPU renderer both do.
    private static (float R, float G, float B) Render(float raw, in StretchUniforms u)
        => (Image.StretchChannelCpu(raw, 0, u),
            Image.StretchChannelCpu(raw, 1, u),
            Image.StretchChannelCpu(raw, 2, u));

    [Fact]
    public void GivenLinkedModeThenAllThreeChannelsShareOneCurve()
    {
        // Deliberately UNEQUAL per-channel stats plus a non-neutral WB: if any of that leaked into
        // the curve, the three slots would differ. Sharing the curve is the definition of linked.
        var stats = new ChannelStretchStats[]
        {
            new(0f, 0.008f, 0.0015f),
            new(0f, 0.011f, 0.0021f),
            new(0f, 0.014f, 0.0026f),
        };

        var u = Solve(StretchMode.Linked, stats, autoWb: (0.463f, 1f, 1.300f));

        u.Shadows.G.ShouldBe(u.Shadows.R);
        u.Shadows.B.ShouldBe(u.Shadows.R);
        u.Midtones.G.ShouldBe(u.Midtones.R);
        u.Midtones.B.ShouldBe(u.Midtones.R);
        u.Rescale.G.ShouldBe(u.Rescale.R);
        u.Rescale.B.ShouldBe(u.Rescale.R);
    }

    [Fact]
    public void GivenLinkedModeWhenWhiteBalanceAppliedThenTheRenderActuallyChanges()
    {
        // THE regression, stated as the symptom it presented as: three very different SPCC triples
        // rendering to within noise of each other. Identical channel stats isolate the WB as the
        // only thing that can separate the channels.
        var stats = FlatStats();
        const float sample = 0.05f;

        var neutral = Render(sample, Solve(StretchMode.Linked, stats, autoWb: (1f, 1f, 1f)));
        neutral.R.ShouldBe(neutral.G, 1e-6f, "a neutral WB must leave the channels together");
        neutral.B.ShouldBe(neutral.G, 1e-6f);

        // An SPCC triple measured off a real OSC master (SMC, LPS filter, average-spiral-galaxy
        // white reference). Red is pulled well down and blue pushed up.
        var calibrated = Render(sample, Solve(StretchMode.Linked, stats, autoWb: (0.463f, 1f, 1.300f)));

        calibrated.R.ShouldBeLessThan(calibrated.G, "wb.R < 1 must render red darker");
        calibrated.B.ShouldBeGreaterThan(calibrated.G, "wb.B > 1 must render blue brighter");

        // And it must be a real separation, not a rounding artefact. Under the replicated-stats form
        // every one of these came out equal to within float noise.
        (calibrated.G - calibrated.R).ShouldBeGreaterThan(0.02f);
        (calibrated.B - calibrated.G).ShouldBeGreaterThan(0.005f);
    }

    [Fact]
    public void GivenLinkedModeWhenWhiteBalanceDiffersThenTheTwoRendersDiffer()
    {
        // The comparison the way it was actually measured on the SMC master: hand the same image two
        // very different calibrations and check the output moves. This is the assertion that fails
        // against the old code even when the per-channel ordering above happens to survive.
        var stats = FlatStats();
        const float sample = 0.05f;

        var a = Render(sample, Solve(StretchMode.Linked, stats, autoWb: (0.463f, 1f, 1.300f)));
        var b = Render(sample, Solve(StretchMode.Linked, stats, autoWb: (1.300f, 1f, 0.463f)));

        // Swapping R and B must swap which channel is brighter.
        (a.R < a.B).ShouldBeTrue();
        (b.R > b.B).ShouldBeTrue();
        MathF.Abs(a.R - b.R).ShouldBeGreaterThan(0.02f);
    }

    [Fact]
    public void GivenUnlinkedModeWhenAutoCalibrationAppliedThenItIsStillAbsorbed()
    {
        // NOT a bug, and the reason Linked had to be fixed instead of Unlinked: an unlinked STF
        // normalises each channel against its own stats, so any per-channel gain folded into those
        // stats cancels and the background stays neutral. That is the entire purpose of the mode.
        var stats = FlatStats();
        const float sample = 0.05f;

        var neutral = Render(sample, Solve(StretchMode.Unlinked, stats, autoWb: (1f, 1f, 1f)));
        var calibrated = Render(sample, Solve(StretchMode.Unlinked, stats, autoWb: (0.463f, 1f, 1.300f)));

        calibrated.R.ShouldBe(neutral.R, 0.01f);
        calibrated.B.ShouldBe(neutral.B, 0.01f);
    }

    [Fact]
    public void GivenUnlinkedModeWhenManualWhiteBalanceAppliedThenItSurvives()
    {
        // The other half of the shaderWhiteBalance split: the manual triple multiplies but does not
        // scale the stats, so the per-channel curve cannot re-absorb it. This is what keeps the WB
        // sliders live in Unlinked and on a linear SER.
        var stats = FlatStats();
        const float sample = 0.05f;

        var plain = Render(sample, Solve(StretchMode.Unlinked, stats, autoWb: (1f, 1f, 1f)));
        var manual = Render(sample, Solve(StretchMode.Unlinked, stats,
            autoWb: (1f, 1f, 1f), shaderWb: (0.6f, 1f, 1.4f)));

        manual.R.ShouldBeLessThan(plain.R);
        manual.B.ShouldBeGreaterThan(plain.B);
    }

    [Fact]
    public void GivenMonoImageThenLinkedIsUnchangedByTheSharedCurveRewrite()
    {
        // A one-channel image has no joint statistic to average, so the shared-curve path must
        // reduce exactly to "channel 0 scaled by wb.R" -- the pre-rewrite arithmetic. The shader's
        // mono branch only ever reads slot 0, so anything else here would be a silent change to
        // every mono render.
        ChannelStretchStats[] mono = [new(0f, 0.01f, 0.002f)];

        var u = Solve(StretchMode.Linked, mono, autoWb: (1.25f, 1f, 1f));
        var expected = Image.ComputeStretchParameters(0.01f * 1.25f, 0.002f * 1.25f,
            Params.Factor, Params.ShadowsClipping);

        u.Shadows.R.ShouldBe((float)expected.Shadows, 1e-6f);
        u.Midtones.R.ShouldBe((float)expected.Midtones, 1e-6f);
        u.Rescale.R.ShouldBe((float)expected.Rescale, 1e-6f);
    }

    [Fact]
    public void GivenLinkedModeThenTheSharedCurveIsPositionedOnTheChannelMean()
    {
        // Pins the joint statistic itself: PixInsight's linked auto-stretch averages the per-channel
        // medians and MADs. Positioning the shared curve on channel 0 alone (the obvious shortcut)
        // would clip whichever channels sit below it.
        var stats = new ChannelStretchStats[]
        {
            new(0f, 0.006f, 0.001f),
            new(0f, 0.010f, 0.002f),
            new(0f, 0.020f, 0.003f),
        };

        var u = Solve(StretchMode.Linked, stats, autoWb: (1f, 1f, 1f));
        var expected = Image.ComputeStretchParameters(
            (0.006f + 0.010f + 0.020f) / 3f, (0.001f + 0.002f + 0.003f) / 3f,
            Params.Factor, Params.ShadowsClipping);

        u.Shadows.R.ShouldBe((float)expected.Shadows, 1e-6f);
        u.Midtones.R.ShouldBe((float)expected.Midtones, 1e-6f);
    }

    [Theory]
    [InlineData(0.463f, 1f, 1.300f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(2.5f, 1f, 0.4f)]
    public void GivenAnAutoCalibrationThenComposeAndDecomposeRoundTrip(float ar, float ag, float ab)
    {
        // The slider contract: the panel shows the EFFECTIVE multiplier and a drag states a new
        // effective value, so the manual factor it solves back to must compose to exactly what the
        // user dropped. Anything else and the handle walks away from the pointer.
        var auto = (R: ar, G: ag, B: ab);
        var wanted = (R: 0.8f, G: 1.1f, B: 1.4f);

        var manual = StretchSolver.DecomposeWhiteBalance(auto, wanted);
        var composed = StretchSolver.ComposeWhiteBalance(auto, manual).ShouldNotBeNull();

        composed.R.ShouldBe(wanted.R, 1e-5f);
        composed.G.ShouldBe(wanted.G, 1e-5f);
        composed.B.ShouldBe(wanted.B, 1e-5f);
    }

    [Fact]
    public void GivenNoCalibrationThenTheEffectiveValueIsTheManualOne()
    {
        // With nothing calibrated the sliders are the whole white balance, so the decomposition must
        // be the identity -- otherwise the panel would report a different number from the render on
        // the commonest path of all.
        var neutral = (R: 1f, G: 1f, B: 1f);
        var manual = StretchSolver.DecomposeWhiteBalance(neutral, (0.7f, 1f, 1.25f));

        manual.R.ShouldBe(0.7f, 1e-6f);
        manual.G.ShouldBe(1f, 1e-6f);
        manual.B.ShouldBe(1.25f, 1e-6f);
    }
}
