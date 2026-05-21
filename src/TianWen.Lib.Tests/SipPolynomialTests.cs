using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Astrometry")]
public class SipPolynomialTests
{
    /// <summary>
    /// Zero coefficient matrix means the SIP polynomial contributes
    /// nothing — the central case for "no distortion fit yet".
    /// </summary>
    [Fact]
    public void GivenZeroCoeffsThenApplyReturnsZero()
    {
        var coeffs = new double[3, 3];   // order 2, all zero
        for (var trial = 0; trial < 5; trial++)
        {
            SipPolynomial.Apply(trial * 0.7, trial * -1.3, coeffs).ShouldBe(0.0);
        }
    }

    /// <summary>
    /// Pure linear (i=1, j=0) coefficient: f(u, v) = a · u, independent of v.
    /// </summary>
    [Fact]
    public void GivenLinearUCoefficientThenApplyMatchesAnalytic()
    {
        var coeffs = new double[3, 3];
        coeffs[1, 0] = 2.5;

        SipPolynomial.Apply(u: 1.0, v: 0.0, coeffs).ShouldBe(2.5);
        SipPolynomial.Apply(u: 3.0, v: 9.0, coeffs).ShouldBe(7.5);   // v should not affect
        SipPolynomial.Apply(u: -4.0, v: 0.0, coeffs).ShouldBe(-10.0);
    }

    /// <summary>
    /// All five order-2 SIP basis terms with known coefficients —
    /// f(u, v) = a10 u + a01 v + a20 u² + a11 u v + a02 v².
    /// </summary>
    [Fact]
    public void GivenOrder2CoefficientsThenApplyMatchesAnalytic()
    {
        var coeffs = new double[3, 3];
        coeffs[1, 0] = 1.0;
        coeffs[0, 1] = 2.0;
        coeffs[2, 0] = 3.0;
        coeffs[1, 1] = 0.5;
        coeffs[0, 2] = -1.0;

        // At (u, v) = (2, 3):
        // = 1·2 + 2·3 + 3·4 + 0.5·6 − 1·9 = 2 + 6 + 12 + 3 − 9 = 14
        SipPolynomial.Apply(2, 3, coeffs).ShouldBe(14.0, 1e-12);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 9)]
    [InlineData(4, 14)]
    public void GivenOrderThenBasisCountIsCorrect(int order, int expected)
    {
        SipPolynomial.BasisCount(order).ShouldBe(expected);
    }

    /// <summary>
    /// Round-trip a known order-2 SIP polynomial via Fit → Apply.
    /// </summary>
    [Fact]
    public void GivenKnownPolynomialThenFitRecoversCoefficients()
    {
        var expected = new double[3, 3];
        expected[1, 0] = 0.001;
        expected[0, 1] = -0.0005;
        expected[2, 0] = 1.5e-6;
        expected[1, 1] = -2.0e-6;
        expected[0, 2] = 0.8e-6;

        var rng = new Random(123);
        const int n = 80;
        var u = new double[n];
        var v = new double[n];
        var targets = new double[n];
        for (var i = 0; i < n; i++)
        {
            // ± 1500 px is the half-extent of a 3000-px master.
            u[i] = (rng.NextDouble() - 0.5) * 3000;
            v[i] = (rng.NextDouble() - 0.5) * 3000;
            targets[i] = SipPolynomial.Apply(u[i], v[i], expected);
        }

        var fit = SipPolynomial.Fit(u, v, targets, order: 2);
        fit.ShouldNotBeNull();

        for (var i = 0; i <= 2; i++)
        {
            for (var j = 0; j <= 2 - i; j++)
            {
                if ((i | j) == 0) continue;
                fit[i, j].ShouldBe(expected[i, j], 1e-12);
            }
        }
    }

    /// <summary>
    /// Too few observations for the chosen order → Fit returns null.
    /// </summary>
    [Fact]
    public void GivenUnderdeterminedInputThenFitReturnsNull()
    {
        var u = new[] { 1.0, 2.0 };
        var v = new[] { 3.0, 4.0 };
        var t = new[] { 5.0, 6.0 };

        // Order 2 needs 5 unknowns; 2 observations is rank-deficient.
        SipPolynomial.Fit(u, v, t, order: 2).ShouldBeNull();
    }

    /// <summary>
    /// Fit + Apply round-trip against an out-of-sample evaluation point —
    /// the canonical SIP use case (fit on observed inliers, apply to
    /// arbitrary pixels at PixelToSky time).
    /// </summary>
    [Fact]
    public void GivenFitThenApplyOnNewPointMatchesGroundTruth()
    {
        var expected = new double[3, 3];
        expected[2, 0] = 1.2e-6;
        expected[1, 1] = -3.4e-6;
        expected[0, 2] = 2.1e-6;

        var rng = new Random(456);
        const int n = 50;
        var u = new double[n];
        var v = new double[n];
        var targets = new double[n];
        for (var i = 0; i < n; i++)
        {
            u[i] = (rng.NextDouble() - 0.5) * 2000;
            v[i] = (rng.NextDouble() - 0.5) * 2000;
            targets[i] = SipPolynomial.Apply(u[i], v[i], expected);
        }

        var fit = SipPolynomial.Fit(u, v, targets, order: 2);
        fit.ShouldNotBeNull();

        // Evaluate at a held-out point.
        var truth = SipPolynomial.Apply(1234, -567, expected);
        var predicted = SipPolynomial.Apply(1234, -567, fit);
        predicted.ShouldBe(truth, 1e-8);
    }
}
