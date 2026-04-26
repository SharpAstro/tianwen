using Shouldly;
using TianWen.Lib.Astrometry.Focus;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="BacklashEstimator.InferFromVerification"/>. Synthesises a known
/// hyperbola, places the verification HFD at a chosen mechanical offset from bestPos, and
/// confirms the inferred backlash matches the ground truth.
/// </summary>
public class BacklashEstimatorTests
{
    // Hyperbola: HFD(p) = a * cosh(asinh((p_perfect - p) / b))
    //   a = 1.2 (HFD at perfect focus)
    //   b = 50  (slope coefficient — wider = shallower V-curve)
    //   p_perfect = 1000
    private const double A = 1.2;
    private const double B = 50.0;
    private const double PerfectFocus = 1000.0;

    private static readonly FocusSolution Fit = new(BestFocus: PerfectFocus, A: A, B: B, Error: 0.001, Iterations: 5);

    // Default focuser direction for tests: outward = positive, prefer outward (SCT-like).
    // PreferredSign = +1, so inferred mechanical position lies above bestPos when overshoot is too small.
    private static readonly FocusDirection PreferOutPositive = new(PreferOutward: true, OutwardIsPositive: true);

    /// <summary>
    /// Computes the HFD a perfectly-fitted hyperbola predicts at the given mechanical position.
    /// Used by tests to simulate verifyHfd at a known mechanical lag.
    /// </summary>
    private static double HfdAt(double mechanicalPos)
        => Hyperbola.CalculateValueAtPosition(mechanicalPos, PerfectFocus, A, B);

    [Fact]
    public void GivenVerifyAtPredictedMinimumWhenInferThenNoSignal()
    {
        // Mechanical == commanded → verifyHfd == hyperbola minimum → no inference possible.
        var bestPos = (int)PerfectFocus;
        var verifyHfd = HfdAt(bestPos);

        var (b, conf, _) = BacklashEstimator.InferFromVerification(
            Fit, bestPos, verifyHfd, currentOvershoot: 50, PreferOutPositive);

        b.ShouldBeNull();
        conf.ShouldBe(0f);
    }

    [Fact]
    public void GivenVerifyOffsetByKnownLagWhenInferThenRecoversBacklash()
    {
        // Synthesise: true backlash = 80 steps, current overshoot = 50 steps.
        // Lag = B - O = 30 steps in the preferred direction (positive here).
        // Mechanical position when verify is taken = bestPos + 30 = 1030.
        const int trueBacklash = 80;
        const int currentOvershoot = 50;
        var bestPos = (int)PerfectFocus;
        var mechanicalPos = bestPos + (trueBacklash - currentOvershoot); // = 1030
        var verifyHfd = HfdAt(mechanicalPos);

        var (b, conf, mech) = BacklashEstimator.InferFromVerification(
            Fit, bestPos, verifyHfd, currentOvershoot, PreferOutPositive);

        b.ShouldNotBeNull();
        b!.Value.ShouldBeInRange(trueBacklash - 1, trueBacklash + 1); // ±1 step rounding
        conf.ShouldBeGreaterThan(0.5f); // strong signal at this offset
        mech.ShouldBe(mechanicalPos, tolerance: 1.0);
    }

    [Fact]
    public void GivenPreferInwardWhenInferThenMechanicalLagsInNegativeDirection()
    {
        // PreferOutward=false, OutwardIsPositive=true → PreferredSign = -1.
        // Mechanical lag is in the negative direction relative to bestPos.
        var preferIn = new FocusDirection(PreferOutward: false, OutwardIsPositive: true);
        const int trueBacklash = 60;
        const int currentOvershoot = 20;
        var bestPos = (int)PerfectFocus;
        var mechanicalPos = bestPos - (trueBacklash - currentOvershoot); // = 960
        var verifyHfd = HfdAt(mechanicalPos);

        var (b, _, mech) = BacklashEstimator.InferFromVerification(
            Fit, bestPos, verifyHfd, currentOvershoot, preferIn);

        b.ShouldNotBeNull();
        b!.Value.ShouldBeInRange(trueBacklash - 1, trueBacklash + 1);
        mech.ShouldBe(mechanicalPos, tolerance: 1.0);
    }

