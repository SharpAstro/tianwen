using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pure (DB-free) tests for <see cref="FramingPlanner.CollapseForSchedule"/>: co-framed proposals
/// collapse to one pointing, ungrouped proposals pass through, identity when there are no groups.
/// </summary>
public sealed class FramingPlannerCollapseTests
{
    private static FramingCandidate Seed(string name, double ra, double dec)
        => new(ra, dec, 0.3, 0.3, name, Index: null, VMag: 7.0, IsSeed: true);

    [Fact]
    public void CollapseForSchedule_MergesCoFramedProposals_IntoOnePointing()
    {
        // A multi-target group of M8 + M20; both are pinned proposals.
        var group = new FramingGroup(
            [Seed("M20", 18.04, -22.97), Seed("M8", 18.06, -24.38)],
            CenterRA: 18.05, CenterDec: -23.70, Name: "M20 + M8");

        var p8 = new ProposedObservation(new Target(18.06, -24.38, "M8", null))
            with { Gain = 42, ObservationTime = TimeSpan.FromHours(2) };
        var p20 = new ProposedObservation(new Target(18.04, -22.97, "M20", null));
        var pOther = new ProposedObservation(new Target(5.58, -5.39, "M42", null)); // Orion, far away

        var result = FramingPlanner.CollapseForSchedule([p8, p20, pOther], [group]);

        // M8 + M20 collapse to ONE representative; M42 passes through -> 2 entries.
        result.Length.ShouldBe(2);

        var rep = result[0];
        rep.Target.Name.ShouldBe("M20 + M8");
        rep.Target.RA.ShouldBe(18.05, 1e-9);
        rep.Target.Dec.ShouldBe(-23.70, 1e-9);
        // First member proposal's imaging params are preserved on the representative.
        rep.Gain.ShouldBe(42);
        rep.ObservationTime.ShouldBe(TimeSpan.FromHours(2));

        result[1].Target.Name.ShouldBe("M42");
    }

    [Fact]
    public void CollapseForSchedule_NoGroups_IsIdentity()
    {
        var p8 = new ProposedObservation(new Target(18.06, -24.38, "M8", null));
        var p20 = new ProposedObservation(new Target(18.04, -22.97, "M20", null));

        var result = FramingPlanner.CollapseForSchedule([p8, p20], []);

        result.Length.ShouldBe(2);
        result[0].Target.Name.ShouldBe("M8");
        result[1].Target.Name.ShouldBe("M20");
    }

    [Fact]
    public void CollapseForSchedule_SingletonGroup_LeavesProposalUnchanged()
    {
        // A single-member group is not a multi-target frame; the proposal must pass through as-is.
        var group = new FramingGroup([Seed("M8", 18.06, -24.38)], 18.06, -24.38, "M8");
        var p8 = new ProposedObservation(new Target(18.06, -24.38, "M8", null));

        var result = FramingPlanner.CollapseForSchedule([p8], [group]);

        result.Length.ShouldBe(1);
        result[0].Target.Name.ShouldBe("M8");
    }
}

/// <summary>
/// DB-backed discovery tests for <see cref="FramingPlanner.BuildGroups"/> against the real catalog:
/// pinning M8 (Lagoon) surfaces M20 (Trifid) as a co-framable neighbour at a wide field, but not at a
/// long focal length. Uses the shared catalog DB (Tycho-2 bulk load), so these are heavier -- kept in
/// the Scheduling collection and typically exercised on CI.
/// </summary>
[Collection("Scheduling")]
public sealed class FramingPlannerDiscoveryTests
{
    private readonly ITestOutputHelper _out;
    public FramingPlannerDiscoveryTests(ITestOutputHelper output) => _out = output;

    // Great-circle separation, RA in hours / Dec in degrees.
    private static double SepDeg(double ra1h, double dec1, double ra2h, double dec2)
    {
        const double d2r = Math.PI / 180.0;
        var r1 = ra1h * 15 * d2r; var dd1 = dec1 * d2r;
        var r2 = ra2h * 15 * d2r; var dd2 = dec2 * d2r;
        var cos = Math.Sin(dd1) * Math.Sin(dd2) + Math.Cos(dd1) * Math.Cos(dd2) * Math.Cos(r1 - r2);
        return Math.Acos(Math.Clamp(cos, -1, 1)) / d2r;
    }

