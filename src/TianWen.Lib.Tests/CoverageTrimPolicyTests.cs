using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a REFUSED edge loses. The walk's verdict is pinned by <see cref="CoverageEdgeWalkTests"/>;
/// this is the policy sitting on top of it, and the whole distinction it draws is between a depth the
/// walk measured and confirmed and a number nobody measured.
/// </summary>
/// <remarks>
/// The numbers are the QHY294C SMC master's, the frame the option was written for: a 4108 px axis, so
/// the 0.05 default works out to 205 px, and its declined edges settle past that. A confirmed border
/// therefore loses MORE than the fraction names, which is the consequence worth pinning rather than
/// discovering on a master.
/// </remarks>
public class CoverageTrimPolicyTests
{
    private const int Span = 4108;
    private const int Blind = 205;

    private static CoverageEdgeTrim Edge(CoverageEdgeOutcome outcome, int depth = 0, int settleDepth = -1)
        => new CoverageEdgeTrim(depth, outcome, EdgeRatio: 3.0, settleDepth);

    /// <summary>
    /// The reason this type exists at all. The stacker writes a master and the CLI crops one, and while
    /// the policy lived in the CLI they disagreed: `tianwen image autocrop` removed V1045 Ori's 356 px
    /// dither strip exactly and `tianwen stack` wrote a master that still had it. One default, one
    /// implementation, so that cannot happen again.
    /// </summary>
    [Fact]
    public void ProducingAMasterAndCroppingOneUseTheSameDefault()
    {
        var beyond = Edge(CoverageEdgeOutcome.BeyondCap, settleDepth: 356);

        // What the stacker applies through CoverageEdgeWalk.Trim, and what the CLI applies for a
        // --trim-declined left at its default, are the same object.
        CoverageTrimPolicy.Default.DeclinedFraction.ShouldBe(0.05);
        CoverageTrimPolicy.Default.DepthFor(beyond, Span).ShouldBe(356);
        (CoverageTrimPolicy.Default with { DeclinedFraction = 0.05 }).ShouldBe(CoverageTrimPolicy.Default);
    }

    [Fact]
    public void TheViewerKeepsEveryPixelThatExists()
    {
        CoverageTrimPolicy.KeepEveryPixel.DepthFor(Edge(CoverageEdgeOutcome.BeyondCap, settleDepth: 356), Span).ShouldBe(0);
        CoverageTrimPolicy.KeepEveryPixel.DepthFor(Edge(CoverageEdgeOutcome.NeverSettles), Span).ShouldBe(0);
        CoverageTrimPolicy.KeepEveryPixel.DepthFor(Edge(CoverageEdgeOutcome.Unconfirmed, settleDepth: 300), Span).ShouldBe(0);

        // ... but an edge the walk ANSWERED for is not a refusal, and is trimmed under every policy.
        CoverageTrimPolicy.KeepEveryPixel.DepthFor(Edge(CoverageEdgeOutcome.Trimmed, depth: 48, settleDepth: 48), Span).ShouldBe(48);
    }

    [Fact]
    public void AConfirmedBorderPastTheCapComesOffAtTheDepthTheWalkMeasured()
    {
        CoverageTrimPolicy.Default.DepthFor(Edge(CoverageEdgeOutcome.BeyondCap, settleDepth: 260), Span).ShouldBe(260);
    }

    [Fact]
    public void AMeasuredDepthIsUsedEvenWhereItCostsMoreThanTheFractionAsked()
    {
        // Not an accident to be clamped away: BeyondCap means the settle depth is past the loss cap,
        // and the fraction defaults to that same cap, so the measured answer is ALWAYS the deeper one.
        var depth = CoverageTrimPolicy.Default.DepthFor(Edge(CoverageEdgeOutcome.BeyondCap, settleDepth: 411), Span);

        depth.ShouldBe(411);
        depth.ShouldBeGreaterThan(Blind);
    }

    [Theory]
    [InlineData(CoverageEdgeOutcome.NeverSettles)]
    [InlineData(CoverageEdgeOutcome.NotMeasurable)]
    [InlineData(CoverageEdgeOutcome.Unconfirmed)]
    public void ARefusalWithNoBorderToReadTakesTheFraction(CoverageEdgeOutcome outcome)
    {
        // Unconfirmed belongs here and not with BeyondCap: a depth that followed the window is not a
        // border, so trimming to it would bite into a gradient on a number that only looks like one.
        CoverageTrimPolicy.Default.DepthFor(Edge(outcome, settleDepth: 300), Span).ShouldBe(Blind);
    }

    [Theory]
    [InlineData(CoverageEdgeOutcome.Clean, 0)]
    [InlineData(CoverageEdgeOutcome.Trimmed, 48)]
    public void AnEdgeTheWalkAnsweredForKeepsItsOwnAnswer(CoverageEdgeOutcome outcome, int depth)
    {
        CoverageTrimPolicy.Default.DepthFor(Edge(outcome, depth, settleDepth: depth), Span).ShouldBe(depth);
    }

    [Fact]
    public void TheLogLineSaysWhetherTheDepthWasMeasuredOrGuessed()
    {
        var p = CoverageTrimPolicy.Default;
        p.Describe("left", Edge(CoverageEdgeOutcome.BeyondCap, settleDepth: 260), 260)
            .ShouldBe("left 260 px, settled there");
        p.Describe("top", Edge(CoverageEdgeOutcome.Unconfirmed, settleDepth: 300), Blind)
            .ShouldBe("top 205 px, blind (no border, the depth followed the window)");
        p.Describe("right", Edge(CoverageEdgeOutcome.NeverSettles), Blind)
            .ShouldBe("right 205 px, blind (never settled)");
        p.Describe("bottom", Edge(CoverageEdgeOutcome.Clean), 0).ShouldBeNull();
    }

    [Fact]
    public void ATrimThatWouldLeaveNothingIsRefused()
    {
        var rect = new PixelRect(0, 0, 100, 100);
        var eatEverything = new CoverageEdgeTrims(
            Edge(CoverageEdgeOutcome.Trimmed, depth: 49, settleDepth: 49),
            Edge(CoverageEdgeOutcome.Trimmed, depth: 49, settleDepth: 49),
            Edge(CoverageEdgeOutcome.Trimmed, depth: 49, settleDepth: 49),
            Edge(CoverageEdgeOutcome.Trimmed, depth: 49, settleDepth: 49));

        eatEverything.Apply(rect, CoverageTrimPolicy.Default).ShouldBe(rect);
    }
}
