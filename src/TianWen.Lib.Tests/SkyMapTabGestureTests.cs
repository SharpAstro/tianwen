using System.Collections.Generic;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the sky map's click-vs-drag classification after its migration onto the DIR.Lib
    /// <c>TapOrDragGesture</c> (the slop/latch math itself is pinned in DIR.Lib's
    /// <c>TapOrDragGestureTests</c>; these prove <see cref="SkyMapTab{TSurface}"/> is wired to it
    /// correctly): a within-slop press-release emits <see cref="SkyMapClickSelectSignal"/> with the
    /// press modifiers, a pan does not, a pan that wanders back over its start stays a drag (the
    /// latch -- the old total-displacement check misclassified this as a click), and a release whose
    /// press never reached the map (sidebar click, chrome-forwarded MouseUp) is ignored.
    /// </summary>
    [Collection("UI")]
    public class SkyMapTabGestureTests
    {
        private static (SkyMapTab<RgbaImage> Tab, SignalBus Bus, List<SkyMapClickSelectSignal> Clicks) BuildTab()
        {
            var bus = new SignalBus();
            var clicks = new List<SkyMapClickSelectSignal>();
            bus.Subscribe<SkyMapClickSelectSignal>(clicks.Add);
            var renderer = new RgbaImageRenderer(320, 240);
            var tab = new SkyMapTab<RgbaImage>(renderer) { Bus = bus };
            return (tab, bus, clicks);
        }

        [Fact]
        public void PressReleaseWithinSlop_EmitsClickSelect()
        {
            var (tab, bus, clicks) = BuildTab();

            tab.HandleInput(new InputEvent.MouseDown(100f, 100f));
            tab.HandleInput(new InputEvent.MouseUp(101f, 101f));
            bus.ProcessPending(); // Post queues; handlers run on the drain (the hosts' per-frame pump)

            var click = clicks.ShouldHaveSingleItem();
            click.ScreenX.ShouldBe(101f);
            click.ScreenY.ShouldBe(101f);
            click.Modifiers.ShouldBe(InputModifier.None);
        }

        [Fact]
        public void PressModifiers_RideTheGestureToTheRelease()
        {
            var (tab, bus, clicks) = BuildTab();

            // MouseUp carries no modifiers; the click-select must replay the press's.
            tab.HandleInput(new InputEvent.MouseDown(100f, 100f, MouseButton.Left, InputModifier.Ctrl));
            tab.HandleInput(new InputEvent.MouseUp(100f, 100f));
            bus.ProcessPending(); // Post queues; handlers run on the drain (the hosts' per-frame pump)

            clicks.ShouldHaveSingleItem().Modifiers.ShouldBe(InputModifier.Ctrl);
        }

        [Fact]
        public void DragPan_DoesNotEmitClickSelect()
        {
            var (tab, bus, clicks) = BuildTab();

            tab.HandleInput(new InputEvent.MouseDown(100f, 100f));
            tab.HandleInput(new InputEvent.MouseMove(160f, 160f));
            tab.HandleInput(new InputEvent.MouseUp(160f, 160f));
            bus.ProcessPending(); // Post queues; handlers run on the drain (the hosts' per-frame pump)

            clicks.ShouldBeEmpty();
            tab.State.IsDragging.ShouldBeFalse(); // drag ended cleanly
        }

        [Fact]
        public void DragThatWandersBackOverItsStart_StaysADrag()
        {
            var (tab, bus, clicks) = BuildTab();

            // The gesture latches once the slop is exceeded: returning to the press position must
            // NOT reclassify the pan as a click (the pre-gesture code compared only down-vs-up
            // distance, so this exact sequence used to fire a spurious select).
            tab.HandleInput(new InputEvent.MouseDown(100f, 100f));
            tab.HandleInput(new InputEvent.MouseMove(160f, 160f));
            tab.HandleInput(new InputEvent.MouseMove(101f, 101f));
            tab.HandleInput(new InputEvent.MouseUp(101f, 101f));
            bus.ProcessPending(); // Post queues; handlers run on the drain (the hosts' per-frame pump)

            clicks.ShouldBeEmpty();
        }

        [Fact]
        public void ReleaseWithoutAPressOnTheMap_IsIgnored()
        {
            var (tab, bus, clicks) = BuildTab();

            // A sidebar click is consumed by chrome, but the host still forwards the MouseUp to the
            // now-active tab -- an idle (never-armed) gesture must not click-select.
            tab.HandleInput(new InputEvent.MouseUp(50f, 50f));
            bus.ProcessPending(); // Post queues; handlers run on the drain (the hosts' per-frame pump)

            clicks.ShouldBeEmpty();
        }
    }
}
