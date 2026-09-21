using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What <c>tianwen image autocrop --trim-declined</c> takes off an edge the walk left alone. The walk
/// itself is pinned by <see cref="CoverageEdgeWalkTests"/>; this is the CLI's policy sitting on top of
/// its verdict, and the whole distinction is between a depth the walk MEASURED and a fraction nobody
/// measured.
/// </summary>
/// <remarks>
/// The numbers are the QHY294C SMC master's, the frame the option was written for: a 4108 px axis, so
/// the 0.05 default fraction is 205 px, and its declined edges settle past that. A beyond-cap edge
/// therefore loses MORE than the flag's own number, which is the consequence worth pinning rather than
/// discovering on a master.
/// </remarks>
public class AutocropDeclinedTrimTests
{
    /// <summary>0.05 of a 4108 px axis: what the default fraction works out to on that master.</summary>
    private const int Blind = 205;

    private static CoverageEdgeTrim Edge(CoverageEdgeOutcome outcome, int depth = 0, int settleDepth = -1)
        => new CoverageEdgeTrim(depth, outcome, EdgeRatio: 3.0, settleDepth);

    [Fact]
    public void ABandPastTheCapComesOffAtTheDepthTheWalkMeasured()
    {
        var edge = Edge(CoverageEdgeOutcome.BeyondCap, settleDepth: 260);

        ImageSubCommand.DeclinedDepth(edge, Blind).ShouldBe(260);
    }

    [Fact]
    public void AMeasuredDepthIsUsedEvenWhereItCostsMoreThanTheFractionAsked()
    {
        // Not an accident to be clamped away: BeyondCap means the settle depth is past the loss cap,
        // and the fraction defaults to that same cap, so the measured answer is ALWAYS the deeper one.
        var edge = Edge(CoverageEdgeOutcome.BeyondCap, settleDepth: 411);

        var depth = ImageSubCommand.DeclinedDepth(edge, Blind);

        depth.ShouldBe(411);
        depth.ShouldBeGreaterThan(Blind);
    }

    [Fact]
    public void AnEdgeStillFallingAtTheEndOfTheSearchHasNoDepthToReadAndTakesTheFraction()
    {
        var edge = Edge(CoverageEdgeOutcome.NeverSettles);

        ImageSubCommand.DeclinedDepth(edge, Blind).ShouldBe(Blind);
    }

    [Fact]
    public void AnEdgeThatCouldNotBeMeasuredTakesTheFraction()
    {
        var edge = Edge(CoverageEdgeOutcome.NotMeasurable);

        ImageSubCommand.DeclinedDepth(edge, Blind).ShouldBe(Blind);
    }

    [Theory]
    [InlineData(CoverageEdgeOutcome.Clean, 0)]
    [InlineData(CoverageEdgeOutcome.Trimmed, 48)]
    public void AnEdgeTheWalkAnsweredForLosesNothingMore(CoverageEdgeOutcome outcome, int depth)
    {
        // It is already trimmed to where it settled; the fallback exists for the edges it refused.
        var edge = Edge(outcome, depth, settleDepth: depth);

        ImageSubCommand.DeclinedDepth(edge, Blind).ShouldBe(0);
    }

    [Fact]
    public void TheLogLineSaysWhetherTheDepthWasMeasuredOrGuessed()
    {
        ImageSubCommand.DescribeDeclinedEdge("left", Edge(CoverageEdgeOutcome.BeyondCap, settleDepth: 260), 260)
            .ShouldBe("left 260 px, settled there");
        ImageSubCommand.DescribeDeclinedEdge("top", Edge(CoverageEdgeOutcome.NeverSettles), Blind)
            .ShouldBe("top 205 px, blind (never settled)");
        ImageSubCommand.DescribeDeclinedEdge("right", Edge(CoverageEdgeOutcome.NotMeasurable), Blind)
            .ShouldBe("right 205 px, blind (not measurable)");
    }

    [Fact]
    public void AnEdgeThatDidNotMoveIsLeftOutOfTheLogLine()
    {
        ImageSubCommand.DescribeDeclinedEdge("bottom", Edge(CoverageEdgeOutcome.Clean), 0).ShouldBeNull();
    }
}
