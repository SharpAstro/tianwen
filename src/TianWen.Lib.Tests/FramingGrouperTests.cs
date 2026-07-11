using System.Linq;
using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for the pure <see cref="FramingGrouper"/> "smart framing" geometry: which targets share
/// a single sensor frame, the suggested pointing centroid, and the seed-vs-neighbour rules. Fixtures
/// use hand-built <see cref="FramingCandidate"/>s (no catalog DB), with M8/M20-like separations.
/// </summary>
public class FramingGrouperTests
{
    // M8 Lagoon (RA 18h03.8m, Dec -24 23') and M20 Trifid (RA 18h02.4m, Dec -22 58') -- ~1.45 deg apart,
    // the canonical "one wide frame" pair. Half-extents approximate their catalog-shape bounding boxes.
    private static FramingCandidate M8(bool seed = true)
        => new(RA: 18.063, Dec: -24.383, HalfWidthDeg: 0.40, HalfHeightDeg: 0.35, Name: "M8", Index: null, VMag: 6.0, IsSeed: seed);

    private static FramingCandidate M20(bool seed = true)
        => new(RA: 18.040, Dec: -22.967, HalfWidthDeg: 0.25, HalfHeightDeg: 0.25, Name: "M20", Index: null, VMag: 6.3, IsSeed: seed);

    private static FramingCandidate Point(double ra, double dec, string name, bool seed = true)
        => new(RA: ra, Dec: dec, HalfWidthDeg: 0.0, HalfHeightDeg: 0.0, Name: name, Index: null, VMag: 8.0, IsSeed: seed);

    [Fact]
    public void WideField_GroupsM8AndM20IntoOneFrame()
    {
        FramingCandidate[] cands = [M8(), M20()];

        // ~3 deg field (short refractor + APS-C): comfortably holds both.
        var groups = FramingGrouper.Group(cands, fovWidthDeg: 3.0, fovHeightDeg: 3.0);

        groups.Length.ShouldBe(1);
        var g = groups[0];
        g.IsMultiTarget.ShouldBeTrue();
        g.Members.Length.ShouldBe(2);
        g.Name.ShouldContain("M8");
        g.Name.ShouldContain("M20");
        // Centroid sits between the two (Dec between -24.4 and -23.0).
        g.CenterDec.ShouldBeInRange(-24.4, -23.0);
    }

    [Fact]
    public void NarrowField_KeepsM8AndM20Separate()
    {
        FramingCandidate[] cands = [M8(), M20()];

        // ~0.8 x 0.6 deg field (long focal length): the pair spans far more than one frame.
        var groups = FramingGrouper.Group(cands, fovWidthDeg: 0.8, fovHeightDeg: 0.6);

        groups.Length.ShouldBe(2);
        groups.ShouldAllBe(g => !g.IsMultiTarget);
    }

    [Fact]
    public void DiscoversNonSeedNeighbour_AndAttachesItToTheSeedFrame()
    {
        // Only M8 is pinned; M20 is a catalog neighbour the caller surfaced (IsSeed = false).
        FramingCandidate[] cands = [M8(seed: true), M20(seed: false)];

        var groups = FramingGrouper.Group(cands, fovWidthDeg: 3.0, fovHeightDeg: 3.0);

        groups.Length.ShouldBe(1);
        groups[0].Members.Length.ShouldBe(2);
        groups[0].Members.ShouldContain(m => m.Name == "M20" && !m.IsSeed);
    }

    [Fact]
    public void NonSeedThatFitsNothing_IsDropped_NotEmittedAsItsOwnGroup()
    {
        // A lone seed plus a neighbour on the far side of the sky: the neighbour must not surface.
        FramingCandidate[] cands = [Point(10.0, 20.0, "seed", seed: true), Point(2.0, -40.0, "faraway", seed: false)];

        var groups = FramingGrouper.Group(cands, fovWidthDeg: 2.0, fovHeightDeg: 2.0);

        groups.Length.ShouldBe(1);
        groups[0].Members.Length.ShouldBe(1);
        groups[0].Members[0].Name.ShouldBe("seed");
    }

