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

        var radiant = MeteorShowerNights.RadiantAt(Perseids, PerseidNight);
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
        state.Proposals[0].Target.RA.ShouldBe(second.RA, 1e-9);
        state.Proposals[0].Target.Dec.ShouldBe(second.Dec, 1e-9);
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
    public void MeteorShowerPinIsJudgedAtTheNightsMiddleAsTheCalendarJudgesIt()
    {
        // A night whose dark window STARTS before the Perseids' window opens and whose middle is inside it: the calendar
        // marks the shower active (mid-dark), so the planner must let it be pinned.
        var opens = FirstInstantInside(Perseids);
        var state = StateAt(opens.AddHours(-2));
        MeteorShowerNights.RadiantAt(Perseids, state.AstroDark).Active.ShouldBeFalse("the window is not open at dusk");
        MeteorShowerNights.RadiantAt(Perseids, PlannerActions.ShowerNightInstant(state)).Active.ShouldBeTrue();

        PlannerActions.CanPin(state, ShowerTarget(opens.AddHours(1))).ShouldBeTrue();
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
