using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public sealed class MosaicSchedulingTests
{
    // Vienna, Austria: ~48.2°N, ~16.4°E
    private const double SiteLatitude = 48.2;
    private const double SiteLongitude = 16.4;
    private const byte MinHeight = 20;

    // Winter night from Vienna (long night, good visibility for M31/M42)
    private static readonly DateTimeOffset AstroDark = new DateTimeOffset(2025, 12, 15, 17, 30, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset AstroTwilight = new DateTimeOffset(2025, 12, 16, 6, 30, 0, TimeSpan.FromHours(1));

    private static Transform CreateTransform()
    {
        var transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = SiteLatitude,
            SiteLongitude = SiteLongitude,
            SiteElevation = 200,
            SiteTemperature = 5,
            DateTimeOffset = AstroDark
        };
        return transform;
    }

    private static readonly Target M42 = new Target(5.588, -5.39, "M42", null);

    [Fact]
    public void Schedule_MosaicGroup_AllocatedContiguously()
    {
        var transform = CreateTransform();
        var mosaicId = Guid.NewGuid();

        // Create a 2×2 mosaic grid manually (simulating MosaicGenerator output)
        var panels = new[]
        {
            new ProposedObservation(new Target(0.60, 41.0, "M31_R0C0", null), MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
            new ProposedObservation(new Target(0.65, 41.5, "M31_R1C0", null), MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
            new ProposedObservation(new Target(0.80, 41.0, "M31_R0C1", null), MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
            new ProposedObservation(new Target(0.85, 41.5, "M31_R1C1", null), MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
        };

        var tree = ObservationScheduler.Schedule(
            panels,
            transform,
            AstroDark,
            AstroTwilight,
            MinHeight,
            defaultGain: 120,
            defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(30)
        );

        tree.Count.ShouldBe(4, "All 4 mosaic panels should be scheduled");

        // Panels should be contiguous in time (each starts when the previous ends)
        for (var i = 1; i < tree.Count; i++)
        {
            tree[i].Start.ShouldBe(tree[i - 1].Start + tree[i - 1].Duration,
                $"Panel {i} should start immediately after panel {i - 1}");
        }
    }

    [Fact]
    public void Schedule_MosaicGroup_OrderedByRAAscending()
    {
        var transform = CreateTransform();
        var mosaicId = Guid.NewGuid();

        // Panels at different RAs: should be ordered by RA ascending
        var panels = new[]
        {
            new ProposedObservation(new Target(0.90, 41.0, "P_HighRA", null), MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
            new ProposedObservation(new Target(0.50, 41.0, "P_LowRA", null), MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
            new ProposedObservation(new Target(0.70, 41.0, "P_MidRA", null), MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
        };

        var tree = ObservationScheduler.Schedule(
            panels,
            transform,
            AstroDark,
            AstroTwilight,
            MinHeight,
            defaultGain: 0,
            defaultOffset: 0,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(30)
        );

        tree.Count.ShouldBe(3);
        // Should be ordered by RA ascending: LowRA, MidRA, HighRA
        tree[0].Target.Name.ShouldBe("P_LowRA");
        tree[1].Target.Name.ShouldBe("P_MidRA");
        tree[2].Target.Name.ShouldBe("P_HighRA");
    }

    [Fact]
    public void Schedule_MosaicGroupWithIndividual_BothScheduled()
    {
        var transform = CreateTransform();
        var mosaicId = Guid.NewGuid();

        var proposals = new[]
        {
            // Individual target
            new ProposedObservation(M42, Priority: ObservationPriority.High,
                ObservationTime: TimeSpan.FromMinutes(30)),
            // Mosaic group
            new ProposedObservation(new Target(0.60, 41.0, "M31_P0", null), MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
            new ProposedObservation(new Target(0.80, 41.0, "M31_P1", null), MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
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

        tree.Count.ShouldBe(3, "M42 + 2 mosaic panels should all be scheduled");

        // M42 is High priority, should be scheduled first
        tree[0].Target.ShouldBe(M42);
    }

    /// <summary>
    /// Where a slot lies against its own target's meridian, read from the sidereal time at its two ends: a test's own
    /// arithmetic, so it does not take the scheduler's word for the flag it is checking.
    /// </summary>
    private static MeridianSide SideOf(ScheduledObservation observation)
    {
        var startHa = CoordinateUtils.ConditionHA(
            Transform.CalculateLocalSiderealTime(observation.Start.UtcDateTime, SiteLongitude) - observation.Target.RA);
        var endHa = startHa + observation.Duration.TotalHours * 1.00273790935;
        return endHa <= 0 ? MeridianSide.East : startHa >= 0 ? MeridianSide.West : MeridianSide.Straddles;
    }

    private enum MeridianSide { East, West, Straddles }

    private static ScheduledObservationTree ScheduleMosaic(params ProposedObservation[] panels)
        => ObservationScheduler.Schedule(
            panels,
            CreateTransform(),
            AstroDark,
            AstroTwilight,
            MinHeight,
            defaultGain: 0,
            defaultOffset: 0,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(30));

    [Fact]
    public void Schedule_MosaicGroup_IsPlacedWhollyOnOneSideOfTheMeridian()
    {
        // A strip transiting early in the night, whose 45-minute panels outrun their 12-minute RA spacing: placed from
        // the start of its window, as it once was, the second panel straddles its transit and flips mid-panel (#1501).
        var mosaicId = Guid.NewGuid();
        var tree = ScheduleMosaic(
            new ProposedObservation(new Target(0.40, 41.0, "P0", null), MosaicGroupId: mosaicId, ObservationTime: TimeSpan.FromMinutes(45)),
            new ProposedObservation(new Target(0.60, 41.0, "P1", null), MosaicGroupId: mosaicId, ObservationTime: TimeSpan.FromMinutes(45)),
            new ProposedObservation(new Target(0.80, 41.0, "P2", null), MosaicGroupId: mosaicId, ObservationTime: TimeSpan.FromMinutes(45)),
            new ProposedObservation(new Target(1.00, 41.0, "P3", null), MosaicGroupId: mosaicId, ObservationTime: TimeSpan.FromMinutes(45)));

        tree.Count.ShouldBe(4);
        var sides = Enumerable.Range(0, tree.Count).Select(i => SideOf(tree[i])).ToArray();
        sides.ShouldNotContain(MeridianSide.Straddles, $"no panel may straddle its transit: {string.Join(", ", sides)}");
        sides.Distinct().Count().ShouldBe(1, $"the whole mosaic is on one side, so no pier change within it: {string.Join(", ", sides)}");
        for (var i = 0; i < tree.Count; i++)
        {
            tree[i].AcrossMeridian.ShouldBeFalse($"panel {i} is flagged as crossing");
        }
    }

    [Fact]
    public void Schedule_MosaicGroup_OrdersWestFirstAcrossRaZero()
    {
        // A strip through Cassiopeia crosses RA 0h; a plain RA sort would put 0.0h first and 23.6h last, so the
        // westmost panel, the first to reach the meridian, would be imaged last.
        var mosaicId = Guid.NewGuid();
        var tree = ScheduleMosaic(
            new ProposedObservation(new Target(0.0, 60.0, "C", null), MosaicGroupId: mosaicId),
            new ProposedObservation(new Target(23.8, 60.0, "B", null), MosaicGroupId: mosaicId),
            new ProposedObservation(new Target(0.2, 60.0, "D", null), MosaicGroupId: mosaicId),
            new ProposedObservation(new Target(23.6, 60.0, "A", null), MosaicGroupId: mosaicId));

        tree.Count.ShouldBe(4);
        string.Concat(Enumerable.Range(0, tree.Count).Select(i => tree[i].Target.Name)).ShouldBe("ABCD");
    }

    [Fact]
    public void Schedule_MosaicGroup_ThatCannotAvoidTheMeridianIsStillScheduledAndFlaggedTruly()
    {
        // Twelve hours of M42 panels: longer than it stays above the minimum height, so no placement keeps it off the
        // meridian. It is still scheduled, and each panel's flag says what its slot does.
        var mosaicId = Guid.NewGuid();
        var panels = Enumerable.Range(0, 6)
            .Select(i => new ProposedObservation(new Target(5.50 + 0.05 * i, -5.4, $"M42_{i}", null),
                MosaicGroupId: mosaicId, ObservationTime: TimeSpan.FromHours(2)))
            .ToArray();

        var tree = ScheduleMosaic(panels);

        tree.Count.ShouldBe(6);
        for (var i = 0; i < tree.Count; i++)
        {
            tree[i].AcrossMeridian.ShouldBe(SideOf(tree[i]) is MeridianSide.Straddles, $"panel {i}'s flag disagrees with its slot");
        }
    }

    [Fact]
    public void Schedule_MosaicGroup_DefaultsResolvedPerPanel()
    {
        var transform = CreateTransform();
        var mosaicId = Guid.NewGuid();

        var panels = new[]
        {
            new ProposedObservation(new Target(0.60, 41.0, "P0", null), Gain: 200, MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
            new ProposedObservation(new Target(0.80, 41.0, "P1", null), MosaicGroupId: mosaicId,
                ObservationTime: TimeSpan.FromMinutes(30)),
        };

        var tree = ObservationScheduler.Schedule(
            panels,
            transform,
            AstroDark,
            AstroTwilight,
            MinHeight,
            defaultGain: 120,
            defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(30)
        );

        tree.Count.ShouldBe(2);
        // P0 has explicit gain=200, P1 should use the group representative's gain (200)
        tree[0].Gain.ShouldBe(200);
    }

    [Fact]
    public void Schedule_EmptyMosaicGroup_NoEffect()
    {
        var transform = CreateTransform();

        // No mosaic panels, just a regular proposal
        var proposals = new[]
        {
            new ProposedObservation(M42, ObservationTime: TimeSpan.FromMinutes(30))
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

        tree.Count.ShouldBe(1);
        tree[0].Target.ShouldBe(M42);
        tree[0].Target.Name.ShouldBe("M42");
    }

    [Fact]
    public void MosaicEndToEnd_GenerateThenSchedule()
    {
        var transform = CreateTransform();

        // Generate mosaic panels for a large object
        var panels = MosaicGenerator.GeneratePanels(
            centerRA: 5.588, centerDec: -5.39,  // M42 area
            majorAxisArcmin: 85, minorAxisArcmin: 60, positionAngleDeg: 0,
            fovWidthDeg: 0.5, fovHeightDeg: 0.5);

        panels.Length.ShouldBeGreaterThan(1, "Should generate multiple panels");

        // Convert to proposals
        var mosaicId = Guid.NewGuid();
        var proposals = panels.Select(p => new ProposedObservation(
            p.Target,
            MosaicGroupId: mosaicId,
            ObservationTime: TimeSpan.FromMinutes(20)
        )).ToArray();

        var tree = ObservationScheduler.Schedule(
            proposals,
            transform,
            AstroDark,
            AstroTwilight,
            MinHeight,
            defaultGain: 120,
            defaultOffset: 10,
            defaultSubExposure: TimeSpan.FromSeconds(120),
            defaultObservationTime: TimeSpan.FromMinutes(20)
        );

        tree.Count.ShouldBe(panels.Length, "All panels should be scheduled");

        // Verify panels are ordered by RA ascending
        for (var i = 1; i < tree.Count; i++)
        {
            tree[i].Target.RA.ShouldBeGreaterThanOrEqualTo(tree[i - 1].Target.RA,
                $"Panel {i} should have RA >= panel {i - 1}");
        }

        // Verify contiguous scheduling
        for (var i = 1; i < tree.Count; i++)
        {
            tree[i].Start.ShouldBe(tree[i - 1].Start + tree[i - 1].Duration,
                $"Panel {i} should start immediately after panel {i - 1}");
        }
    }
}
