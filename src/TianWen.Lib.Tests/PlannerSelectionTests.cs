using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for planner list-selection behaviour around pinning
/// (<see cref="PlannerActions.ToggleProposal"/>). The GUI/TUI navigate the pinned-first
/// <see cref="PlannerActions.GetFilteredTargets"/> list, so on a pin the cursor should
/// follow the object into the pinned section at the top (<c>followPinnedSelection: true</c>)
/// instead of being left at the old index where an unrelated target slides under it. The
/// <c>plan</c> CLI navigates the un-reordered <see cref="PlannerState.TonightsBest"/> and
/// keeps the default (no follow).
/// </summary>
public class PlannerSelectionTests
{
    private static readonly DateTimeOffset NightStart = new(2025, 12, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NightEnd = new(2025, 12, 16, 6, 0, 0, TimeSpan.Zero);

    private static Target T(int id) => new(0, 45, $"T{id}", null);

    // Peak at a fraction of the night so GetFilteredTargets' peak-time sort is deterministic.
    private static List<(DateTimeOffset Time, double Alt)> PeakProfile(double peakFraction)
    {
        var nightSeconds = (NightEnd - NightStart).TotalSeconds;
        var peakTime = NightStart + TimeSpan.FromSeconds(nightSeconds * peakFraction);
        return [(NightStart, 0), (peakTime, 80), (NightEnd, 0)];
    }

    /// <summary>Builds a state with <paramref name="targets"/> as unpinned TonightsBest entries.</summary>
    private static PlannerState BuildUnpinnedState(
        IReadOnlyList<(Target Target, List<(DateTimeOffset, double)> Profile)> targets)
    {
        var state = new PlannerState
        {
            AstroDark = NightStart,
            AstroTwilight = NightEnd,
            MinHeightAboveHorizon = 0,
        };

        var scoredBuilder = ImmutableDictionary.CreateBuilder<Target, ScoredTarget>();
        var profilesBuilder = ImmutableDictionary.CreateBuilder<Target, List<(DateTimeOffset Time, double Alt)>>();
        var tonightsBuilder = ImmutableArray.CreateBuilder<ScoredTarget>();

        for (var i = 0; i < targets.Count; i++)
        {
            var (target, profile) = targets[i];
            var optimalStart = NightStart + TimeSpan.FromMinutes(60 * (i + 1));
            var scored = new ScoredTarget(target, (Half)1.0, (Half)1.0,
                new Dictionary<RaDecEventTime, RaDecEventInfo>(),
                OptimalStart: optimalStart, OptimalDuration: TimeSpan.FromHours(1));
            scoredBuilder[target] = scored;
            profilesBuilder[target] = profile;
            tonightsBuilder.Add(scored);
        }

        state.ScoredTargets = scoredBuilder.ToImmutable();
        state.AltitudeProfiles = profilesBuilder.ToImmutable();
        state.TonightsBest = tonightsBuilder.ToImmutable();
        return state;
    }

    [Fact]
    public void GivenUnpinnedTargetWhenPinnedWithFollowThenSelectionMovesToPinnedSlot()
    {
        var a = T(1);
        var b = T(2);
        var c = T(3);
        // Ascending peak times so the unpinned filtered order matches TonightsBest order [a, b, c].
        var state = BuildUnpinnedState([(a, PeakProfile(0.2)), (b, PeakProfile(0.5)), (c, PeakProfile(0.8))]);

        // Cursor on C (last row of the all-unpinned list).
        var before = PlannerActions.GetFilteredTargets(state);
        before[2].Target.ShouldBe(c);
        state.SelectedTargetIndex = 2;

        PlannerActions.ToggleProposal(state, c, followPinnedSelection: true);

        // C is now pinned -> sorted to the top; the cursor followed it there.
        var after = PlannerActions.GetFilteredTargets(state);
        state.PinnedCount.ShouldBe(1);
        state.SelectedTargetIndex.ShouldBe(0);
        after[state.SelectedTargetIndex].Target.ShouldBe(c);
    }

    [Fact]
    public void GivenPinWithoutFollowThenSelectionIndexStaysPut()
    {
        var a = T(1);
        var b = T(2);
        var c = T(3);
        var state = BuildUnpinnedState([(a, PeakProfile(0.2)), (b, PeakProfile(0.5)), (c, PeakProfile(0.8))]);
        state.SelectedTargetIndex = 2;

        // Default (no follow) -- the `plan` CLI path. Index is unchanged, so after the
        // pinned-to-top reorder a *different* target sits under the cursor (legacy behaviour,
        // which is correct for the CLI since it navigates the un-reordered TonightsBest).
        PlannerActions.ToggleProposal(state, c);

        state.SelectedTargetIndex.ShouldBe(2);
        var after = PlannerActions.GetFilteredTargets(state);
        after[2].Target.ShouldNotBe(c);
    }

    [Fact]
    public void GivenUnpinWithFollowThenSelectionUsesClampNotFollow()
    {
        var a = T(1);
        var b = T(2);
        var state = BuildUnpinnedState([(a, PeakProfile(0.3)), (b, PeakProfile(0.7))]);

        // Pin both; filtered pinned order is [a, b] by peak time.
        PlannerActions.ToggleProposal(state, a, followPinnedSelection: true);
        PlannerActions.ToggleProposal(state, b, followPinnedSelection: true);
        state.PinnedCount.ShouldBe(2);
        state.SelectedTargetIndex = 1;

        // Unpin b. followPinnedSelection is ignored on an unpin: the index is in range (1 < 2),
        // so the clamp leaves it put rather than following anything.
        PlannerActions.ToggleProposal(state, b, followPinnedSelection: true);

        state.SelectedTargetIndex.ShouldBe(1);
        var after = PlannerActions.GetFilteredTargets(state);
        state.SelectedTargetIndex.ShouldBeLessThan(after.Count);
    }
}