    [Fact]
    public void GivenZeroOvershootWhenInferThenNoSignal()
    {
        // No overshoot was performed (final move was direct in the preferred direction)
        // → verifyHfd carries no backlash signal.
        var bestPos = (int)PerfectFocus;
        var verifyHfd = HfdAt(bestPos + 50); // even with worse HFD, no signal

        var (b, conf, _) = BacklashEstimator.InferFromVerification(
            Fit, bestPos, verifyHfd, currentOvershoot: 0, PreferOutPositive);

        b.ShouldBeNull();
        conf.ShouldBe(0f);
    }

    [Fact]
    public void GivenNoiseLevelExcessWhenInferThenNoSignal()
    {
        // verifyHfd just barely above predicted (within 2% of A) → below noise floor.
        var bestPos = (int)PerfectFocus;
        var verifyHfd = A * 1.01; // 1% excess, below 2% noise floor

        var (b, conf, _) = BacklashEstimator.InferFromVerification(
            Fit, bestPos, verifyHfd, currentOvershoot: 50, PreferOutPositive);

        b.ShouldBeNull();
        conf.ShouldBe(0f);
    }

    [Fact]
    public void GivenLargeExcessWhenInferThenHighConfidence()
    {
        // Backlash way bigger than overshoot → mechanical far from bestPos → big HFD excess.
        const int trueBacklash = 200;
        const int currentOvershoot = 20;
        var bestPos = (int)PerfectFocus;
        var mechanicalPos = bestPos + (trueBacklash - currentOvershoot);
        var verifyHfd = HfdAt(mechanicalPos);

        var (b, conf, _) = BacklashEstimator.InferFromVerification(
            Fit, bestPos, verifyHfd, currentOvershoot, PreferOutPositive);

        b.ShouldNotBeNull();
        b!.Value.ShouldBeInRange(trueBacklash - 1, trueBacklash + 1);
        conf.ShouldBeGreaterThan(0.95f); // saturated
    }

    [Fact]
    public void GivenInvalidFitWhenInferThenNoSignal()
    {
        // a or b ≤ 0 means the hyperbola fit is degenerate.
        var badFit = new FocusSolution(BestFocus: PerfectFocus, A: 0, B: B, Error: 1, Iterations: 0);

        var (b, conf, _) = BacklashEstimator.InferFromVerification(
            badFit, (int)PerfectFocus, verifyHfd: 2.0, currentOvershoot: 50, PreferOutPositive);

        b.ShouldBeNull();
        conf.ShouldBe(0f);
    }

    [Fact]
    public void GivenFirstSampleWhenUpdateEwmaThenReplacesEstimate()
    {
        // First sample: ignore the (zero) prior estimate, take new sample outright.
        var updated = BacklashEstimator.UpdateEwma(currentEstimate: 0, newSample: 100, sampleCount: 0);
        updated.ShouldBe(100);
    }

    [Fact]
    public void GivenSecondSampleWhenUpdateEwmaThenBlendsAt30Percent()
    {
        // alpha = 0.3 → 0.3 * 200 + 0.7 * 100 = 60 + 70 = 130.
        var updated = BacklashEstimator.UpdateEwma(currentEstimate: 100, newSample: 200, sampleCount: 1);
        updated.ShouldBe(130);
    }

    [Fact]
    public void GivenManySamplesWhenUpdateEwmaThenConvergesToTrueValue()
    {
        // Stream a stable true value through and confirm the EWMA approaches it.
        var estimate = 50;
        for (var i = 1; i < 30; i++)
        {
            estimate = BacklashEstimator.UpdateEwma(estimate, newSample: 100, sampleCount: i);
        }
        estimate.ShouldBeInRange(99, 101);
    }
}
