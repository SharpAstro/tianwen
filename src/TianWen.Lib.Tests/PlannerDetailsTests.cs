using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for the planner details panel content (<see cref="PlannerDetails.GetLines"/>),
/// specifically the catalog-property enrichment (type, constellation, magnitude, surface
/// brightness, colour index, angular size) resolved from <see cref="PlannerState.ObjectDb"/>.
/// </summary>
public class PlannerDetailsTests
{
    private static readonly DateTimeOffset NightStart = new(2025, 12, 15, 18, 0, 0, TimeSpan.Zero);

    private static PlannerState BuildState(ScoredTarget scored, ICelestialObjectDB? db)
        => new()
        {
            ObjectDb = db,
            SelectedTargetIndex = 0,
            SiteTimeZone = TimeSpan.Zero,
            AstroDark = NightStart,
            AstroTwilight = NightStart + TimeSpan.FromHours(8),
            TonightsBest = [scored],
            AltitudeProfiles = ImmutableDictionary<Target, List<(DateTimeOffset Time, double Alt)>>.Empty,
            TargetAliases = ImmutableDictionary<Target, string>.Empty,
        };

    private static ScoredTarget Scored(Target target, ObjectType type = ObjectType.Unknown)
        => new(target, (Half)1.0, (Half)1.0,
            new Dictionary<RaDecEventTime, RaDecEventInfo>(),
            OptimalStart: NightStart, OptimalDuration: TimeSpan.FromHours(2),
            OptimalAltitude: 60.0, ObjectType: type);

    [Fact]
    public async Task GivenCataloguedTargetWhenObjectDbSetThenDetailsIncludePhysicalProperties()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

        // M 31: a bright, large galaxy that carries magnitude AND a shape entry in OpenNGC,
        // so it exercises the type/constellation, photometry, and arcminute size branches.
        db.TryLookupByIndex("M31", out var obj).ShouldBeTrue();
        Half.IsNaN(obj.V_Mag).ShouldBeFalse("test object must have a magnitude");
        db.TryGetShape(obj.Index, out _).ShouldBeTrue("test object must have a shape");

        var target = new Target(obj.RA, obj.Dec, obj.DisplayName, obj.Index);
        var lines = PlannerDetails.GetLines(BuildState(Scored(target, obj.ObjectType), db), [Scored(target, obj.ObjectType)]);

        // Name first, physical-property lines inserted between it and the coordinate line.
        lines[0].ShouldBe(obj.DisplayName);
        lines.ShouldContain(l => l.Contains(obj.ObjectType.ToName()), "type name line missing");
        lines.ShouldContain(l => l.Contains(obj.Constellation.ToIAUAbbreviation()), "constellation missing");
        lines.ShouldContain(l => l.Contains("mag "), "magnitude line missing");
        lines.ShouldContain(l => l.StartsWith("size "), "angular size line missing");
        // Existing observability lines are still present and after the enrichment.
        lines.ShouldContain(l => l.StartsWith("RA "), "coordinate line missing");
        lines[^1].ShouldStartWith("Rating");
    }

    [Fact]
    public async Task GivenNoObjectDbWhenGetLinesThenNoCatalogPropertyLines()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("M31", out var obj).ShouldBeTrue();

        var target = new Target(obj.RA, obj.Dec, obj.DisplayName, obj.Index);
        // ObjectDb null -> enrichment is skipped and the coordinate line follows the name directly,
        // preserving the pre-enrichment layout the CLI inline prompt used to index as details[1].
        var lines = PlannerDetails.GetLines(BuildState(Scored(target), db: null), [Scored(target)]);

        lines[0].ShouldBe(obj.DisplayName);
        lines[1].ShouldStartWith("RA ");
        lines.ShouldNotContain(l => l.StartsWith("size "));
        lines.ShouldNotContain(l => l.Contains("mag "));
    }
}
