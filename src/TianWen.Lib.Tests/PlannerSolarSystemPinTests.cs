using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the planner's handling of a pinned SOLAR-SYSTEM target -- the class of object whose RA/Dec
    /// is an ephemeris value rather than an identity.
    /// <para>
    /// The reported defect was "Venus is in a proposal but doesn't appear in the planner so I can't
    /// remove it". <see cref="Target"/> is a positional record, so its equality includes the position,
    /// and every planner recompute resolves a planet at a new instant -- producing a <c>Target</c> value
    /// that matches nothing in the scored lists. The pinned row was therefore dropped from
    /// <see cref="PlannerActions.GetFilteredTargets"/>, and since the row IS the unpin affordance, the
    /// proposal became invisible and permanently unremovable while still being saved and scheduled.
    /// </para>
    /// </summary>
    public class PlannerSolarSystemPinTests
    {
        private static readonly DateTimeOffset NightStart = new(2026, 3, 20, 19, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset NightEnd = new(2026, 3, 21, 5, 0, 0, TimeSpan.Zero);

        /// <summary>Venus as the planner materialises it: a real position resolved at some instant.</summary>
        private static Target VenusAt(double ra, double dec) => new(ra, dec, "Venus", CatalogIndex.Venus);

        private static Target Dso(string name) => new(5.5, 22.0, name, CatalogIndex.NGC1976);

        private static ScoredTarget Score(Target target, double altitude = 45.0) => new(
            target,
            (Half)1.0,
            (Half)1.0,
            new Dictionary<RaDecEventTime, RaDecEventInfo>(),
            OptimalStart: NightStart + TimeSpan.FromHours(1),
            OptimalDuration: TimeSpan.FromHours(2),
            OptimalAltitude: altitude,
            ObjectType: ObjectType.Planet);

        private static PlannerState StateWith(params ScoredTarget[] tonightsBest)
        {
            var state = new PlannerState
            {
                AstroDark = NightStart,
                AstroTwilight = NightEnd,
                MinHeightAboveHorizon = 0,
                SiteLatitude = -37.8,
                SiteLongitude = 145.0,
            };

            state.TonightsBest = [.. tonightsBest];
            var scored = ImmutableDictionary.CreateBuilder<Target, ScoredTarget>();
            foreach (var s in tonightsBest)
            {
                scored[s.Target] = s;
            }
            state.ScoredTargets = scored.ToImmutable();
            return state;
        }

        /// <summary>
        /// The reported bug, end to end: a pin whose scored entry has been wiped by a full recompute
        /// (which rebuilds ScoredTargets from tonight's list alone, and the scheduler never sweeps
        /// planets) must still produce a row, and that row must be removable.
        /// </summary>
        [Fact]
        public void APinnedPlanetWithNoScoredEntryAnywhereIsStillListedAndRemovable()
        {
            var venus = VenusAt(ra: 1.5, dec: 12.0);
            var state = StateWith(Score(Dso("NGC 1976")));

            // Pin Venus, then simulate the full recompute that drops every off-list scored entry.
            state.Proposals = [new ProposedObservation(venus, ObjectType.Planet)];
            state.ScoredTargets = state.ScoredTargets.Remove(venus);
            state.SearchResults = [];

            var filtered = PlannerActions.GetFilteredTargets(state);

            state.PinnedCount.ShouldBe(1);
            filtered[0].Target.ShouldBe(venus);

            // The removal path the row's [-] button and the keyboard toggle both use.
            var proposalIndex = PlannerActions.FindProposalIndex(state.Proposals, filtered[0].Target);
            proposalIndex.ShouldBe(0);

            PlannerActions.RemoveProposal(state, proposalIndex);
            state.Proposals.ShouldBeEmpty();
        }

        /// <summary>
        /// A planet pinned at one instant re-binds to tonight's freshly-computed entry, so the row
        /// carries the current altitude and score rather than a stale synthetic zero.
        /// </summary>
        [Fact]
        public void APinnedPlanetRebindsToTonightsFreshlyComputedPosition()
        {
            var pinnedVenus = VenusAt(ra: 1.5, dec: 12.0);
            var tonightsVenus = VenusAt(ra: 2.1, dec: 14.5);

            // Same body, different ephemeris instant -> different record values.
            pinnedVenus.ShouldNotBe(tonightsVenus);

            var state = StateWith(Score(tonightsVenus, altitude: 62.0));
            state.Proposals = [new ProposedObservation(pinnedVenus, ObjectType.Planet)];

            var filtered = PlannerActions.GetFilteredTargets(state);

            state.PinnedCount.ShouldBe(1);
            filtered[0].Target.ShouldBe(tonightsVenus);
            filtered[0].OptimalAltitude.ShouldBe(62.0);
        }

        /// <summary>
        /// Re-binding must not also leave the object in the unpinned section: "already pinned" is an
        /// object test, so tonight's Venus row is suppressed by the pinned Venus even though the two
        /// Target values differ.
        /// </summary>
        [Fact]
        public void ARepositionedPlanetAppearsExactlyOnce()
        {
            var pinnedVenus = VenusAt(ra: 1.5, dec: 12.0);
            var tonightsVenus = VenusAt(ra: 2.1, dec: 14.5);

            var state = StateWith(Score(tonightsVenus), Score(Dso("NGC 1976")));
            state.Proposals = [new ProposedObservation(pinnedVenus, ObjectType.Planet)];

            var filtered = PlannerActions.GetFilteredTargets(state);

            filtered.Count(t => t.Target.CatalogIndex == CatalogIndex.Venus).ShouldBe(1);
            state.PinnedCount.ShouldBe(1);
        }

        /// <summary>
        /// The invariant that makes the whole class of bug impossible: the pinned section is a
        /// projection of Proposals, so its length is the proposal count -- never a filtered subset.
        /// This is also what the N-1 HandoffSliders indexing assumes.
        /// </summary>
        [Fact]
        public void PinnedCountAlwaysEqualsTheProposalCount()
        {
            var resolvable = Dso("NGC 1976");
            var orphanedPlanet = VenusAt(ra: 1.5, dec: 12.0);
            var orphanedManual = new Target(3.0, -8.0, "RA 3h Dec -8", null);

            var state = StateWith(Score(resolvable));
            state.Proposals =
            [
                new ProposedObservation(resolvable),
                new ProposedObservation(orphanedPlanet, ObjectType.Planet),
                new ProposedObservation(orphanedManual),
            ];

            PlannerActions.GetFilteredTargets(state);

            state.PinnedCount.ShouldBe(state.Proposals.Length);
        }

        /// <summary>
        /// Toggling an already-pinned planet by a DIFFERENT ephemeris position unpins it rather than
        /// adding a second pin for the same body -- the trap that comes with object-wise re-binding,
        /// since the row now hands back tonight's Target and not the one stored in Proposals.
        /// </summary>
        [Fact]
        public void TogglingAPinnedPlanetByAnotherPositionUnpinsRatherThanDuplicating()
        {
            var pinnedVenus = VenusAt(ra: 1.5, dec: 12.0);
            var tonightsVenus = VenusAt(ra: 2.1, dec: 14.5);

            var state = StateWith(Score(tonightsVenus));
            state.Proposals = [new ProposedObservation(pinnedVenus, ObjectType.Planet)];

            PlannerActions.ToggleProposal(state, tonightsVenus);

            state.Proposals.ShouldBeEmpty();
        }

        /// <summary>
        /// A fixed object keeps exact value equality: same catalog index but a different position is a
        /// DIFFERENT target (mosaic panels share an index and differ only by their offset centre), so
        /// the solar-system rule must not leak into the general case.
        /// </summary>
        [Fact]
        public void TwoOffsetPositionsOfOneFixedObjectRemainDistinct()
        {
            var panelA = new Target(5.575, -5.39, "NGC 1976", CatalogIndex.NGC1976);
            var panelB = new Target(5.595, -5.21, "NGC 1976", CatalogIndex.NGC1976);

            PlannerActions.IsSameObject(panelA, panelB).ShouldBeFalse();
            PlannerActions.IsSameObject(panelA, panelA).ShouldBeTrue();
        }

        [Fact]
        public void OnePlanetAtTwoInstantsIsOneObject()
        {
            PlannerActions.IsSameObject(VenusAt(1.5, 12.0), VenusAt(2.1, 14.5)).ShouldBeTrue();
        }

        /// <summary>
        /// The reported pin, byte for byte, as it sat in
        /// <c>AppData/Planner/{profile}/2026-07-29.json</c> when the bug was filed: catalog index
        /// 1324630 (= <see cref="CatalogIndex.Venus"/>, packed 'P','l','V'), at the position Venus held
        /// when it was pinned. Tonight's Venus is elsewhere, which is the whole failure -- so this pins
        /// that the real payload lists and is removable rather than vanishing.
        /// </summary>
        [Fact]
        public void TheReportedVenusPinListsAndIsRemovable()
        {
            const ulong SavedIndex = 1324630;
            ((CatalogIndex)SavedIndex).ShouldBe(CatalogIndex.Venus);

            var pinnedVenus = VenusAt(ra: 10.148449221283096, dec: 12.988162264841751);
            var state = StateWith(Score(Dso("NGC 1976")));
            state.Proposals = [new ProposedObservation(pinnedVenus, ObjectType.Planet)];

            var filtered = PlannerActions.GetFilteredTargets(state);

            state.PinnedCount.ShouldBe(1);
            filtered[0].Target.Name.ShouldBe("Venus");

            PlannerActions.RemoveProposal(state, PlannerActions.FindProposalIndex(state.Proposals, filtered[0].Target));
            state.Proposals.ShouldBeEmpty();
        }

        /// <summary>
        /// The restore flavour of the same defect. Solar-system bodies are stored in the object DB with
        /// NaN coordinates (their position is ephemeris-computed), and the restore fallback took those
        /// verbatim -- producing a NaN-positioned pin that matched no scored entry and no altitude
        /// profile. The saved proposal's own RA/Dec is a real position, so it wins over the sentinel.
        /// </summary>
        [Fact]
        public void RestoringAPinnedPlanetDoesNotProduceANaNPosition()
        {
            var db = Substitute.For<ICelestialObjectDB>();
            db.TryLookupByIndex(CatalogIndex.Venus, out Arg.Any<CelestialObject>())
                .Returns(call =>
                {
                    call[1] = new CelestialObject(
                        CatalogIndex.Venus, ObjectType.Planet,
                        double.NaN, double.NaN, default, (Half)(-4.14), (Half)0, (Half)0.82,
                        new HashSet<string>(["Venus"]));
                    return true;
                });

            var state = new PlannerState
            {
                AstroDark = NightStart,
                AstroTwilight = NightEnd,
                SiteLatitude = -37.8,
                SiteLongitude = 145.0,
                ObjectDb = db,
            };

            var dto = new PlannerSessionDto(
                Proposals:
                [
                    new ProposalDto(RA: 1.5, Dec: 12.0, Name: "Venus",
                        CatalogIndex: (ulong)CatalogIndex.Venus,
                        Priority: ObservationPriority.Normal,
                        SubExposureSeconds: null, ObservationTimeMinutes: null, MosaicGroupId: null),
                ],
                Sliders: [],
                MinHeightAboveHorizon: 20,
                MinRatingFilter: 0f,
                SiteLatitude: -37.8,
                SiteLongitude: 145.0);

            PlannerPersistence.TryRestoreFromDto(state, dto, NullLogger.Instance).ShouldBeTrue();

            var restored = state.Proposals.ShouldHaveSingleItem();
            double.IsNaN(restored.Target.RA).ShouldBeFalse("a NaN-positioned pin can never resolve or be drawn");
            double.IsNaN(restored.Target.Dec).ShouldBeFalse();
            restored.Target.RA.ShouldBe(1.5);
            restored.Target.CatalogIndex.ShouldBe(CatalogIndex.Venus);

            // And it lists, so it can be removed.
            PlannerActions.GetFilteredTargets(state);
            state.PinnedCount.ShouldBe(1);
        }

        // A comet pin stores a position, and a comet MOVES: a pin made last week points at empty sky,
        // and the altitude profile scored from it is for a place the comet has left. The refresh runs
        // on the proposal-change hook (bounded) rather than per frame; the sky-map marker is separately
        // live to the second.
        private static CometElements TenP => StubCometRepository.Comet("10P", "Tempel");

        [Fact]
        public void APinnedCometsPositionIsRefreshedFromTheEphemeris()
        {
            var comet = TenP;
            var index = comet.CatalogIndex.ShouldNotBeNull();
            var repo = new StubCometRepository(comet);
            repo.Positions[index] = (RaHours: 6.0, DecDeg: -20.0, VMag: 12.8);

            var state = StateWith();
            state.Comets = repo;
            // Pinned days ago, 15 degrees away from where it is now.
            state.Proposals = [new ProposedObservation(new Target(5.0, -20.0, "10P/Tempel", index), ObjectType.Comet)];

            PlannerActions.RefreshCometProposalPositions(state);

            var refreshed = state.Proposals.ShouldHaveSingleItem();
            refreshed.Target.RA.ShouldBe(6.0);
            refreshed.Target.Dec.ShouldBe(-20.0);
            // Identity is unchanged, which is what makes rewriting the position safe: a pin is matched
            // by CatalogIndex for a solar-system body, never by the Target's value.
            refreshed.Target.CatalogIndex.ShouldBe(index);
            refreshed.ObjectType.ShouldBe(ObjectType.Comet);
        }

        [Fact]
        public void ACometThatHasBarelyMovedIsLeftAlone()
        {
            // The proposal list is an ImmutableArray the render thread reads, so it is only replaced
            // when there is something to see; re-resolving the same instant must not churn it.
            var comet = TenP;
            var index = comet.CatalogIndex.ShouldNotBeNull();
            var repo = new StubCometRepository(comet);
            repo.Positions[index] = (RaHours: 5.0 + 0.0001, DecDeg: -20.0, VMag: 12.8);

            var state = StateWith();
            state.Comets = repo;
            var original = state.Proposals = [new ProposedObservation(new Target(5.0, -20.0, "10P/Tempel", index), ObjectType.Comet)];

            PlannerActions.RefreshCometProposalPositions(state);

            state.Proposals.ShouldBe(original);
        }

        [Fact]
        public void ADeepSkyPinIsNeverRewritten()
        {
            // A DSO does not move, so touching it would be pure churn -- and the refresh must not
            // reach for a comet ephemeris on an index that is not a comet.
            var repo = new StubCometRepository(TenP);
            var state = StateWith();
            state.Comets = repo;
            var original = state.Proposals = [new ProposedObservation(Dso("NGC 1976"), ObjectType.Galaxy)];

            PlannerActions.RefreshCometProposalPositions(state);

            state.Proposals.ShouldBe(original);
        }

        [Fact]
        public void WithNoCometRepositoryTheProposalsAreUntouched()
        {
            var state = StateWith();
            var original = state.Proposals = [new ProposedObservation(Dso("NGC 1976"), ObjectType.Galaxy)];

            PlannerActions.RefreshCometProposalPositions(state);

            state.Proposals.ShouldBe(original);
        }
    }
}
