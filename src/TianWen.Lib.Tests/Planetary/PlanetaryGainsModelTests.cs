using System;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The learned sharpening gains' model (<see cref="PlanetaryGainsModel"/>, #1552): a ridge per colour over the readings it names, each
/// standardised by the twins' own mean and spread, applied only inside the twins' range and at their sampling, else null with the reason in
/// words.
/// </summary>
public class PlanetaryGainsModelTests
{
    // Two bands from the glow alone: band 1 falls as the glow rises, band 2 rises.
    private static readonly PlanetaryGainsModel Glow = new("a test model", [PlanetaryGainsModel.GlowReading], [new PlanetaryGainsModel.Colour(
        Mean: [2.0], Spread: [0.5], Low: [1.5], High: [3.0], Cutoff: 0.25,
        Weights: [[-0.2], [1.5]], Intercepts: [-0.1, 3.0])]);

    // The glow and the edge, in that order.
    private static readonly PlanetaryGainsModel Both = new("a test model", [PlanetaryGainsModel.GlowReading, PlanetaryGainsModel.EdgeReading],
        [new PlanetaryGainsModel.Colour(Mean: [2.0, 0.4], Spread: [0.5, 0.05], Low: [1.5, 0.3], High: [3.0, 0.5], Cutoff: 0.25,
            Weights: [[-0.2, 0.0], [1.5, -1.0]], Intercepts: [-0.1, 3.0])]);

    [Fact]
    public void InsideTheTwinsRangeTheGainsAreTheRidgesStandardisedSum()
    {
        var gains = Glow.Predict(0, edgeAtTenth: double.NaN, nearGlow: 2.5, 0.25, out var outside).ShouldNotBeNull();

        outside.ShouldBeNull();
        gains.Length.ShouldBe(2);
        gains[0].ShouldBe(-0.1 + (-0.2 * ((2.5 - 2.0) / 0.5)), 1e-12);  // -0.3
        gains[1].ShouldBe(3.0 + (1.5 * ((2.5 - 2.0) / 0.5)), 1e-12);   // 4.5
    }

    [Fact]
    public void EachReadingIsTakenByItsNameInTheModelsOrder()
    {
        var gains = Both.Predict(0, edgeAtTenth: 0.45, nearGlow: 2.5, 0.25, out var outside).ShouldNotBeNull();

        outside.ShouldBeNull();
        gains[1].ShouldBe(3.0 + (1.5 * ((2.5 - 2.0) / 0.5)) + (-1.0 * ((0.45 - 0.4) / 0.05)), 1e-12);  // 3.5
    }

    [Fact]
    public void AReadingTheModelDoesNotTakeIsNotLookedAt()
    {
        // The glow-only model ignores an edge outside any range, as a master whose limb falls faster than Minnaert's reads it (#1561).
        Glow.Predict(0, edgeAtTenth: 0.05, nearGlow: 2.0, 0.25, out var outside).ShouldNotBeNull();
        outside.ShouldBeNull();
    }

    [Fact]
    public void AReadingOutsideTheTwinsRangeIsRefusedAndNamed()
    {
        Both.Predict(0, edgeAtTenth: 0.21, nearGlow: 2.0, 0.25, out var outside).ShouldBeNull();

        outside.ShouldNotBeNull();
        outside.ShouldContain(PlanetaryGainsModel.EdgeReading);
        outside.ShouldContain("0.210");
    }

    [Fact]
    public void AnotherSamplingIsRefusedEvenWithEveryReadingInside()
    {
        // The readings are taken in pixels and cycles a pixel: at a cutoff 20 % off the twins', they mean something else.
        Glow.Predict(0, double.NaN, 2.0, 0.30, out var outside).ShouldBeNull();
        outside.ShouldNotBeNull().ShouldContain("pupil cutoff");

        // Within the tolerance it applies.
        Glow.Predict(0, double.NaN, 2.0, 0.25 * (1 + (PlanetaryGainsModel.CutoffTolerance / 2)), out _).ShouldNotBeNull();
    }

    [Fact]
    public void AChannelPastTheModelsColoursTakesTheLast()
    {
        Glow.Predict(2, double.NaN, 2.0, 0.25, out var outside).ShouldNotBeNull()[1].ShouldBe(3.0, 1e-12);
        outside.ShouldBeNull();
    }

    [Fact]
    public void ANaNReadingTheModelTakesIsOutside()
    {
        Glow.Predict(0, 0.4, double.NaN, 0.25, out var outside).ShouldBeNull();
        outside.ShouldNotBeNull();
    }

    [Fact]
    public void OnlyAModelThatTakesTheGlowAsksForIt()
    {
        Glow.TakesGlow.ShouldBeTrue();
        (Glow with { Readings = [PlanetaryGainsModel.EdgeReading] }).TakesGlow.ShouldBeFalse();
    }

    [Fact]
    public void AnUnknownReadingIsRefusedLoudly()
    {
        var model = Glow with { Readings = ["the moon's phase"] };
        Should.Throw<InvalidOperationException>(() => model.Predict(0, 0.4, 2.0, 0.25, out _));
    }
}
