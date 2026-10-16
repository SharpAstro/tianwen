using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The node rate gate's rules (<see cref="RateGate"/>, TIA2's <c>NodePlanetaryRateProbe</c>): what the gate compares a recording
/// through the node with, and what it judges.
/// </summary>
public class RateGateTests
{
    [Fact]
    public void WithNoCleanInProcessRecordingThereIsNoBaselineAndNothingIsWithinIt()
    {
        // Both in-process recordings stalled: the comparison the gate exists for cannot be made, and must not pass. A NaN
        // baseline did, since a comparison through CompareTo ranks NaN below every rate (review round 1 of #1609, B1).
        var baseline = RateGate.InProcessBaseline([]);

        RateGate.IsWithinInProcess(196.8, baseline).ShouldBeFalse("a node recording is within no baseline at all");
        RateGate.IsWithinInProcess(196.8, double.NaN).ShouldBeFalse("nor within one that is not a number");
    }

    [Fact]
    public void TheBaselineIsTheMeanOfTheCleanInProcessRates()
    {
        var baseline = RateGate.InProcessBaseline([196.8, 196.6]);

        baseline.ShouldNotBeNull().ShouldBe(196.7, tolerance: 1e-9);
        RateGate.IsWithinInProcess(191.0, baseline).ShouldBeTrue("97.1 % of it");
        RateGate.IsWithinInProcess(190.0, baseline).ShouldBeFalse("96.6 % of it");
    }
}
