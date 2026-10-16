using System;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The evidence run's prompt for a person's clicks (<see cref="ClickPrompt"/>) is due at once, before anything was printed, and
/// then each interval. Both prompt loops of <c>NodePlanetaryRateProbe</c>'s GUI evidence run marked "never printed" with
/// <see cref="TimeSpan.MinValue"/> and subtracted it from the clock, which overflows: the run threw before it printed a click
/// (review round 1 of #1610, B1).
/// </summary>
public class ClickPromptTests
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    [InlineData(3600.0)]
    public void WithNothingPrintedTheClicksAreDueAtOnce(double seconds)
        => ClickPrompt.Due(lastPrinted: null, TimeSpan.FromSeconds(seconds), Every).ShouldBeTrue();

    [Fact]
    public void OncePrintedTheyAreDueAgainOnlyAfterTheInterval()
    {
        var printed = TimeSpan.FromSeconds(10);

        ClickPrompt.Due(printed, printed, Every).ShouldBeFalse("just printed");
        ClickPrompt.Due(printed, printed + Every - TimeSpan.FromTicks(1), Every).ShouldBeFalse();
        ClickPrompt.Due(printed, printed + Every, Every).ShouldBeTrue();
    }
}
