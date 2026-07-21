using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Surface-side wiring tests for the Planner target list after its migration onto the DIR.Lib
    /// <c>ListScrollController</c> (atom model). The atom math itself is exhaustively pinned in DIR.Lib's
    /// <c>ListScrollControllerTests</c>; these prove <see cref="PlannerTab{TSurface}"/> is wired to it
    /// correctly over the CPU <see cref="RgbaImageRenderer"/> -- input is fed exactly as the GUI/web hosts
    /// do it (HitTestAndDispatch first, then HandleInput on a miss), so tap-on-release selection, drag
    /// scrolling, sub-unit wheel accumulation, and pin-button coexistence are exercised end-to-end.
    /// </summary>
    [Collection("UI")]
    public class PlannerTabScrollTests
    {
        private static readonly DateTimeOffset NightStart = new(2025, 12, 15, 18, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset NightEnd = new(2025, 12, 16, 6, 0, 0, TimeSpan.Zero);

        /// <summary>A planner state with <paramref name="count"/> selectable, unpinned targets.</summary>
        private static PlannerState BuildState(int count)
        {
            var state = new PlannerState
            {
                AstroDark = NightStart,
                AstroTwilight = NightEnd,
                MinHeightAboveHorizon = 0,
                SelectedTargetIndex = 0,
            };

            var scored = ImmutableDictionary.CreateBuilder<Target, ScoredTarget>();
            var profiles = ImmutableDictionary.CreateBuilder<Target, List<(DateTimeOffset Time, double Alt)>>();
            var tonights = ImmutableArray.CreateBuilder<ScoredTarget>();
            for (var i = 0; i < count; i++)
            {
                var target = new Target((i * 0.5) % 24.0, 30 + i % 30, $"T{i}", null);
                var st = new ScoredTarget(target, (Half)1.0, (Half)1.0,
                    new Dictionary<RaDecEventTime, RaDecEventInfo>(),
                    OptimalStart: NightStart + TimeSpan.FromHours(1), OptimalDuration: TimeSpan.FromHours(1),
                    OptimalAltitude: 60.0);
                scored[target] = st;
                profiles[target] = [(NightStart, 0), (NightStart + TimeSpan.FromHours(3), 80), (NightEnd, 0)];
                tonights.Add(st);
            }

            state.ScoredTargets = scored.ToImmutable();
            state.AltitudeProfiles = profiles.ToImmutable();
            state.TonightsBest = tonights.ToImmutable();
            return state;
        }

        private static void RenderInto(PlannerTab<RgbaImage> tab, RgbaImageRenderer r, PlannerState state)
        {
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero));
            tab.Render(state, new RectF32(0, 0, r.Width, r.Height), 1f, FontResolver.ResolveSystemFont(), time);
        }

        // Mirror the GUI/web host: on a press, dispatch registered clickables first; only forward the
        // raw press to HandleInput when nothing claimed it. Move/Up always go straight to HandleInput.
        private static void HostPress(PlannerTab<RgbaImage> tab, float x, float y)
        {
            if (tab.HitTestAndDispatch(x, y) is null)
            {
                tab.HandleInput(new InputEvent.MouseDown(x, y));
            }
        }

        // A point on the top visible row's body (just left of its pin button), derived from the registered
        // pin-button region so no private geometry is needed.
        private static (float X, float Y) TopRowBodyPoint(PlannerTab<RgbaImage> tab)
        {
            var pin = tab.GetRegisteredRegions()
                .Where(r => r.Result is HitResult.ButtonHit { Action: "AddProposal" or "RemoveProposal" })
                .OrderBy(r => r.Y)
                .First();
            return (pin.X - 5f, pin.Y + pin.Height / 2f);
        }

        [Fact]
        public void Wheel_SubUnitDeltas_Accumulate_ScrollTheList()
        {
            // A 400px-tall window shows ~15 rows of 40 -> the list overflows and can scroll.
            using var r = new RgbaImageRenderer(1600, 400);
            var state = BuildState(40);
            var tab = new PlannerTab<RgbaImage>(r);
            RenderInto(tab, r, state);
            tab.ScrollOffset.ShouldBe(0);

            var cx = tab.TargetListRect.X + tab.TargetListRect.Width * 0.5f;
            var cy = tab.TargetListRect.Y + tab.TargetListRect.Height * 0.5f;
            // Three sub-unit (|delta| < 1) wheel events, the precision-trackpad / browser-bridge shape the
            // old (int)scrollY*step truncated to zero. They must accumulate into a real scroll.
            for (var i = 0; i < 3; i++)
            {
                tab.HandleInput(new InputEvent.Scroll(-0.3f, cx, cy));
            }

            tab.ScrollOffset.ShouldBeGreaterThan(0);
        }

        [Fact]
        public void Tap_SelectsRowUnderPress_WithoutScrolling()
        {
            using var r = new RgbaImageRenderer(1600, 400);
            var state = BuildState(40);
            var tab = new PlannerTab<RgbaImage>(r);
            RenderInto(tab, r, state);

            // Scroll down a notch first, then re-render so the regions reflect the new offset. The top
            // visible row is now index == ScrollOffset, proving the tap maps to the actual row, not slot 0.
            var cx = tab.TargetListRect.X + tab.TargetListRect.Width * 0.5f;
            var cy = tab.TargetListRect.Y + tab.TargetListRect.Height * 0.5f;
            tab.HandleInput(new InputEvent.Scroll(-1f, cx, cy));
            RenderInto(tab, r, state);
            var offset = tab.ScrollOffset;
            offset.ShouldBeGreaterThan(0);

            var (px, py) = TopRowBodyPoint(tab);
            HostPress(tab, px, py);                              // unclaimed body press -> controller arms
            tab.HandleInput(new InputEvent.MouseUp(px, py));     // release without moving -> tap

            state.SelectedTargetIndex.ShouldBe(offset);          // selected the tapped (top visible) row
            tab.ScrollOffset.ShouldBe(offset);                   // a tap never scrolls
        }

        [Fact]
        public void Drag_ScrollsList_WithoutSelecting()
        {
            using var r = new RgbaImageRenderer(1600, 400);
            var state = BuildState(40);
            var tab = new PlannerTab<RgbaImage>(r);
            RenderInto(tab, r, state);
            state.SelectedTargetIndex = 0;

            var (px, py) = TopRowBodyPoint(tab);
            HostPress(tab, px, py);                                    // arm
            tab.HandleInput(new InputEvent.MouseMove(px, py - 132f));  // drag up well past the 4px slop
            tab.HandleInput(new InputEvent.MouseUp(px, py - 132f));

            tab.ScrollOffset.ShouldBeGreaterThan(0);  // dragging up revealed later rows
            state.SelectedTargetIndex.ShouldBe(0);    // a drag never selects
        }

        [Fact]
        public void RowBodyIsUnclaimed_ButPinButtonStaysRegistered()
        {
            // The structural change behind tap-on-release: the row-select ListItemHit is gone (so a body
            // press falls through to the controller), while the per-row pin button stays a clickable that
            // HitTestAndDispatch claims first.
            using var r = new RgbaImageRenderer(1600, 400);
            var state = BuildState(40);
            var tab = new PlannerTab<RgbaImage>(r);
            RenderInto(tab, r, state);

            var regions = tab.GetRegisteredRegions();
            regions.Any(x => x.Result is HitResult.ListItemHit { ListId: "TargetList" }).ShouldBeFalse();
            regions.Any(x => x.Result is HitResult.ButtonHit { Action: "AddProposal" }).ShouldBeTrue();
        }
    }
}