    [Fact]
    public void CoFramableSeeds_MergeIntoOneGroup_NotTwo()
    {
        // Two pinned targets that share a frame collapse to a single group (each seed assigned once).
        FramingCandidate[] cands = [Point(10.0, 20.0, "A", seed: true), Point(10.0, 20.5, "B", seed: true)];

        var groups = FramingGrouper.Group(cands, fovWidthDeg: 2.0, fovHeightDeg: 2.0);

        groups.Length.ShouldBe(1);
        groups[0].Members.Length.ShouldBe(2);
    }

    [Fact]
    public void Centroid_IsTheFootprintMidpoint_NorthSouthPair()
    {
        // Point sources 1 deg apart in Dec at the same RA -> centroid halfway between.
        FramingCandidate[] cands = [Point(10.0, 20.0, "A"), Point(10.0, 21.0, "B")];

        var groups = FramingGrouper.Group(cands, fovWidthDeg: 2.0, fovHeightDeg: 2.0);

        groups.Length.ShouldBe(1);
        groups[0].CenterRA.ShouldBe(10.0, 1e-6);
        groups[0].CenterDec.ShouldBe(20.5, 1e-6);
    }

    [Fact]
    public void Centroid_AccountsForCosDec_EastWestPair()
    {
        // 0.1h apart in RA at Dec 0 (cos = 1) -> RA centroid halfway (10.05h).
        FramingCandidate[] cands = [Point(10.0, 0.0, "A"), Point(10.1, 0.0, "B")];

        var groups = FramingGrouper.Group(cands, fovWidthDeg: 3.0, fovHeightDeg: 3.0);

        groups.Length.ShouldBe(1);
        groups[0].CenterRA.ShouldBe(10.05, 1e-6);
        groups[0].CenterDec.ShouldBe(0.0, 1e-6);
    }

    [Fact]
    public void MaxMembers_CapsGroupSize()
    {
        // Four point sources tightly packed (all pairwise co-framable); cap keeps groups <= 2.
        FramingCandidate[] cands =
        [
            Point(10.00, 20.0, "A"), Point(10.01, 20.1, "B"),
            Point(10.02, 20.2, "C"), Point(10.03, 20.3, "D"),
        ];

        var groups = FramingGrouper.Group(cands, fovWidthDeg: 3.0, fovHeightDeg: 3.0,
            options: new FramingOptions(MaxMembers: 2));

        groups.ShouldAllBe(g => g.Members.Length <= 2);
        groups.Sum(g => g.Members.Length).ShouldBe(4); // every seed still accounted for
    }

    [Fact]
    public void EmitSingletonsFalse_ReturnsOnlyMultiTargetGroups()
    {
        FramingCandidate[] cands =
        [
            M8(), M20(),                    // co-framable pair
            Point(2.0, 40.0, "lonely"),     // isolated seed
        ];

        var groups = FramingGrouper.Group(cands, fovWidthDeg: 3.0, fovHeightDeg: 3.0,
            options: new FramingOptions(EmitSingletons: false));

        groups.Length.ShouldBe(1);
        groups[0].IsMultiTarget.ShouldBeTrue();
    }

    [Fact]
    public void GroupsAcrossTheRaZeroSeam()
    {
        // 23h59m and 00h01m are 0.033h apart, not 23.97h -- the wrap must measure the short way.
        FramingCandidate[] cands = [Point(23.983, 5.0, "west"), Point(0.017, 5.0, "east")];

        var groups = FramingGrouper.Group(cands, fovWidthDeg: 2.0, fovHeightDeg: 2.0);

        groups.Length.ShouldBe(1);
        groups[0].Members.Length.ShouldBe(2);
    }

    [Fact]
    public void ZeroFovOrEmpty_ReturnsEmpty()
    {
        FramingGrouper.Group([M8(), M20()], fovWidthDeg: 0, fovHeightDeg: 3.0).ShouldBeEmpty();
        FramingGrouper.Group([], fovWidthDeg: 3.0, fovHeightDeg: 3.0).ShouldBeEmpty();
    }
}
