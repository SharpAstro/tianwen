using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The tonight's-best CANDIDATE cache: the catalog sweep's output, persisted so a repeat visit can
    /// skip the scan and go straight to <see cref="PlannerActions.RecomputeForDate"/>.
    ///
    /// <para><b>Why it exists.</b> Measured on the deployed browser build, a cold boot against the same
    /// boot reloaded: catalog init 1306 -> 1330 ms, sweep 1507 -> 1482 ms. Unchanged, both of them,
    /// while the Tycho-2 star tiles went 12305 -> 998 ms off IndexedDB -- the planner's own costs were
    /// the only part of a boot that nothing cached. The catalog is embedded in the assembly so there is
    /// no fetch to cache, but the sweep is pure computation over it, and its candidate set is the
    /// cacheable part. The rescore that replaces it measures ~170 ms.</para>
    ///
    /// <para><b>What must NOT be cached is the interesting half.</b> Scores, altitude profiles and the
    /// night window are all functions of the date; <c>RecomputeForDate</c> rebuilds every one from the
    /// target list alone. Persisting them would multiply the payload by the profile arrays and then
    /// discard them on restore -- and, far worse, a restore that skipped the rescore would show
    /// yesterday's scores as though they were tonight's.</para>
    /// </summary>
    public class TonightsBestCacheTests
    {
        private const double Lat = -37.8;
        private const double Lon = 145.0;
        private const byte MinAlt = 20;
        private static readonly DateTimeOffset Night = new(2026, 8, 17, 21, 0, 0, TimeSpan.FromHours(10));

        private static ScoredTarget Score(Target target, ObjectType type, double score) => new(
            target,
            (Half)score,
            (Half)1.0,
            new Dictionary<RaDecEventTime, RaDecEventInfo>(),
            OptimalStart: Night,
            OptimalDuration: TimeSpan.FromHours(2),
            OptimalAltitude: 55.0,
            ObjectType: type);

        private static PlannerState StateWithSweep()
        {
            var state = new PlannerState
            {
                SiteLatitude = Lat,
                SiteLongitude = Lon,
                MinHeightAboveHorizon = MinAlt,
            };
            state.TonightsBest =
            [
                Score(new Target(5.59, -5.39, "Orion Nebula", CatalogIndex.NGC1976), ObjectType.HIIReg, 0.9),
                Score(new Target(13.42, 47.19, "Whirlpool", CatalogIndex.NGC5194), ObjectType.Galaxy, 0.7),
                // No catalog index: a manually entered RA/Dec target must survive the round trip too.
                Score(new Target(2.5, 61.0, "Dark Patch", null), ObjectType.DarkNeb, 0.5),
            ];
            return state;
        }

        private static PlannerState EmptyState() => new()
        {
            SiteLatitude = Lat,
            SiteLongitude = Lon,
            MinHeightAboveHorizon = MinAlt,
        };

        private static bool Restore(PlannerState into, string json,
            double lat = Lat, double lon = Lon, byte minAlt = MinAlt, DateTimeOffset? night = null)
            => PlannerPersistence.TryRestoreTonightsBest(
                into, json, lat, lon, minAlt, night ?? Night, NullLogger.Instance);

        [Fact]
        public void EveryCandidateSurvivesTheRoundTrip()
        {
            var saved = StateWithSweep();
            var json = PlannerPersistence.SerializeTonightsBest(saved, Lat, Lon, Night);

            var restored = EmptyState();
            Restore(restored, json).ShouldBeTrue();

            restored.TonightsBest.Length.ShouldBe(saved.TonightsBest.Length);
            for (var i = 0; i < saved.TonightsBest.Length; i++)
            {
                var before = saved.TonightsBest[i];
                var after = restored.TonightsBest[i];
                after.Target.ShouldBe(before.Target);
                after.ObjectType.ShouldBe(before.ObjectType);
            }
        }

        /// <summary>
        /// The restored entries are deliberately UNSCORED. The caller owes a
        /// <see cref="PlannerActions.RecomputeForDate"/>, and this is what makes forgetting it a
        /// visible zero rather than a plausible-looking stale score from another night.
        /// </summary>
        [Fact]
        public void RestoredCandidatesCarryNoScore()
        {
            var json = PlannerPersistence.SerializeTonightsBest(StateWithSweep(), Lat, Lon, Night);
            var restored = EmptyState();
            Restore(restored, json).ShouldBeTrue();

            restored.TonightsBest.ShouldAllBe(t => (double)t.TotalScore == 0.0);
            restored.TonightsBest.ShouldAllBe(t => t.ElevationProfile.Count == 0);
        }

        /// <summary>
        /// A week is fine (the candidate set is what clears the horizon during the dark window, which
        /// drifts about four minutes of RA a day, so seven days is ~28 minutes); a season is not.
        /// </summary>
        [Theory]
        [InlineData(0, true)]
        [InlineData(3, true)]
        [InlineData(-6, true)]
        [InlineData(9, false)]
        [InlineData(-40, false)]
        [InlineData(120, false)]
        public void TheCacheAgesOutOfTheNightItWasComputedFor(int daysLater, bool expected)
        {
            var json = PlannerPersistence.SerializeTonightsBest(StateWithSweep(), Lat, Lon, Night);
            var restored = EmptyState();

            Restore(restored, json, night: Night.AddDays(daysLater)).ShouldBe(expected);
        }

        /// <summary>
        /// Site and minimum altitude both change WHAT the sweep would have found, so a cache keyed to
        /// different ones is not merely stale, it is a different question's answer.
        /// </summary>
        [Theory]
        [InlineData(0.5, 0.0, MinAlt, true)]
        [InlineData(2.0, 0.0, MinAlt, false)]
        [InlineData(0.0, 3.0, MinAlt, false)]
        [InlineData(0.0, 0.0, (byte)40, false)]
        public void TheCacheIsRefusedWhenItWasComputedForADifferentQuestion(
            double dLat, double dLon, byte minAlt, bool expected)
        {
            var json = PlannerPersistence.SerializeTonightsBest(StateWithSweep(), Lat, Lon, Night);
            var restored = EmptyState();

            Restore(restored, json, Lat + dLat, Lon + dLon, minAlt).ShouldBe(expected);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("null")]
        [InlineData("{\"Version\":999,\"Candidates\":[]}")]
        public void GarbageAndForeignPayloadsAreRefusedRatherThanThrown(string json)
        {
            var restored = EmptyState();

            Restore(restored, json).ShouldBeFalse();
            restored.TonightsBest.ShouldBeEmpty();
        }

        /// <summary>
        /// An empty sweep must not be cached as a hit: it would pin the planner at zero targets for a
        /// week, and the recovery (a full sweep) is exactly what the cache would be suppressing.
        /// </summary>
        [Fact]
        public void AnEmptySweepIsNotAUsableCache()
        {
            var json = PlannerPersistence.SerializeTonightsBest(EmptyState(), Lat, Lon, Night);
            var restored = EmptyState();

            Restore(restored, json).ShouldBeFalse();
        }

        /// <summary>
        /// The payload is small enough for localStorage, which is where the browser host puts it (a
        /// ~5 MB per-origin budget shared with the pins). 100 candidates is the sweep's own cap.
        /// </summary>
        [Fact]
        public void AFullHundredCandidateSweepStaysSmall()
        {
            var state = EmptyState();
            state.TonightsBest = Enumerable.Range(0, 100)
                .Select(i => Score(
                    new Target(i * 0.24, i * 0.9 - 45.0, $"NGC {1000 + i}", CatalogIndex.NGC1976),
                    ObjectType.Galaxy, 0.5))
                .ToImmutableArray();

            var json = PlannerPersistence.SerializeTonightsBest(state, Lat, Lon, Night);

            json.Length.ShouldBeLessThan(32 * 1024);
        }
    }
}