    // M20 Trifid ~ 18h02.6m, -23 02'.
    private const double M20_RA = 18.043;
    private const double M20_Dec = -23.03;

    [Fact]
    public async Task BuildGroups_M8Seed_WideField_DiscoversM20AsNeighbour()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("M8", out var m8).ShouldBeTrue();
        var m20Resolved = db.TryLookupByIndex("M20", out var m20);
        _out.WriteLine($"M20 resolved={m20Resolved} idx={(m20Resolved ? m20.Index.ToCanonical() : "-")} name={(m20Resolved ? m20.DisplayName : "-")} RA={(m20Resolved ? m20.RA : double.NaN):F4} Dec={(m20Resolved ? m20.Dec : double.NaN):F3}");

        Target[] seeds = [new Target(m8.RA, m8.Dec, m8.DisplayName, m8.Index)];

        // ~3 deg field: M20 (~1.4 deg from M8) is co-framable. Slightly raised member cap since the
        // Sagittarius field carries several named DSOs.
        var groups = FramingPlanner.BuildGroups(db, (3.0, 3.0), seeds,
            new FramingOptions(EmitSingletons: false, MaxMembers: 8));

        // Diagnostics.
        _out.WriteLine($"M8 resolved: {m8.DisplayName} idx={m8.Index.ToCanonical()} RA={m8.RA:F4}h Dec={m8.Dec:F3}");
        _out.WriteLine($"groups={groups.Length}");
        foreach (var g0 in groups)
        {
            _out.WriteLine($"  group '{g0.Name}' center=({g0.CenterRA:F4}h,{g0.CenterDec:F3}) members={g0.Members.Length}");
            foreach (var m in g0.Members)
            {
                var cross = m.Index is { } mi && db.TryGetCrossIndices(mi, out var xs)
                    ? string.Join(",", xs.Select(x => x.ToCanonical()))
                    : "";
                _out.WriteLine($"    - {m.Name} idx={m.Index?.ToCanonical() ?? "-"} cross=[{cross}] RA={m.RA:F4}h Dec={m.Dec:F3} vmag={m.VMag:F2} sepToM20={SepDeg(m.RA, m.Dec, M20_RA, M20_Dec):F3}");
            }
        }
        // Is M20 even in the DSO grid near its known position?
        _out.WriteLine("grid @ M20:");
        foreach (var idx in db.DeepSkyCoordinateGrid[M20_RA, M20_Dec])
        {
            if (db.TryLookupByIndex(idx, out var o))
            {
                _out.WriteLine($"    {idx.ToCanonical()} '{o.DisplayName}' RA={o.RA:F4}h Dec={o.Dec:F3} sep={SepDeg(o.RA, o.Dec, M20_RA, M20_Dec):F3}");
            }
        }

        var group = groups.FirstOrDefault(g => g.IsMultiTarget);
        group.Members.IsDefaultOrEmpty.ShouldBeFalse("M8 should co-frame at least one neighbour at 3 deg");
        group.Members.ShouldContain(m => SepDeg(m.RA, m.Dec, M20_RA, M20_Dec) < 0.25,
            "the Trifid (M20) should be discovered in the same frame as M8");
    }

    [Fact]
    public async Task BuildGroups_M8Seed_NarrowField_NoGrouping()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("M8", out var m8).ShouldBeTrue();

        Target[] seeds = [new Target(m8.RA, m8.Dec, m8.DisplayName, m8.Index)];

        // ~0.3 x 0.2 deg field (long focal length): nothing else fits with M8.
        var groups = FramingPlanner.BuildGroups(db, (0.3, 0.2), seeds, new FramingOptions(EmitSingletons: false));

        groups.ShouldBeEmpty();
    }
}
