using System;
using System.Collections.Immutable;
using System.Linq;
using DIR.Lib;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.MeteorShowers;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A meteor shower is found by name in the atlas's and the planner's search and pinned on a night it is active, the way a
/// comet is, without ever entering <see cref="ICelestialObjectDB"/> (docs/plans/yearly-planner.md, E).
/// </summary>
[Collection("Astrometry")]
public class MeteorShowerSearchAndPinTests
{
    private static readonly DateTimeOffset PerseidNight = new(2026, 8, 12, 21, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextNight = PerseidNight.AddDays(1);
    private static readonly DateTimeOffset WinterNight = new(2026, 3, 20, 21, 0, 0, TimeSpan.Zero);

    private const double Latitude = 50.0;
    private const double Longitude = 8.0;

    private static MeteorShower Perseids => MeteorShowerTable.ByCode("PER").ShouldNotBeNull();

    private static ICelestialObjectDB EmptyDb()
    {
        var db = Substitute.For<ICelestialObjectDB>();
        db.CreateAutoCompleteList().Returns([]);
        return db;
    }

    private static PlannerState StateAt(DateTimeOffset dark, ICelestialObjectDB? db = null)
        => new()
        {
            AstroDark = dark,
            AstroTwilight = dark.AddHours(6),
            MinHeightAboveHorizon = 0,
            SiteLatitude = Latitude,
            SiteLongitude = Longitude,
            ObjectDb = db ?? EmptyDb(),
        };

    private static Transform TransformAt(DateTimeOffset dark)
        => new(SystemTimeProvider.Instance)
        {
            SiteLatitude = Latitude,
            SiteLongitude = Longitude,
            SiteElevation = 100,
            SiteTemperature = 10,
            DateTimeOffset = dark,
        };

    private static Target ShowerTarget(DateTimeOffset night)
    {
        var radiant = MeteorShowerNights.RadiantAt(Perseids, night);
        return new Target(radiant.RaHours, radiant.DecDeg, Perseids.Name, MeteorShowerIdentity.IndexOf(Perseids));
    }

    [Fact]
    public void MeteorShowerIdentityRoundTripsEveryShowerAndIsNotASolarSystemBody()
    {
        foreach (var shower in MeteorShowerTable.All)
        {
            var index = MeteorShowerIdentity.IndexOf(shower);
            index.ToCatalog().ShouldBe(Catalog.MeteorShower);
            index.IsMeteorShower.ShouldBeTrue();
            index.IsSolarSystemObject.ShouldBeFalse("the planet and comet ephemeris paths must not claim a shower");
            MeteorShowerIdentity.TryGet(index, out var back).ShouldBeTrue();
            back.ShouldBe(shower);
        }

        MeteorShowerTable.All.Select(MeteorShowerIdentity.IndexOf).Distinct().Count().ShouldBe(MeteorShowerTable.All.Length);
    }

    [Fact]
    public void MeteorShowerPlannerAutoCompleteListHoldsPerseidsAndStaysSorted()
    {
        var list = PlannerActions.BuildAutoCompleteList(EmptyDb(), comets: null);

        list.ShouldContain("Perseids");
        list.ShouldBe(list.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void MeteorShowerAtlasSearchFindsPerseidsAndCommitCentresThatNightsRadiant()
    {
        var db = EmptyDb();
        var search = new SkyMapSearchState();
        SkyMapSearchActions.OpenSearch(search, db, new TextInputFocus());

        search.SearchIndex.ShouldContain("Perseids");
        var hits = SkyMapSearchActions.FilterResults(search, db, "Perseids");
        var hit = hits.ShouldHaveSingleItem();
        hit.ObjType.ShouldBe(ObjectType.MeteorShower);
        hit.Index.ShouldBe(MeteorShowerIdentity.IndexOf(Perseids));

        var skyMap = new SkyMapState { Mode = SkyMapMode.Equatorial, FieldOfViewDeg = 20.0 };
        var site = SiteContext.Create(Latitude, Longitude, PerseidNight);
        SkyMapSearchActions.CommitResult(search, skyMap, db, hit, new TextInputFocus(), Latitude, Longitude, PerseidNight, site)
            .ShouldBeTrue();

        var radiant = MeteorShowerNights.RadiantAt(Perseids, MeteorShowerNights.NightInstant(PerseidNight, Longitude));
        radiant.Active.ShouldBeTrue();
        var info = search.InfoPanel.ShouldNotBeNull();
        info.RA.ShouldBe(radiant.RaHours, 1e-9);
        info.Dec.ShouldBe(radiant.DecDeg, 1e-9);
        info.ObjType.ShouldBe(ObjectType.MeteorShower);
    }

    [Fact]
    public void MeteorShowerPlannerSearchOffersPerseidsOnAnActiveNightAndNothingOutsideTheWindow()
    {
        var active = StateAt(PerseidNight);
        PlannerActions.SearchTargets(active, EmptyDb(), TransformAt(PerseidNight), "Perseids");
        var found = active.SearchResults.ShouldHaveSingleItem();
        found.Target.CatalogIndex.ShouldBe(MeteorShowerIdentity.IndexOf(Perseids));
        found.ObjectType.ShouldBe(ObjectType.MeteorShower);

        var inactive = StateAt(WinterNight);
        PlannerActions.SearchTargets(inactive, EmptyDb(), TransformAt(WinterNight), "Perseids");
        inactive.SearchResults.ShouldBeEmpty();
    }

    [Fact]
    public void MeteorShowerPinnedOneNightIsTheSamePinTheNextAlthoughItsRadiantMoved()
    {
        var first = ShowerTarget(PerseidNight);
        var second = ShowerTarget(NextNight);
        first.ShouldNotBe(second, "the radiant drifts, so the positional records differ");
        (second.RA - first.RA).ShouldBeGreaterThan(0.0);

        PlannerActions.IsSameObject(first, second).ShouldBeTrue();

        var state = StateAt(PerseidNight);
        PlannerActions.ToggleProposal(state, first);
        state.Proposals.Length.ShouldBe(1);

        // The next night: the pin is re-placed from the table, and is still one row.
        state.AstroDark = NextNight;
        PlannerActions.RecomputeHandoffSliders(state);
        state.Proposals.Length.ShouldBe(1);
        var placed = MeteorShowerNights.RadiantAt(Perseids, PlannerActions.ShowerNightInstant(state));
        state.Proposals[0].Target.RA.ShouldBe(placed.RaHours, 1e-9);
        state.Proposals[0].Target.Dec.ShouldBe(placed.DecDeg, 1e-9);
        state.Proposals[0].Target.RA.ShouldBeGreaterThan(first.RA, "the next night's radiant, not the one it was pinned at");
        PlannerActions.GetFilteredTargets(state).Count.ShouldBe(1);

        // Pinning it again is a no-op, and toggling by the other night's value removes the one pin.
        PlannerActions.ToggleProposal(state, second);
        state.Proposals.ShouldBeEmpty();
    }

    [Fact]
    public void MeteorShowerPinOutsideItsWindowIsRefused()
    {
        var state = StateAt(WinterNight);
        var target = ShowerTarget(WinterNight);

        PlannerActions.CanPin(state, target).ShouldBeFalse();
        PlannerActions.ToggleProposal(state, target);
        state.Proposals.ShouldBeEmpty();

        var onTheNight = StateAt(PerseidNight);
        PlannerActions.CanPin(onTheNight, ShowerTarget(PerseidNight)).ShouldBeTrue();
    }

    [Fact]
    public void MeteorShowerRestoredPinHasFiniteCoordinatesAndOneRow()
    {
        var state = StateAt(PerseidNight);
        PlannerActions.ToggleProposal(state, ShowerTarget(PerseidNight));
        var json = PlannerPersistence.SerializeToJson(state);

        // A fresh planner on the next night: the DB holds no shower, yet the pin comes back.
        var restored = StateAt(NextNight);
        PlannerPersistence.TryRestoreFromJson(restored, json, NullLogger.Instance).ShouldBeTrue();

        var pin = restored.Proposals.ShouldHaveSingleItem();
        pin.Target.CatalogIndex.ShouldBe(MeteorShowerIdentity.IndexOf(Perseids));
        double.IsFinite(pin.Target.RA).ShouldBeTrue();
        double.IsFinite(pin.Target.Dec).ShouldBeTrue();
        PlannerActions.GetFilteredTargets(restored).Count.ShouldBe(1);
    }

    [Fact]
    public void MeteorShowerRestoredPinWhoseSavedPositionIsNotANumberComesBackAtTheRadiant()
    {
        // The repair branch of the restore: a plan saved with no position (as the object DB's NaN sentinel once gave a planet).
        var dto = new PlannerSessionDto(
            [new ProposalDto(double.NaN, double.NaN, Perseids.Name, (ulong)MeteorShowerIdentity.IndexOf(Perseids),
                ObservationPriority.Normal, null, null, null)],
            [], 0, 0f, Latitude, Longitude);

        var restored = StateAt(PerseidNight);
        PlannerPersistence.TryRestoreFromDto(restored, dto, NullLogger.Instance).ShouldBeTrue();

        var pin = restored.Proposals.ShouldHaveSingleItem();
        var radiant = MeteorShowerNights.RadiantAt(Perseids, PlannerActions.ShowerNightInstant(restored));
        pin.Target.RA.ShouldBe(radiant.RaHours, 0.2, "the table's radiant, then re-placed for the planner's night");
        pin.Target.Dec.ShouldBe(radiant.DecDeg, 1.0);
        PlannerActions.GetFilteredTargets(restored).Count.ShouldBe(1);
    }

    [Fact]
    public void MeteorShowerNightIsJudgedAtOneInstantByTheCalendarThePlannerAndTheAtlas()
    {
        // The nights around the hour the Perseids' window opens: whatever the calendar says of a night, the planner's pin
        // and the atlas's radiant say the same, at dusk, in the small hours and at dawn alike.
        var opens = FirstInstantInside(Perseids);
        var index = MeteorShowerIdentity.IndexOf(Perseids);
        var target = ShowerTarget(opens.AddDays(10));
        int named = 0, bare = 0;
        for (var d = -3; d <= 3; d++)
        {
            var evening = DateOnly.FromDateTime(opens.UtcDateTime).AddDays(d);
            var summary = NightSummary.Compute(TransformAt(new DateTimeOffset(evening.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)), evening);
            var calendar = MeteorShowerNights.For(Latitude, Longitude, summary).Any(n => n.Shower == Perseids);

            var state = StateAt(summary.DarkStart);
            PlannerActions.CanPin(state, target).ShouldBe(calendar, $"{evening}: the planner's pin against the calendar");

            var sky = new SkyMapState { ShowerSiteLongitudeDeg = Longitude };
            foreach (var instant in new[] { summary.DarkStart, summary.DarkStart + (summary.Dark / 2), summary.DarkEnd })
            {
                sky.GetShowerRadiantsCached(instant).ToArray().Any(m => m.Index == index)
                    .ShouldBe(calendar, $"{evening} at {instant:u}: the atlas's radiant against the calendar");
            }

            sky.ShowerCacheRebuilds.ShouldBe(1, "one night is one computation, whatever the hour");
            _ = calendar ? named++ : bare++;
        }

        named.ShouldBeGreaterThan(0);
        bare.ShouldBeGreaterThan(0, "the sweep crosses the window's opening");
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(151.2)]
    [InlineData(0.0)]
    [InlineData(-122.0)]
    [InlineData(double.NaN)]
    public void MeteorShowerPlannerWithASiteAndNoNightYetRefusesAPinFindsNothingAndDoesNotThrow(double longitude)
    {
        // The site is set before the planner has computed its first night: AstroDark is still default. East of Greenwich
        // the night's midnight then falls before the first instant there is.
        var state = new PlannerState
        {
            SiteLatitude = Latitude,
            SiteLongitude = longitude,
            MinHeightAboveHorizon = 0,
            ObjectDb = EmptyDb(),
        };
        state.AstroDark.ShouldBe(default(DateTimeOffset));
        var target = ShowerTarget(PerseidNight);

        PlannerActions.CanPin(state, target).ShouldBeFalse();
        PlannerActions.ToggleProposal(state, target);
        state.Proposals.ShouldBeEmpty();

        PlannerActions.SearchTargets(state, EmptyDb(), TransformAt(PerseidNight), "Perseids");
        state.SearchResults.ShouldBeEmpty();

        // A plan restored before the night is known already holds a shower pin: re-placing it must not throw.
        state.Proposals = [new ProposedObservation(target, ObjectType.MeteorShower)];
        Should.NotThrow(() => PlannerActions.RecomputeHandoffSliders(state));
        state.Proposals.Length.ShouldBe(1, "the pin is kept, and stays removable");
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(180.0)]
    [InlineData(-180.0)]
    [InlineData(0.0)]
    public void MeteorShowerNightInstantNeverThrowsAtTheEdgesOfTime(double longitude)
    {
        // A midnight no DateTimeOffset can hold is answered as the instant itself.
        Should.NotThrow(() => MeteorShowerNights.NightInstant(default, longitude));
        Should.NotThrow(() => MeteorShowerNights.NightInstant(DateTimeOffset.MinValue, longitude));
        Should.NotThrow(() => MeteorShowerNights.NightInstant(DateTimeOffset.MaxValue, longitude));
        Should.NotThrow(() => MeteorShowerNights.NightKey(DateTimeOffset.MaxValue, longitude));
        MeteorShowerNights.RadiantAt(Perseids, MeteorShowerNights.NightInstant(default, longitude)).Active
            .ShouldBeFalse("no shower is active at an instant that has no night");
    }

    [Fact]
    public void MeteorShowerAtlasSetIsReckonedAgainWhenTheSiteMoves()
    {
        var sky = new SkyMapState { ShowerSiteLongitudeDeg = 8.0 };
        sky.GetShowerRadiantsCached(PerseidNight).Length.ShouldBeGreaterThan(0);
        sky.GetShowerRadiantsCached(PerseidNight.AddHours(1)).Length.ShouldBeGreaterThan(0);
        sky.ShowerCacheRebuilds.ShouldBe(1);

        sky.ShowerSiteLongitudeDeg = 151.2;
        sky.GetShowerRadiantsCached(PerseidNight.AddHours(1)).Length.ShouldBeGreaterThan(0);
        sky.ShowerCacheRebuilds.ShouldBe(2, "another longitude is another night's question, the instant being the same");

        sky.ShowerSiteLongitudeDeg = double.NaN;
        sky.GetShowerRadiantsCached(PerseidNight).Length.ShouldBeGreaterThan(0);
        sky.GetShowerRadiantsCached(PerseidNight).Length.ShouldBeGreaterThan(0);
        sky.ShowerCacheRebuilds.ShouldBe(3, "a longitude that is not a number is 0, and is still cached");
    }

    [Fact]
    public void MeteorShowerNightInstantIsSolarMidnightOfTheEveningTheInstantBelongsTo()
    {
        // 8 degrees east: solar midnight is 23:28 UT. From one solar noon to the next is one night.
        var midnight = new DateTimeOffset(2026, 8, 12, 23, 28, 0, TimeSpan.Zero);
        foreach (var within in new[] { midnight.AddHours(-11.9), midnight.AddHours(-3), midnight, midnight.AddHours(4), midnight.AddHours(11.9) })
        {
            MeteorShowerNights.NightInstant(within, 8.0).ShouldBe(midnight, TimeSpan.FromSeconds(1));
        }

        MeteorShowerNights.NightInstant(midnight.AddHours(12.1), 8.0).ShouldBe(midnight.AddDays(1), TimeSpan.FromSeconds(1));
        MeteorShowerNights.NightKey(midnight.AddHours(12.1), 8.0).ShouldBe(MeteorShowerNights.NightKey(midnight, 8.0) + 1);
        MeteorShowerNights.NightInstant(midnight, double.NaN).ShouldBe(new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>The first whole hour of 2026 at which the shower's window is open, found by walking back from its peak.</summary>
    private static DateTimeOffset FirstInstantInside(MeteorShower shower)
    {
        var t = MeteorShowerCalendar.PeakNear(shower, PerseidNight).ShouldNotBeNull();
        t = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 24 * 120 && MeteorShowerNights.RadiantAt(shower, t.AddHours(-1)).Active; i++)
        {
            t = t.AddHours(-1);
        }

        return t;
    }

    [Fact]
    public void MeteorShowerRadiantAtSaysWhetherTheNightIsInsideTheWindow()
    {
        MeteorShowerNights.RadiantAt(Perseids, PerseidNight).Active.ShouldBeTrue();
        var outside = MeteorShowerNights.RadiantAt(Perseids, WinterNight);
        outside.Active.ShouldBeFalse();
        double.IsFinite(outside.RaDeg).ShouldBeTrue();
        double.IsFinite(outside.DecDeg).ShouldBeTrue();
    }
}
