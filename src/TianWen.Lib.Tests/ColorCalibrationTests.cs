using Shouldly;
using System;
using TianWen.Lib.Imaging.ColorCalibration;
using Xunit;

namespace TianWen.Lib.Tests;

public class ColorCalibrationTests(ITestOutputHelper output)
{
    [Fact]
    public void ComputeMultipliers_BalancedStars_ReturnsIdentity()
    {
        // Stars with observed = expected → WB should be (1, 1, 1)
        var obsR = new[] { 100f, 200f, 300f, 400f, 500f };
        var obsG = new[] { 120f, 240f, 360f, 480f, 600f };
        var obsB = new[] { 80f, 160f, 240f, 320f, 400f };
        // Expected ratios match observed → identity
        var (expR, expG, expB) = (obsR, obsG, obsB);

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, expR, expG, expB);

        wbG.ShouldBe(1f);
        wbR.ShouldBeInRange(0.99f, 1.01f);
        wbB.ShouldBeInRange(0.99f, 1.01f);
    }

    [Fact]
    public void ComputeMultipliers_GreenCast_BoostsRedAndBlue()
    {
        // Simulate green cast: observed green is 2x brighter than expected relative to R/B
        var obsR = new[] { 100f, 200f, 300f, 400f, 500f };
        var obsG = new[] { 200f, 400f, 600f, 800f, 1000f }; // 2x too bright
        var obsB = new[] { 80f, 160f, 240f, 320f, 400f };
        // Expected: equal ratios (gray star B-V = 0.65 → roughly equal RGB)
        var expR = new[] { 100f, 200f, 300f, 400f, 500f };
        var expG = new[] { 100f, 200f, 300f, 400f, 500f };
        var expB = new[] { 100f, 200f, 300f, 400f, 500f };

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, expR, expG, expB);

        output.WriteLine($"WB: R={wbR:F3} G={wbG:F3} B={wbB:F3}");

        // Green is too bright → R and B need boosting
        wbG.ShouldBe(1f);
        wbR.ShouldBeGreaterThanOrEqualTo(1.5f, "red needs boosting when green cast exists");
        wbB.ShouldBeGreaterThanOrEqualTo(1.5f, "blue needs boosting when green cast exists");
    }

    [Fact]
    public void ComputeMultipliers_BlueCast_ReducesBlue()
    {
        // Simulate blue cast: observed blue is 2x brighter relative to expected
        var obsR = new[] { 100f, 200f, 300f, 400f, 500f };
        var obsG = new[] { 120f, 240f, 360f, 480f, 600f };
        var obsB = new[] { 160f, 320f, 480f, 640f, 800f }; // 2x too bright vs expected
        var expR = new[] { 100f, 200f, 300f, 400f, 500f };
        var expG = new[] { 100f, 200f, 300f, 400f, 500f };
        var expB = new[] { 100f, 200f, 300f, 400f, 500f };

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, expR, expG, expB);

        output.WriteLine($"WB: R={wbR:F3} G={wbG:F3} B={wbB:F3}");

        wbG.ShouldBe(1f);
        // wbR = expR/obsR * obsG/expG = 1.0 * 1.2 = 1.2 (mild red boost from green reference)
        wbR.ShouldBeGreaterThan(1f, "red boosted slightly since green ref is also above expected");
        // wbB = expB/obsB * obsG/expG = 0.5 * 1.2 = 0.6 → blue reduced
        wbB.ShouldBeLessThan(1f, "blue reduced when blue cast exists");
    }

    [Fact]
    public void ComputeMultipliers_RedCast_ReducesRed()
    {
        var obsR = new[] { 200f, 400f, 600f, 800f, 1000f }; // 2x too bright
        var obsG = new[] { 120f, 240f, 360f, 480f, 600f };
        var obsB = new[] { 80f, 160f, 240f, 320f, 400f };
        var expR = new[] { 100f, 200f, 300f, 400f, 500f };
        var expG = new[] { 100f, 200f, 300f, 400f, 500f };
        var expB = new[] { 100f, 200f, 300f, 400f, 500f };

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, expR, expG, expB);

        output.WriteLine($"WB: R={wbR:F3} G={wbG:F3} B={wbB:F3}");

        wbG.ShouldBe(1f);
        // wbR = expR/obsR * obsG/expG = 0.5 * 1.2 = 0.6 → red reduced
        wbR.ShouldBeLessThan(1f, "red reduced when red cast exists");
        // wbB = expB/obsB * obsG/expG = 1.25 * 1.2 = 1.5 → blue boosted
        wbB.ShouldBeGreaterThan(1f, "blue boosted when red cast exists");
    }

    [Fact]
    public void ComputeMultipliers_ClampsToRange()
    {
        // Extreme cast — should clamp to [0.1, 10]
        var obsR = new[] { 1f };
        var obsG = new[] { 1000f }; // huge green cast
        var obsB = new[] { 1f };
        var exp = new[] { 1f };

        var (wbR, wbG, wbB) = Tycho2ColorCalibration.ComputeMultipliers(
            obsR, obsG, obsB, exp, exp, exp);

        output.WriteLine($"WB: R={wbR:F3} G={wbG:F3} B={wbB:F3}");
        wbR.ShouldBe(10f, "clamped to max");
        wbB.ShouldBe(10f, "clamped to max");
    }
}
