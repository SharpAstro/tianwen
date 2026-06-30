using System;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

public class FlatExposureSolverTests
{
    private static readonly TimeSpan Min = TimeSpan.FromSeconds(0.1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(0.50)]  // dead on
    [InlineData(0.46)]  // just inside the band
    [InlineData(0.54)]
    public void WithinTolerance_CapturesAtCurrentExposure(double measured)
    {
        var current = TimeSpan.FromSeconds(2);

        var d = FlatExposureSolver.Solve(measured, current, targetFraction: 0.5, tolerance: 0.05, Min, Max, attempt: 0, maxAttempts: 6);

        d.Action.ShouldBe(FlatExposureAction.Capture);
        d.NextExposure.ShouldBe(current);
        d.Reason.ShouldBeNull();
    }

    [Fact]
    public void TooDim_AdjustsExposureUp_LinearScale()
    {
        // level 0.1 at 1s, target 0.5 -> scale x5 -> 5s
        var d = FlatExposureSolver.Solve(0.1, TimeSpan.FromSeconds(1), 0.5, 0.05, Min, Max, attempt: 0, maxAttempts: 6);

        d.Action.ShouldBe(FlatExposureAction.Adjust);
        d.NextExposure.TotalSeconds.ShouldBe(5.0, 1e-6);
    }

    [Fact]
    public void TooBright_AdjustsExposureDown_LinearScale()
    {
        // level 0.9 at 1s, target 0.5 -> scale 0.5/0.9 -> ~0.556s
        var d = FlatExposureSolver.Solve(0.9, TimeSpan.FromSeconds(1), 0.5, 0.05, Min, Max, attempt: 0, maxAttempts: 6);

        d.Action.ShouldBe(FlatExposureAction.Adjust);
        d.NextExposure.TotalSeconds.ShouldBe(0.5 / 0.9, 1e-6);
    }

    [Fact]
    public void Converges_WithinBrackets_UnderLinearPanelModel()
    {
        // Linear panel: level = 0.1 * seconds, saturating at 0.98.
        static double Level(TimeSpan e) => Math.Min(0.1 * e.TotalSeconds, 0.98);

        const double target = 0.5, tol = 0.05;
        const int maxBrackets = 6;
        var exposure = TimeSpan.FromSeconds(1);

        FlatExposureDecision d = default;
        var iterations = 0;
        for (var attempt = 0; attempt < maxBrackets; attempt++)
        {
            iterations++;
            d = FlatExposureSolver.Solve(Level(exposure), exposure, target, tol, Min, Max, attempt, maxBrackets);
            if (d.Action != FlatExposureAction.Adjust) break;
            exposure = d.NextExposure;
        }

        d.Action.ShouldBe(FlatExposureAction.Capture);
        Level(d.NextExposure).ShouldBe(target, tol);
        iterations.ShouldBeLessThanOrEqualTo(maxBrackets);
    }

    [Fact]
    public void SaturatedAtMinExposure_Fails_PanelTooBright()
    {
        // Already at the minimum exposure but the panel is still blowing out.
        var d = FlatExposureSolver.Solve(0.95, Min, 0.5, 0.05, Min, Max, attempt: 0, maxAttempts: 6);

        d.Action.ShouldBe(FlatExposureAction.Fail);
        d.NextExposure.ShouldBe(Min);
        d.Reason.ShouldNotBeNull().ShouldContain("too bright");
    }

    [Fact]
    public void TooDimAtMaxExposure_Fails_PanelTooDim()
    {
        // Already at the maximum exposure but the panel is still too dim.
        var d = FlatExposureSolver.Solve(0.05, Max, 0.5, 0.05, Min, Max, attempt: 0, maxAttempts: 6);

        d.Action.ShouldBe(FlatExposureAction.Fail);
        d.NextExposure.ShouldBe(Max);
        d.Reason.ShouldNotBeNull().ShouldContain("too dim");
    }

    [Fact]
    public void OutOfBrackets_Fails_EvenWhenAdjustmentWouldBePossible()
    {
        // Off-target but on the final allowed attempt -> fail rather than adjust.
        var d = FlatExposureSolver.Solve(0.1, TimeSpan.FromSeconds(1), 0.5, 0.05, Min, Max, attempt: 5, maxAttempts: 6);

        d.Action.ShouldBe(FlatExposureAction.Fail);
        d.Reason.ShouldNotBeNull().ShouldContain("did not converge");
    }

    [Fact]
    public void VeryDim_AdjustsClampedToMax_NotFail_WhenRoomRemains()
    {
        // level 0.01 at 10s would want 500s; clamps to 30s and keeps trying (10s != 30s).
        var d = FlatExposureSolver.Solve(0.01, TimeSpan.FromSeconds(10), 0.5, 0.05, Min, Max, attempt: 0, maxAttempts: 6);

        d.Action.ShouldBe(FlatExposureAction.Adjust);
        d.NextExposure.ShouldBe(Max);
    }

    [Fact]
    public void VeryBright_AdjustsClampedToMin_NotFail_WhenRoomRemains()
    {
        // level 0.99 at 1s would want ~0.505s; well above min so it just adjusts down.
        var d = FlatExposureSolver.Solve(0.99, TimeSpan.FromSeconds(1), 0.5, 0.05, Min, Max, attempt: 0, maxAttempts: 6);

        d.Action.ShouldBe(FlatExposureAction.Adjust);
        d.NextExposure.TotalSeconds.ShouldBe(0.5 / 0.99, 1e-6);
    }

    [Fact]
    public void NearZeroMeasurement_DoesNotOverflow_ClampsToMax()
    {
        // A black metering frame must not divide-by-zero into an infinite exposure.
        var d = FlatExposureSolver.Solve(0.0, TimeSpan.FromSeconds(1), 0.5, 0.05, Min, Max, attempt: 0, maxAttempts: 6);

        d.Action.ShouldBe(FlatExposureAction.Adjust);
        d.NextExposure.ShouldBe(Max);
    }
}
