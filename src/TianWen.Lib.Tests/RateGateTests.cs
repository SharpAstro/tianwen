using System;
using System.Linq;
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

    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    private static SerRateReadBack Ser(int frames, params TimeSpan[] longSteps)
        => new SerRateReadBack(frames, Minute, 5.08, 5.2, longSteps.Length > 0 ? longSteps.Max() : TimeSpan.FromMilliseconds(6),
            [.. longSteps.Select((step, i) => (1000 * (i + 1), step))], TimeSpan.FromMilliseconds(100));

    [Fact]
    public void AGapTheStreamLoggedNoStallForIsJudgedAndFails()
    {
        // A reader late past the hand-off, or a disk that held the recorder, leaves a long step with no stall logged: it is the
        // node's or the box's, and the gate judges it (review round 1 of #1609, B2).
        var ser = Ser(11_500, TimeSpan.FromSeconds(1.5));

        var standing = RateGate.StandingOf(loggedStalls: 0, ser, Minute);

        standing.SetAside.ShouldBeFalse("no stall was logged, so it is judged");
        standing.MakesAnother.ShouldBeTrue("it looks stalled, so another run is made in case the log says so");
        RateGate.Failures(ser, replaced: 0, bodyDropped: 0, diskDropped: 0, baseline: 196.8)
            .ShouldContain(failure => failure.Contains("over 100 ms"), "the step fails it");
    }

    [Fact]
    public void ALoggedStallSetsTheRecordingAside()
    {
        var standing = RateGate.StandingOf(loggedStalls: 1, Ser(11_800), Minute);

        standing.SetAside.ShouldBeTrue("the camera's stall, recovered, is reported apart");
        standing.MakesAnother.ShouldBeTrue();
    }

    [Fact]
    public void ACleanRecordingStandsAndPasses()
    {
        var ser = Ser(11_808);

        RateGate.StandingOf(loggedStalls: 0, ser, Minute).ShouldBe(new RateGate.Standing(SetAside: false, MakesAnother: false));
        RateGate.Failures(ser, replaced: 0, bodyDropped: 0, diskDropped: 0, baseline: 196.8).ShouldBeEmpty();
    }

    [Fact]
    public void EveryShortfallIsSaid()
    {
        var failures = RateGate.Failures(Ser(11_000), replaced: 3, bodyDropped: 1, diskDropped: 2, baseline: null);

        failures.Count.ShouldBe(5, string.Join("; ", failures));
    }
}
