using System.Collections.Immutable;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The learned sharpening gains' model (<see cref="PlanetaryGainsModel"/>, #1552): a ridge per colour, its readings standardised by the
/// twins' own mean and spread, applied only inside the twins' range and at their sampling, else null with the reason in words.
/// </summary>
public class PlanetaryGainsModelTests
{
    // Two bands, readings (edge, core, halo): band 1 rises with the edge, band 2 falls with the core.
    private static readonly PlanetaryGainsModel Model = new("a test model", [new PlanetaryGainsModel.Colour(
        Mean: [0.5, 4.0, 0.3], Spread: [0.1, 1.0, 0.1], Low: [0.3, 2.0, 0.2], High: [0.7, 6.0, 0.5], Cutoff: 0.25,
        Weights: [[2.0, 0.0, 0.0], [0.0, -1.5, 0.0]], Intercepts: [-0.1, 3.0])]);

    [Fact]
    public void InsideTheTwinsRangeTheGainsAreTheRidgesStandardisedSum()
    {
        var gains = Model.Predict(0, [0.6, 5.0, 0.3], 0.25, out var outside).ShouldNotBeNull();

        outside.ShouldBeNull();
        gains.Length.ShouldBe(2);
        gains[0].ShouldBe(-0.1 + (2.0 * ((0.6 - 0.5) / 0.1)), 1e-12);  // 1.9
        gains[1].ShouldBe(3.0 + (-1.5 * ((5.0 - 4.0) / 1.0)), 1e-12);   // 1.5
    }

    [Fact]
    public void AReadingOutsideTheTwinsRangeIsRefusedAndNamed()
    {
        Model.Predict(0, [0.21, 5.0, 0.3], 0.25, out var outside).ShouldBeNull();

        outside.ShouldNotBeNull();
        outside.ShouldContain("the edge at 0.1 cycles a pixel");
        outside.ShouldContain("0.210");
    }

    [Fact]
    public void AnotherSamplingIsRefusedEvenWithEveryReadingInside()
    {
        // The readings are in pixels and cycles a pixel: at a cutoff 20 % off the twins', they mean something else.
        Model.Predict(0, [0.5, 4.0, 0.3], 0.30, out var outside).ShouldBeNull();
        outside.ShouldNotBeNull().ShouldContain("pupil cutoff");

        // Within the tolerance it applies.
        Model.Predict(0, [0.5, 4.0, 0.3], 0.25 * (1 + (PlanetaryGainsModel.CutoffTolerance / 2)), out _).ShouldNotBeNull();
    }

    [Fact]
    public void AChannelPastTheModelsColoursTakesTheLast()
    {
        Model.Predict(2, [0.5, 4.0, 0.3], 0.25, out var outside).ShouldNotBeNull()[1].ShouldBe(3.0, 1e-12);
        outside.ShouldBeNull();
    }

    [Fact]
    public void ANaNReadingIsOutside()
    {
        Model.Predict(0, [double.NaN, 4.0, 0.3], 0.25, out var outside).ShouldBeNull();
        outside.ShouldNotBeNull();
    }
}
