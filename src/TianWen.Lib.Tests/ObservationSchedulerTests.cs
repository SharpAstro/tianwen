using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public sealed class ObservationSchedulerTests
{
    // Vienna, Austria — ~48.2°N, ~16.4°E
    private const double SiteLatitude = 48.2;
    private const double SiteLongitude = 16.4;
    private const byte MinHeight = 20;

    // Astro dark/twilight for a summer night in Vienna (approximate)
    private static readonly DateTimeOffset AstroDark = new DateTimeOffset(2025, 6, 15, 22, 30, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset AstroTwilight = new DateTimeOffset(2025, 6, 16, 3, 30, 0, TimeSpan.FromHours(2));

    private static Transform CreateTransform()
    {
        var transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = SiteLatitude,
            SiteLongitude = SiteLongitude,
            SiteElevation = 200,
            SiteTemperature = 15,
            DateTimeOffset = AstroDark
        };
        return transform;
    }

    // M42 — Orion Nebula, low in summer from Vienna
    private static readonly Target M42 = new Target(5.588, -5.39, "M42", null);

    // M13 — Hercules Cluster, high in summer from Vienna
    private static readonly Target M13 = new Target(16.695, 36.46, "M13", null);

    // M31 — Andromeda Galaxy, rises during the night in summer
    private static readonly Target M31 = new Target(0.712, 41.27, "M31", null);

    [Fact]
    public void ScoreTarget_HighAltitudeTarget_HasPositiveScore()
    {
        var transform = CreateTransform();
        var score = ObservationScheduler.ScoreTarget(M13, transform, AstroDark, AstroTwilight, MinHeight);

        score.TotalScore.ShouldBeGreaterThan(Half.Zero, "M13 should be well above horizon in summer from Vienna");
        score.OptimalDuration.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void ScoreTarget_LowTarget_HasLowerScoreThanHighTarget()
    {
        var transform = CreateTransform();
        var scoreM13 = ObservationScheduler.ScoreTarget(M13, transform, AstroDark, AstroTwilight, MinHeight);
        var scoreM31 = ObservationScheduler.ScoreTarget(M31, transform, AstroDark, AstroTwilight, MinHeight);

        // M13 at ~65° declination should score much higher than M31 at ~41° during summer from Vienna
        scoreM13.TotalScore.ShouldBeGreaterThan(scoreM31.TotalScore,
            "M13 (near zenith in summer) should score higher than M31 (rising late)");
    }

    [Fact]
    public void Schedule_EmptyProposals_ReturnsEmptyTree()
    {
        var transform = CreateTransform();
        var tree = ObservationScheduler.Schedule(
            [],
            transform,
            AstroDark,
            AstroTwilight,
            MinHeight,
            defaultGain: 120,
            defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(30)
        );

        tree.Count.ShouldBe(0);
    }

    [Fact]
    public void Schedule_SingleHighPriority_SchedulesSuccessfully()
    {
        var transform = CreateTransform();
        var proposals = new[]
        {
            new ProposedObservation(M13, Priority: ObservationPriority.High)
        };

        var tree = ObservationScheduler.Schedule(
            proposals,
            transform,
            AstroDark,
            AstroTwilight,
            MinHeight,
            defaultGain: 120,
            defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(30)
        );

        tree.Count.ShouldBe(1);
        tree[0].Target.ShouldBe(M13);
        tree[0].Priority.ShouldBe(ObservationPriority.High);
        tree[0].Gain.ShouldBe(120);
        tree[0].Offset.ShouldBe(10);
        tree[0].SubExposure.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Schedule_HighBeforeNormal_PriorityOrdering()
    {
        var transform = CreateTransform();
        var proposals = new[]
        {
            new ProposedObservation(M31, Priority: ObservationPriority.Normal),
            new ProposedObservation(M13, Priority: ObservationPriority.High),
        };

        var tree = ObservationScheduler.Schedule(
            proposals,
            transform,
            AstroDark,
            AstroTwilight,
            MinHeight,
            defaultGain: 0,
            defaultOffset: 0,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(30)
        );

        tree.Count.ShouldBeGreaterThanOrEqualTo(1);
        // High priority should be scheduled first
        tree[0].Priority.ShouldBe(ObservationPriority.High);
    }

    [Fact]
    public void Schedule_ExplicitGainOverridesDefault()
    {
        var transform = CreateTransform();
        var proposals = new[]
        {
            new ProposedObservation(M13, Gain: 200, Offset: 50)
        };

        var tree = ObservationScheduler.Schedule(
            proposals,
            transform,
            AstroDark,
            AstroTwilight,
            MinHeight,
            defaultGain: 120,
            defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(30)
        );

        tree.Count.ShouldBe(1);
        tree[0].Gain.ShouldBe(200);
        tree[0].Offset.ShouldBe(50);
    }

    [Fact]
    public void Schedule_SpareTargets_AttachedToSlots()
    {
        var transform = CreateTransform();
        var proposals = new[]
        {
            new ProposedObservation(M13, Priority: ObservationPriority.Normal),
            new ProposedObservation(M31, Priority: ObservationPriority.Spare),
        };

        var tree = ObservationScheduler.Schedule(
            proposals,
            transform,
            AstroDark,
            AstroTwilight,
            MinHeight,
            defaultGain: 0,
            defaultOffset: 0,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(30)
        );

        tree.Count.ShouldBeGreaterThanOrEqualTo(1);
        // Spare targets should not appear in the primary list
        foreach (var obs in tree)
        {
            obs.Priority.ShouldNotBe(ObservationPriority.Spare);
        }
    }

    [Fact]
    public void ResolveGain_ExplicitValue_TakesPrecedence()
    {
        var result = ObservationScheduler.ResolveGain(200, null, 0, 256, true);
        result.ShouldBe(200);
    }

    [Fact]
    public void ResolveGain_FromUri_UsedWhenNoExplicit()
    {
        var uri = new Uri("Camera://FakeDevice/FakeCamera1?gain=150#Test Camera");
        var result = ObservationScheduler.ResolveGain(null, uri, 0, 256, true);
        result.ShouldBe(150);
    }

    [Fact]
    public void ResolveGain_Interpolation_FallbackWhenNoUri()
    {
        var result = ObservationScheduler.ResolveGain(null, null, 0, 256, true);
        // 40% from 0 toward 256 ≈ 102
        result.ShouldBe((int)MathF.FusedMultiplyAdd(256, 0.4f, 0));
    }

    [Fact]
    public void ResolveOffset_ExplicitValue_TakesPrecedence()
    {
        var result = ObservationScheduler.ResolveOffset(50, null);
        result.ShouldBe(50);
    }

    [Fact]
    public void ResolveOffset_FromUri_UsedWhenNoExplicit()
    {
        var uri = new Uri("Camera://FakeDevice/FakeCamera1?offset=25#Test Camera");
        var result = ObservationScheduler.ResolveOffset(null, uri);
        result.ShouldBe(25);
    }

    [Fact]
    public void ResolveOffset_Default_ZeroWhenNothingAvailable()
    {
        var result = ObservationScheduler.ResolveOffset(null, null);
        result.ShouldBe(0);
    }

    [Fact]
    public void ScheduledObservationTree_TryGetNextSpare_ReturnsNullWhenNoSpares()
    {
        var tree = new ScheduledObservationTree(
        [
            new ScheduledObservation(M13, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30), false, FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(120)), 0, 0)
        ]);

        var spareIdx = 0;
        tree.TryGetNextSpare(0, ref spareIdx).ShouldBeNull();
    }

    [Fact]
    public void ScheduledObservationTree_TryGetNextSpare_ReturnsSparesThenNull()
    {
        var spare1 = new ScheduledObservation(M31, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30), false, FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(120)), 0, 0, ObservationPriority.Spare);
        var spare2 = new ScheduledObservation(M42, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30), false, FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(120)), 0, 0, ObservationPriority.Spare);

        var primary = ImmutableArray.Create(new ScheduledObservation(M13, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30), false, FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(120)), 0, 0));
        var spares = ImmutableDictionary<int, ImmutableArray<ScheduledObservation>>.Empty
            .Add(0, [spare1, spare2]);

        var tree = new ScheduledObservationTree(primary, spares);

        var spareIdx = 0;
        tree.TryGetNextSpare(0, ref spareIdx).ShouldBe(spare1);
        tree.TryGetNextSpare(0, ref spareIdx).ShouldBe(spare2);
        tree.TryGetNextSpare(0, ref spareIdx).ShouldBeNull();
    }

    [Theory]
    [InlineData(48.2, 16.4, "2025-06-15T00:00:00+02:00", 200, 2, 14)]     // Vienna, summer — short night
    [InlineData(-37.88, 145.17, "2025-06-15T00:00:00+10:00", 120, 8, 14)]  // Melbourne, winter (AEST +10) — long night
    [InlineData(51.4, 8.06, "2025-12-21T00:00:00+01:00", 400, 10, 14)]     // Northern Germany, winter solstice
    [InlineData(53.35, -6.26, "2025-06-21T00:00:00+01:00", 20, 1, 8)]      // Dublin, summer solstice — no astro dark, falls back to nautical twilight
    public void CalculateNightWindow_ReturnsValidWindow(double lat, double lon, string dateStr, double elevation, double minNightHours, double maxNightHours)
    {
        var dto = DateTimeOffset.Parse(dateStr, System.Globalization.CultureInfo.InvariantCulture);
        var transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = lat,
            SiteLongitude = lon,
            SiteElevation = elevation,
            SiteTemperature = 15,
            DateTimeOffset = dto
        };

        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);

        // Astro twilight should be after astro dark
        astroTwilight.ShouldBeGreaterThan(astroDark, "Astro twilight should be after astro dark");
        // Night duration should be within expected range
        var nightDuration = astroTwilight - astroDark;
        nightDuration.ShouldBeGreaterThan(TimeSpan.FromHours(minNightHours), $"Night should be at least {minNightHours} hours");
        nightDuration.ShouldBeLessThan(TimeSpan.FromHours(maxNightHours), $"Night should be less than {maxNightHours} hours");
    }

    [Fact]
    public void CalculateNightWindow_PolarNight_Returns24HourWindow()
    {
        // Tromsø, Norway — ~69.6°N, polar night in December
        var dto = new DateTimeOffset(2025, 12, 21, 0, 0, 0, TimeSpan.FromHours(1));
        var transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = 69.6,
            SiteLongitude = 18.95,
            SiteElevation = 100,
            SiteTemperature = -10,
            DateTimeOffset = dto
        };

        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);

        // In polar night, should get a 24-hour window (midnight to next midnight)
        var nightDuration = astroTwilight - astroDark;
        nightDuration.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromHours(20), "Polar night should have near 24h window");
    }

    [Theory]
    [InlineData(48.2, 16.4, "2025-06-15T00:00:00+02:00", 200)]    // Vienna, summer — short night
    [InlineData(48.2, 16.4, "2025-12-15T00:00:00+01:00", 200)]    // Vienna, winter — long night
    [InlineData(-37.88, 145.17, "2025-06-15T00:00:00+10:00", 120)] // Melbourne, winter
    [InlineData(51.4, 8.06, "2025-12-21T00:00:00+01:00", 400)]      // Northern Germany, winter solstice
    public void CalculateNightWindow_ThenTwilightBoundaries_OrderedCorrectly(double lat, double lon, string dateStr, double elevation)
    {
        var dto = DateTimeOffset.Parse(dateStr, System.Globalization.CultureInfo.InvariantCulture);
        var transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = lat,
            SiteLongitude = lon,
            SiteElevation = elevation,
            SiteTemperature = 15,
            DateTimeOffset = dto
        };

        // CalculateNightWindow mutates transform.DateTimeOffset — this is by design.
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);

        // Compute twilight boundaries AFTER CalculateNightWindow (the bug scenario).
        // Evening events must be derived from astroDark's date, not transform.DateTimeOffset.
        // Subtract 12h so that post-midnight astroDark (e.g., 00:23) maps to the previous day's evening.
        var eveningDate = astroDark.AddHours(-12);
        var eveningDayStart = new DateTimeOffset(eveningDate.Date, eveningDate.Offset);
        var morningDayStart = new DateTimeOffset(astroTwilight.Date, astroTwilight.Offset);

        transform.DateTimeOffset = eveningDayStart;
        var (_, _, civilS) = transform.EventTimes(EventType.CivilTwilight);
        transform.DateTimeOffset = eveningDayStart;
        var (_, _, nautS) = transform.EventTimes(EventType.NauticalTwilight);

        transform.DateTimeOffset = morningDayStart;
        var (_, civilR, _) = transform.EventTimes(EventType.CivilTwilight);
        transform.DateTimeOffset = morningDayStart;
        var (_, nautR, _) = transform.EventTimes(EventType.NauticalTwilight);

        // Evening: civilSet < nauticalSet < astroDark
        if (civilS is { Count: >= 1 } && nautS is { Count: >= 1 })
        {
            var civilSet = eveningDayStart + civilS[0];
            var nauticalSet = eveningDayStart + nautS[0];

            civilSet.ShouldBeLessThan(nauticalSet, "Civil twilight set should precede nautical set");
            nauticalSet.ShouldBeLessThan(astroDark, "Nautical set should precede astronomical dark");
        }

        // Morning: astroTwilight < nauticalRise < civilRise
        if (nautR is { Count: >= 1 } && civilR is { Count: >= 1 })
        {
            var nauticalRise = morningDayStart + nautR[0];
            var civilRise = morningDayStart + civilR[0];

            astroTwilight.ShouldBeLessThan(nauticalRise, "Astronomical twilight should precede nautical rise");
            nauticalRise.ShouldBeLessThan(civilRise, "Nautical rise should precede civil rise");
        }

        // Full chain: civilSet < nauticalSet < astroDark < astroTwilight < nauticalRise < civilRise
        astroDark.ShouldBeLessThan(astroTwilight, "Astro dark should precede astro twilight");
    }

    [Fact]
    public void CalculateNightWindow_ThenTwilightBoundaries_HighLatitudeSummer_PartialOrdering()
    {
        // Dublin, summer solstice — no astronomical twilight, falls back to nautical.
        // Civil twilight set may occur AFTER the nautical-derived "astro dark" because
        // the sun never drops below -18° (or even -12° fully), so only a partial chain applies.
        var dto = new DateTimeOffset(2025, 6, 21, 0, 0, 0, TimeSpan.FromHours(1));
        var transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = 53.35,
            SiteLongitude = -6.26,
            SiteElevation = 20,
            SiteTemperature = 15,
            DateTimeOffset = dto
        };

        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);
        astroDark.ShouldBeLessThan(astroTwilight, "Dark should precede twilight");

        // Night window falls back to nautical — should be short (1-8 hours per existing test)
        var nightDuration = astroTwilight - astroDark;
        nightDuration.ShouldBeGreaterThan(TimeSpan.FromHours(1));
        nightDuration.ShouldBeLessThan(TimeSpan.FromHours(8));

        // Civil twilight still exists at this latitude.
        // Subtract 12h so post-midnight astroDark maps to the previous day's evening.
        var eveningDate = astroDark.AddHours(-12);
        var eveningDayStart = new DateTimeOffset(eveningDate.Date, eveningDate.Offset);
        var morningDayStart = new DateTimeOffset(astroTwilight.Date, astroTwilight.Offset);

        transform.DateTimeOffset = eveningDayStart;
        var (_, _, civilS) = transform.EventTimes(EventType.CivilTwilight);
        civilS.ShouldNotBeNull();
        civilS.Count.ShouldBeGreaterThanOrEqualTo(1, "Civil twilight set should exist at Dublin latitude");
        var civilSet = eveningDayStart + civilS![0];

        transform.DateTimeOffset = morningDayStart;
        var (_, civilR, _) = transform.EventTimes(EventType.CivilTwilight);
        civilR.ShouldNotBeNull();
        civilR.Count.ShouldBeGreaterThanOrEqualTo(1, "Civil twilight rise should exist at Dublin latitude");
        var civilRise = morningDayStart + civilR![0];

        // Partial ordering: civil set < astroDark (nautical-derived) and astroTwilight < civil rise
        civilSet.ShouldBeLessThan(astroDark, "Civil set should precede nautical-derived dark boundary");
        astroTwilight.ShouldBeLessThan(civilRise, "Nautical-derived twilight should precede civil rise");

        // Civil rise should be after civil set (dawn after dusk)
        civilRise.ShouldBeGreaterThan(civilSet, "Dawn should be after dusk");
    }

    [Fact]
    public void Schedule_WithCalculatedNightWindow_SchedulesSuccessfully()
    {
        // Use a winter date where M42 (Orion) is well-placed from Vienna
        var winterDate = new DateTimeOffset(2025, 12, 15, 0, 0, 0, TimeSpan.FromHours(1));
        var transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = SiteLatitude,
            SiteLongitude = SiteLongitude,
            SiteElevation = 200,
            SiteTemperature = 5,
            DateTimeOffset = winterDate
        };

        var proposals = new[]
        {
            new ProposedObservation(M42, Priority: ObservationPriority.High),
            new ProposedObservation(M13, Priority: ObservationPriority.Normal),
        };

        // Use the overload that calculates night window internally
        var tree = ObservationScheduler.Schedule(
            proposals,
            transform,
            MinHeight,
            defaultGain: 120,
            defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(30)
        );

        tree.Count.ShouldBeGreaterThanOrEqualTo(1, "At least one target should be schedulable in winter from Vienna");
        // In winter, M42 should be well-placed and scheduled
        tree[0].Target.ShouldBe(M42);
        tree[0].Priority.ShouldBe(ObservationPriority.High);
    }

}
