using System;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pure convergence + twilight-direction tests for <see cref="SkyFlatExposureSolver"/>: in-tolerance
/// capture with a re-centred next exposure, recoverable adjust, and the direction-aware wait/stop
/// classification when pinned at an exposure bound (dawn brightening vs dusk darkening).
/// </summary>
public class SkyFlatExposureSolverTests
{
    private static readonly TimeSpan Min = TimeSpan.FromSeconds(0.1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);
    private const double Target = 0.5;
    private const double Tol = 0.05;

    [Theory]
    [InlineData(TwilightPeriod.Dawn)]
    [InlineData(TwilightPeriod.Dusk)]
    public void InTolerance_Captures(TwilightPeriod period)
    {
        var d = SkyFlatExposureSolver.Decide(period, 0.50, TimeSpan.FromSeconds(2), Target, Tol, Min, Max);
        d.Action.ShouldBe(SkyFlatAction.Capture);
        d.NextExposure.TotalSeconds.ShouldBe(2.0, 1e-6); // level == target -> exposure unchanged
    }

    [Fact]
    public void Capture_ReCentresNextExposure_TowardTarget()
    {
        // Slightly bright but in tolerance -> the next frame's exposure is re-centred shorter.
        var d = SkyFlatExposureSolver.Decide(TwilightPeriod.Dawn, 0.54, TimeSpan.FromSeconds(2), Target, Tol, Min, Max);
        d.Action.ShouldBe(SkyFlatAction.Capture);
        d.NextExposure.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        d.NextExposure.TotalSeconds.ShouldBe(2.0 * (0.5 / 0.54), 1e-6);
    }

    [Fact]
    public void Capture_ReCentre_ClampedToBounds()
    {
        // In tolerance at max exposure with the target above the measured level -> the re-centre would
        // exceed max, so it clamps to max.
        var d = SkyFlatExposureSolver.Decide(TwilightPeriod.Dawn, 0.48, Max, Target, Tol, Min, Max);
        d.Action.ShouldBe(SkyFlatAction.Capture);
        d.NextExposure.ShouldBe(Max);
    }

    [Fact]
    public void OffTargetButRecoverable_Adjusts()
    {
        var d = SkyFlatExposureSolver.Decide(TwilightPeriod.Dawn, 0.25, TimeSpan.FromSeconds(1), Target, Tol, Min, Max);
        d.Action.ShouldBe(SkyFlatAction.Adjust);
        d.NextExposure.TotalSeconds.ShouldBe(2.0, 1e-6); // 1s * (0.5 / 0.25)
    }

    [Fact]
    public void Dawn_TooDimAtMax_Waits()
    {
        // Below target, pinned at max exposure: the dawn sky is still brightening -> wait for it.
        var d = SkyFlatExposureSolver.Decide(TwilightPeriod.Dawn, 0.10, Max, Target, Tol, Min, Max);
        d.Action.ShouldBe(SkyFlatAction.Wait);
    }

    [Fact]
    public void Dawn_TooBrightAtMin_Stops()
    {
        // Above target, pinned at min exposure: the dawn sky only gets brighter -> window closed.
        var d = SkyFlatExposureSolver.Decide(TwilightPeriod.Dawn, 0.90, Min, Target, Tol, Min, Max);
        d.Action.ShouldBe(SkyFlatAction.Stop);
    }

    [Fact]
    public void Dusk_TooBrightAtMin_Waits()
    {
        // Above target, pinned at min exposure: the dusk sky is still darkening -> wait for it.
        var d = SkyFlatExposureSolver.Decide(TwilightPeriod.Dusk, 0.90, Min, Target, Tol, Min, Max);
        d.Action.ShouldBe(SkyFlatAction.Wait);
    }

    [Fact]
    public void Dusk_TooDimAtMax_Stops()
    {
        // Below target, pinned at max exposure: the dusk sky only gets darker -> window closed.
        var d = SkyFlatExposureSolver.Decide(TwilightPeriod.Dusk, 0.10, Max, Target, Tol, Min, Max);
        d.Action.ShouldBe(SkyFlatAction.Stop);
    }

    [Fact]
    public void Capture_NearZeroLevel_DoesNotDivideByZero()
    {
        // A tiny target with a wide tolerance so a ~0 level still captures; the re-centred next exposure
        // must stay finite and clamped into the exposure bounds.
        var d = SkyFlatExposureSolver.Decide(TwilightPeriod.Dawn, 0.0, TimeSpan.FromSeconds(1), targetFraction: 0.001, tolerance: 0.01, Min, Max);
        d.Action.ShouldBe(SkyFlatAction.Capture);
        d.NextExposure.ShouldBeGreaterThanOrEqualTo(Min);
        d.NextExposure.ShouldBeLessThanOrEqualTo(Max);
    }
}
