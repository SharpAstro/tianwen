using DIR.Lib;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The sky map's layer table and the adapter that renders it as DIR.Lib's
    /// <see cref="FloatingPalette"/>.
    ///
    /// <para><b>Scope, deliberately.</b> The palette's own behaviour -- the idle fade, the grip drag,
    /// the double-click collapse, the clamp reconciliation, an unavailable row being drawn and dimmed
    /// -- is pinned in DIR.Lib's <c>FloatingPaletteTests</c>, where it was hoisted after two apps had
    /// each built the same panel. What is left here is what is actually TianWen's: the table, and
    /// whether this map's rows reach the right layers.</para>
    ///
    /// <para>The table is where this can go wrong invisibly: ten entries, each naming a property twice
    /// (a getter and a setter lambda), which is exactly the shape a copy-paste survives. Two entries
    /// pointing at one flag would look right in every screenshot -- the palette would draw ten rows,
    /// nine of which behaved -- so <see cref="EachLayerTogglesItsOwnFlagAndNothingElse"/> snapshots
    /// every flag around each toggle and insists that precisely one moved.</para>
    /// </summary>
    public class SkyMapLayerPaletteTests
    {
        private static bool[] Snapshot(SkyMapState state)
            => [.. SkyMapLayers.All.Select(l => l.IsOn(state))];

        private static SkyMapState StateWithMilkyWay(bool available = true)
            => new SkyMapState { MilkyWayAvailable = available };

        [Fact]
        public void EachLayerTogglesItsOwnFlagAndNothingElse()
        {
            var state = StateWithMilkyWay();

            for (var i = 0; i < SkyMapLayers.All.Length; i++)
            {
                var before = Snapshot(state);
                SkyMapLayers.All[i].Toggle(state).ShouldBeTrue();
                var after = Snapshot(state);

                var moved = Enumerable.Range(0, before.Length).Where(j => before[j] != after[j]).ToArray();
                moved.ShouldBe([i], $"toggling {SkyMapLayers.All[i].Label} moved the wrong flag(s)");
            }
        }

        [Fact]
        public void NoTwoLayersShareAKeyOrALabel()
        {
            SkyMapLayers.All.Select(l => l.Key).Distinct().Count().ShouldBe(SkyMapLayers.All.Length);
            SkyMapLayers.All.Select(l => l.KeyLabel).Distinct().Count().ShouldBe(SkyMapLayers.All.Length);
            SkyMapLayers.All.Select(l => l.Label).Distinct().Count().ShouldBe(SkyMapLayers.All.Length);
        }

        [Fact]
        public void EveryKeyLabelIsTheKeyItActuallyBinds()
        {
            // The palette prints KeyLabel and the handler dispatches on Key, so a mismatch is a panel
            // that teaches the wrong shortcut -- readable to nobody, since both halves work.
            foreach (var layer in SkyMapLayers.All)
            {
                layer.KeyLabel.ShouldBe(layer.Key.ToString(), $"{layer.Label} prints a key it does not bind");
            }
        }

        [Fact]
        public void AKeyNoLayerClaimsIsNotHandled()
            => SkyMapLayers.TryToggleByKey(StateWithMilkyWay(), InputKey.Q).ShouldBeFalse();

        [Fact]
        public void TheMilkyWayKeyIsUnhandledWithNoTexture()
        {
            // The behaviour the old `case InputKey.S when State.MilkyWayAvailable` had: with no texture
            // the press must fall THROUGH, so a host binding S to something else still gets it.
            var state = StateWithMilkyWay(available: false);
            var before = state.ShowMilkyWay;

            SkyMapLayers.TryToggleByKey(state, InputKey.S).ShouldBeFalse();

            state.ShowMilkyWay.ShouldBe(before);
        }

        [Fact]
        public void EveryRowActionResolvesBackToItsLayer()
        {
            // The adapter's whole job: a click arrives as an action string, and a wrong mapping would
            // toggle a different layer than the row that was pressed.
            foreach (var layer in SkyMapLayers.All)
            {
                var resolved = SkyMapLayerPalette.LayerForAction(SkyMapLayerPalette.RowAction(in layer));

                resolved.ShouldNotBeNull();
                resolved.Value.Key.ShouldBe(layer.Key);
            }

            SkyMapLayerPalette.LayerForAction("SkyMapLayer:not-a-key").ShouldBeNull();
        }

        [Fact]
        public void TheItemsMirrorTheTableAndItsAvailability()
        {
            var state = StateWithMilkyWay(available: false);
            state.ShowGrid = true;

            var items = SkyMapLayerPalette.ItemsFor(state);

            items.Length.ShouldBe(SkyMapLayers.All.Length);
            items.Select(i => i.Label).ShouldBe(SkyMapLayers.All.Select(l => l.Label));
            items.Select(i => i.KeyHint).ShouldBe(SkyMapLayers.All.Select(l => (string?)l.KeyLabel));

            items.Single(i => i.KeyHint == "S").IsAvailable.ShouldBeFalse("no texture, no milky way");
            items.Single(i => i.KeyHint == "G").IsOn.ShouldBeTrue();
        }

        [Fact]
        public void ThePaletteBindsTheGripAndOneRowPerAvailableLayer()
        {
            var tree = SkyMapLayerPalette.Build(
                StateWithMilkyWay(available: false), 12f, _ => { }, () => { });

            var actions = ClickActions(tree);

            actions.ShouldContainKey(SkyMapLayerPalette.GripAction);
            foreach (var layer in SkyMapLayers.All)
            {
                var action = SkyMapLayerPalette.RowAction(in layer);
                if (layer.Key == InputKey.S)
                {
                    actions.ShouldNotContainKey(action, "an unavailable layer is drawn but not clickable");
                }
                else
                {
                    actions.ShouldContainKey(action);
                }
            }
        }

        [Fact]
        public void ClickingARowTogglesThatLayerAndNoOther()
        {
            var state = StateWithMilkyWay();
            var toggled = new List<string>();
            var tree = SkyMapLayerPalette.Build(state, 12f, layer =>
            {
                toggled.Add(layer.Label);
                layer.Toggle(state);
            }, () => { });

            var grid = SkyMapLayers.All.Single(l => l.Key == InputKey.G);
            var before = Snapshot(state);

            ClickActions(tree)[SkyMapLayerPalette.RowAction(in grid)].Invoke(InputModifier.None);

            toggled.ShouldBe(["Grid"]);
            var after = Snapshot(state);
            Enumerable.Range(0, before.Length).Where(j => before[j] != after[j])
                .ShouldBe([SkyMapLayers.All.IndexOf(grid)]);
        }

        [Fact]
        public void PressingTheGripReachesTheHost()
        {
            // The drag's entry point: a host dispatches clickable regions from the mouse-DOWN and
            // forwards the press to the tab only when nothing was hit, so this binding is the only way
            // a grip drag can begin at all.
            var pressed = 0;
            var tree = SkyMapLayerPalette.Build(StateWithMilkyWay(), 12f, _ => { }, () => pressed++);

            ClickActions(tree)[SkyMapLayerPalette.GripAction].Invoke(InputModifier.None);

            pressed.ShouldBe(1);
        }

        private static Dictionary<string, Action<InputModifier>> ClickActions(Layout.Node root)
        {
            var found = new Dictionary<string, Action<InputModifier>>();
            Walk(root, n =>
            {
                if (n is { Hit: HitResult.ButtonHit button, OnClick: { } click })
                {
                    found[button.Action] = click;
                }
            });
            return found;
        }

        private static void Walk(Layout.Node node, Action<Layout.Node> visit)
        {
            visit(node);
            switch (node)
            {
                case Layout.Node.Stack stack:
                    foreach (var child in stack.Children) Walk(child, visit);
                    break;
                case Layout.Node.Anchored anchored:
                    Walk(anchored.Child, visit);
                    break;
            }
        }
    }
}
