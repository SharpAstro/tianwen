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

    [Fact]
    public async Task GivenLineBudgetWhenOverThenLeastImportantLinesShedFirst()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("M31", out var obj).ShouldBeTrue();
        var target = new Target(obj.RA, obj.Dec, obj.DisplayName, obj.Index);
        var state = BuildState(Scored(target, obj.ObjectType), db);

        // Unbudgeted baseline: name + subtitle + photometry + size + coords + rating = 6 lines for M 31.
        var full = PlannerDetails.GetLines(state, [Scored(target, obj.ObjectType)]);
        full.Count.ShouldBe(6);

        // Portrait's compact strip: catalog niceties (size, photometry, type/constellation) shed
        // first, the observability essentials (name, coords, rating) survive in display order.
        var budgeted = PlannerDetails.GetLines(state, [Scored(target, obj.ObjectType)], maxLines: 3);
        budgeted.Count.ShouldBe(3);
        budgeted[0].ShouldBe(obj.DisplayName);
        budgeted[1].ShouldStartWith("RA ");
        budgeted[2].ShouldStartWith("Rating");

        // The name never drops, even under an absurd budget.
        var one = PlannerDetails.GetLines(state, [Scored(target, obj.ObjectType)], maxLines: 1);
        one.ShouldBe([obj.DisplayName]);
    }

    [Fact]
    public async Task GivenCataloguedTargetWhenGetWikipediaUrlThenLinksMainCatalogName()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("M31", out var obj).ShouldBeTrue();

        var target = new Target(obj.RA, obj.Dec, obj.DisplayName, obj.Index);
        var scored = Scored(target, obj.ObjectType);

        var url = PlannerDetails.GetWikipediaUrl(BuildState(scored, db), [scored]);

        // The link points at en.wikipedia.org and uses the MAIN catalog designation, NOT the display
        // name: decoding the path (and '_' -> ' ') must reproduce Index.ToCanonical(). No raw spaces.
        url.ShouldNotBeNull();
        url.ShouldStartWith("https://en.wikipedia.org/wiki/");
        url.ShouldNotContain(" ");
        var slug = url["https://en.wikipedia.org/wiki/".Length..];
        Uri.UnescapeDataString(slug).Replace('_', ' ').ShouldBe(obj.Index.ToCanonical());
    }

    [Fact]
    public void GivenNoCatalogIndexOrNoSelectionWhenGetWikipediaUrlThenNull()
    {
        // Bare position (no catalog index) -> nothing to link.
        var bare = new Target(1.23, 45.6, "Custom position", null);
        var scored = Scored(bare);
        PlannerDetails.GetWikipediaUrl(BuildState(scored, db: null), [scored]).ShouldBeNull();

        // Selection out of range (empty filtered list) -> null.
        PlannerDetails.GetWikipediaUrl(BuildState(scored, db: null), []).ShouldBeNull();
    }
}
