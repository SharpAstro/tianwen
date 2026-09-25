using Shouldly;
using System;
using TianWen.Lib.Astrometry.PlateSolve;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The catalog solver's acceptance threshold. The old rule, five times the chance expectation, asked a
/// dense wide field for more hits than it sampled (144 of 120 on the 24 mm Sagittarius star cloud master,
/// 2026-09-25), so a solve matching 115 of 120 was rejected as noise. The new threshold is the lower of that
/// and the chance mean plus ten of its standard deviations, so it can never be HIGHER than the old one: a
/// field that was accepted still is, and only a dense field's bar comes down.
/// </summary>
public class CatalogPlateSolverAcceptanceGateTests
{
    private const int Sampled = 120;

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(1.5)]
    [InlineData(4.0)]
    public void ASparseFieldKeepsTheBarItAlwaysHad(double expectedChance)
        => CatalogPlateSolver.AcceptanceThreshold(Sampled, expectedChance)
            .ShouldBe(Math.Max(6, 5 * expectedChance), 1e-9);

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(10.0)]
    [InlineData(28.8)]
    [InlineData(33.2)]
    [InlineData(60.0)]
    [InlineData(119.0)]
    public void TheNewBarIsNeverAboveTheOldOne(double expectedChance)
        => CatalogPlateSolver.AcceptanceThreshold(Sampled, expectedChance)
            .ShouldBeLessThanOrEqualTo(Math.Max(6, 5 * expectedChance) + 1e-9);

    [Theory]
    [InlineData(28.8, 115)] // the Sagittarius star cloud master, 24 mm on the ASI533
    [InlineData(33.2, 119)] // the second dense wide field in the same bake
    public void ADenseWideFieldWithAGenuineSolveIsAcceptedNow(double expectedChance, int hits)
    {
        Math.Max(6, 5 * expectedChance).ShouldBeGreaterThan(Sampled, "the precondition: the old bar was out of reach");
        CatalogPlateSolver.AcceptanceThreshold(Sampled, expectedChance).ShouldBeLessThanOrEqualTo(hits);
    }

    [Theory]
    [InlineData(28.8)]
    [InlineData(33.2)]
    [InlineData(60.0)]
    public void ARandomAlignmentInTheSameDenseFieldIsStillRejected(double expectedChance)
    {
        // Five standard deviations above the chance mean is already a one-in-millions random alignment,
        // and it must still fall short of the bar.
        var p = expectedChance / Sampled;
        var fiveSigmaFluke = expectedChance + 5 * Math.Sqrt(Sampled * p * (1 - p));
        CatalogPlateSolver.AcceptanceThreshold(Sampled, expectedChance).ShouldBeGreaterThan(fiveSigmaFluke);
    }
}
