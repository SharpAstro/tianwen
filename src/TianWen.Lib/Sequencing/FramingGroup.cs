using System.Collections.Immutable;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// One object considered for a <see cref="FramingGroup"/>: its J2000 centre plus its on-sky
/// half-extents (from the catalog shape's rotated-ellipse bounding box, in TRUE sky degrees --
/// east-west and north-south, already independent of the cos(Dec) RA foreshortening). A point-like
/// object (no shape) uses zero half-extents. <see cref="IsSeed"/> marks a user-chosen target (a
/// pinned proposal) versus a neighbour the grouper discovered from the catalog -- both can join a
/// frame, but only seeds start a group (a discovered neighbour never anchors one of its own).
/// </summary>
public readonly record struct FramingCandidate(
    double RA,
    double Dec,
    double HalfWidthDeg,
    double HalfHeightDeg,
    string Name,
    CatalogIndex? Index,
    double VMag,
    bool IsSeed);

/// <summary>
/// A set of objects that all fall inside a single sensor frame, with the suggested pointing
/// (<see cref="CenterRA"/>/<see cref="CenterDec"/> = the centroid of their combined footprint) and a
/// combined display <see cref="Name"/> (e.g. "M8 + M20"). A single-member group is a pass-through of
/// one target (nothing co-frames with it); multi-member groups are the "smart" framings.
/// </summary>
public readonly record struct FramingGroup(
    ImmutableArray<FramingCandidate> Members,
    double CenterRA,
    double CenterDec,
    string Name)
{
    /// <summary>True when more than one object shares the frame (a genuine smart-framing grouping).</summary>
    public bool IsMultiTarget => Members.Length > 1;
}

/// <summary>
/// Tunables for <see cref="FramingGrouper.Group"/>. A <see langword="record"/> class (NOT a struct):
/// a <c>record struct</c> constructed via <c>default</c>/<c>new()</c> silently ignores its
/// primary-constructor defaults, which would zero every field here; a record class applies them.
/// </summary>
public sealed record FramingOptions(
    // Hard cap on members per frame so a dense region can't accrete an unbounded pile of faint
    // neighbours into one pointing.
    int MaxMembers = 4,
    // Fraction of the sensor FOV kept as a border, so grouped objects aren't jammed against the frame
    // edge (matches MosaicGenerator's margin convention).
    double MarginFraction = 0.1,
    // Emit a single-member group for a seed that co-frames with nothing (the planner passes seeds
    // through so ungrouped pins still schedule). Set false to return only genuine multi-target frames.
    bool EmitSingletons = true);
