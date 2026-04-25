using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="PlannerActions.ApplyHandoffWindows"/> — projecting per-target
/// handoff slider windows + horizon clip into <see cref="ProposedObservation.ObservationTime"/>.
/// </summary>
public class PlannerHandoffWindowTests
{
    private static readonly DateTimeOffset NightStart = new(2025, 12, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NightEnd = new(2025, 12, 16, 6, 0, 0, TimeSpan.Zero); // 12 h dark

    private static Target T(int id, double dec) => new(0, dec, $"T{id}", null);

    /// <summary>
    /// Builds a profile that is constant <paramref name="alt"/> degrees throughout the night.
    /// Two-point profile is enough — the visible-time helper interpolates linearly between samples.
    /// </summary>
    private static List<(DateTimeOffset Time, double Alt)> FlatProfile(double alt)
        => [(NightStart, alt), (NightEnd, alt)];

    /// <summary>
    /// Builds a profile that linearly transitions from <paramref name="startAlt"/> at NightStart
    /// to <paramref name="endAlt"/> at NightEnd. Lets tests synthesize "rising" / "setting"
    /// targets without going through SOFA.
    /// </summary>
    private static List<(DateTimeOffset Time, double Alt)> RampProfile(double startAlt, double endAlt)
        => [(NightStart, startAlt), (NightEnd, endAlt)];

    /// <summary>
    /// Builds a profile with a peak at the requested fraction of the night, climbing from
    /// 0° at NightStart, peaking at 80°, and descending back to 0° at NightEnd. The peak's
    /// time is the value <c>GetFilteredTargets</c> sorts by, so tests can control input order.
    /// </summary>
    private static List<(DateTimeOffset Time, double Alt)> PeakProfile(double peakFraction)
    {
        var nightSeconds = (NightEnd - NightStart).TotalSeconds;
        var peakTime = NightStart + TimeSpan.FromSeconds(nightSeconds * peakFraction);
        return [(NightStart, 0), (peakTime, 80), (NightEnd, 0)];
    }

    private static PlannerState BuildState(
        IReadOnlyList<(Target Target, List<(DateTimeOffset, double)> Profile)> targets,
        IReadOnlyList<DateTimeOffset>? sliders = null,
        byte minAlt = 20)
    {
        var state = new PlannerState
        {
            AstroDark = NightStart,
            AstroTwilight = NightEnd,
            MinHeightAboveHorizon = minAlt,
        };

        // Add targets to ScoredTargets so GetFilteredTargets can find them
        var scoredBuilder = ImmutableDictionary.CreateBuilder<Target, ScoredTarget>();
        var profilesBuilder = ImmutableDictionary.CreateBuilder<Target, List<(DateTimeOffset Time, double Alt)>>();
        var proposalsBuilder = ImmutableArray.CreateBuilder<ProposedObservation>();

        for (var i = 0; i < targets.Count; i++)
        {
            var (target, profile) = targets[i];
            // OptimalStart spread across the night so multi-target ordering is deterministic
            // when test profiles don't have natural peak-time differences.
            var optimalStart = NightStart + TimeSpan.FromMinutes(60 * (i + 1));
            scoredBuilder[target] = new ScoredTarget(target, (Half)1.0, (Half)1.0,
                new Dictionary<TianWen.Lib.Astrometry.SOFA.RaDecEventTime, TianWen.Lib.Astrometry.SOFA.RaDecEventInfo>(),
                OptimalStart: optimalStart, OptimalDuration: TimeSpan.FromHours(1));
            profilesBuilder[target] = profile;
            proposalsBuilder.Add(new ProposedObservation(target));
        }

        state.ScoredTargets = scoredBuilder.ToImmutable();
        state.AltitudeProfiles = profilesBuilder.ToImmutable();
        state.Proposals = proposalsBuilder.ToImmutable();
        if (sliders is not null)
        {
            state.HandoffSliders.AddRange(sliders);
        }
        return state;
    }

    [Fact]
    public void GivenSingleProposalWhenApplyThenWindowIsFullNight()
    {
        var t = T(1, 60);
        var state = BuildState([(t, FlatProfile(60))]);

        var result = PlannerActions.ApplyHandoffWindows(state);

        result.Length.ShouldBe(1);
        result[0].Target.ShouldBe(t);
        result[0].ObservationTime.ShouldNotBeNull();
        result[0].ObservationTime!.Value.ShouldBe(TimeSpan.FromHours(12), tolerance: TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void GivenTwoProposalsWithSliderWhenApplyThenSplitAtSlider()
    {
        var t1 = T(1, 60);
        var t2 = T(2, 50);
        // Peak profiles make the peak-time sort deterministic; minAlt=0 keeps the test
        // focused on slider math (no horizon clipping). t1 peaks at 1/4 of night, t2 at 3/4.
        var slider = NightStart + TimeSpan.FromHours(4);
        var state = BuildState(
            [(t1, PeakProfile(0.25)), (t2, PeakProfile(0.75))],
            sliders: [slider],
            minAlt: 0);

        var result = PlannerActions.ApplyHandoffWindows(state);

        result.Length.ShouldBe(2);
        var byTarget = new Dictionary<Target, ProposedObservation>();
        foreach (var p in result) byTarget[p.Target] = p;

        byTarget[t1].ObservationTime!.Value.ShouldBe(TimeSpan.FromHours(4), tolerance: TimeSpan.FromMinutes(2));
        byTarget[t2].ObservationTime!.Value.ShouldBe(TimeSpan.FromHours(8), tolerance: TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void GivenRisingTargetWhenApplyThenHorizonClipReducesWindow()
    {
        // Target rises from 0° at NightStart to 40° at NightEnd; minAlt=20° → above horizon
        // for the latter half of the night → ~6 h visible inside the 12 h window.
        var t = T(1, 0);
        var state = BuildState([(t, RampProfile(startAlt: 0, endAlt: 40))], minAlt: 20);

        var result = PlannerActions.ApplyHandoffWindows(state);

        result[0].ObservationTime.ShouldNotBeNull();
        // Crosses 20° at 50% of the night → ~6 h visible.
        result[0].ObservationTime!.Value.ShouldBeInRange(
            TimeSpan.FromHours(5), TimeSpan.FromHours(7));
    }

    [Fact]
    public void GivenSettingTargetWhenApplyThenHorizonClipReducesWindow()
    {
        // Target sets from 60° at NightStart to 0° at NightEnd; minAlt=20° → above horizon
        // for the first 2/3 of the night → ~8 h visible.
        var t = T(1, 60);
        var state = BuildState([(t, RampProfile(startAlt: 60, endAlt: 0))], minAlt: 20);

        var result = PlannerActions.ApplyHandoffWindows(state);

        result[0].ObservationTime.ShouldNotBeNull();
        result[0].ObservationTime!.Value.ShouldBeInRange(
            TimeSpan.FromHours(7), TimeSpan.FromHours(9));
    }

    [Fact]
    public void GivenAlwaysBelowHorizonWhenApplyThenObservationTimeStaysNull()
    {
        // Target never reaches 20°; ApplyHandoffWindows produces zero-duration window,
        // which the helper treats as "don't override null". So ObservationTime stays null.
        var t = T(1, 5);
        var state = BuildState([(t, FlatProfile(5))], minAlt: 20);

        var result = PlannerActions.ApplyHandoffWindows(state);

        result[0].ObservationTime.ShouldBeNull();
    }

    [Fact]
    public void GivenUserSetObservationTimeWhenApplyThenPreserved()
    {
        var t = T(1, 60);
        var state = BuildState([(t, FlatProfile(60))]);

        // Explicitly set ObservationTime on the proposal
        var explicitTime = TimeSpan.FromMinutes(45);
        state.Proposals = state.Proposals.SetItem(0,
            state.Proposals[0] with { ObservationTime = explicitTime });

        var result = PlannerActions.ApplyHandoffWindows(state);

        result[0].ObservationTime.ShouldBe(explicitTime);
    }

    [Fact]
    public void GivenMissingProfileWhenComputeVisibleThenReturnsFullWindow()
    {
        var t = T(1, 60);
        var profiles = ImmutableDictionary<Target, List<(DateTimeOffset, double)>>.Empty;

        var visible = PlannerActions.ComputeVisibleTimeInWindow(
            t, NightStart, NightEnd, minAlt: 20, profiles);

        visible.ShouldBe(NightEnd - NightStart);
    }

    [Fact]
    public void GivenEmptyWindowWhenComputeVisibleThenReturnsZero()
    {
        var t = T(1, 60);
        var profiles = ImmutableDictionary<Target, List<(DateTimeOffset, double)>>.Empty
            .Add(t, FlatProfile(60));

        var visible = PlannerActions.ComputeVisibleTimeInWindow(
            t, NightStart, NightStart, minAlt: 20, profiles);

        visible.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void GivenNoPinnedProposalsWhenApplyThenReturnsInputUnchanged()
    {
        var state = new PlannerState
        {
            AstroDark = NightStart,
            AstroTwilight = NightEnd,
        };

        var result = PlannerActions.ApplyHandoffWindows(state);

        result.IsEmpty.ShouldBeTrue();
    }
}
