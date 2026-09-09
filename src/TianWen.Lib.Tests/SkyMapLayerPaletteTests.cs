using DIR.Lib;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The sky map's layer table and the palette that renders it.
    ///
    /// <para>The table is where this feature can go wrong invisibly: ten entries, each naming a
    /// property twice (a getter and a setter lambda), which is exactly the shape a copy-paste survives.
    /// Two entries pointing at one flag would look right in every screenshot -- the palette would draw
    /// ten rows, nine of which behaved -- so <see cref="EachLayerTogglesItsOwnFlagAndNothingElse"/>
    /// snapshots every flag around each toggle and insists that precisely one moved.</para>
    /// </summary>
    public class SkyMapLayerPaletteTests
    {
        /// <summary>
        /// A measure context with no glyphs behind it: text measures at a fixed cell per character, and
        /// a design unit is a pixel. Enough for the engine, which is the point of it being an interface
        /// (<c>Layout.Engine</c> geometry is headless-testable by design).
        /// </summary>
        private sealed class StubMeasure : Layout.IMeasureContext<float>
        {
            public Layout.Size<float> MeasureText(ReadOnlySpan<char> text, float fontSize)
                => new Layout.Size<float>(text.Length * fontSize * 0.6f, fontSize * 1.2f);

            public float ToSurface(float designUnits) => designUnits;
        }

        /// <summary>Reads every layer's flag in table order, so two snapshots can be compared.</summary>
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
        {
            SkyMapLayers.TryToggleByKey(StateWithMilkyWay(), InputKey.Q).ShouldBeFalse();
        }

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
        public void ThePaletteBindsOneClickableRowPerAvailableLayer()
        {
            var state = StateWithMilkyWay();
            var tree = SkyMapLayerPalette.Build(state, 12f, _ => { });

            var actions = ClickActions(tree);

            actions.Count.ShouldBe(SkyMapLayers.All.Length);
            foreach (var layer in SkyMapLayers.All)
            {
                actions.ShouldContainKey(SkyMapLayerPalette.RowAction(in layer));
            }
        }

        [Fact]
        public void AnUnavailableLayerIsDrawnButNotClickable()
        {
            // Shown and dimmed rather than dropped: a row that comes and goes moves every row under it,
            // and an absent row says nothing about WHY the milky way is missing.
            var state = StateWithMilkyWay(available: false);
            var milkyWay = SkyMapLayers.All.Single(l => l.Key == InputKey.S);

            var tree = SkyMapLayerPalette.Build(state, 12f, _ => { });

            ClickActions(tree).ShouldNotContainKey(SkyMapLayerPalette.RowAction(in milkyWay));
            TextRuns(tree).ShouldContain(milkyWay.Label);
        }

        [Fact]
        public void ClickingARowTogglesThatLayer()
        {
            var state = StateWithMilkyWay();
            var toggled = new List<string>();
            var tree = SkyMapLayerPalette.Build(state, 12f, layer =>
            {
                toggled.Add(layer.Label);
                layer.Toggle(state);
            });

            var grid = SkyMapLayers.All.Single(l => l.Key == InputKey.G);
            var before = state.ShowGrid;

            ClickActions(tree)[SkyMapLayerPalette.RowAction(in grid)].Invoke(InputModifier.None);

            toggled.ShouldBe(["Grid"]);
            state.ShowGrid.ShouldBe(!before);
        }

        [Fact]
        public void ThePaletteArrangesInsideTheContentRectAgainstItsRightEdge()
        {
            // The whole reason this goes through Layout.Builder.Anchored rather than hand-placed
            // coordinates: the pinning and the clamp are the engine's, so the panel cannot be arranged
            // off the edge of the map.
            var state = StateWithMilkyWay();
            var bounds = new Rect<float>(40f, 20f, 900f, 600f);

            var arranged = Layout.Engine.Arrange(
                SkyMapLayerPalette.Build(state, 12f, _ => { }), bounds, new StubMeasure());

            var panel = arranged.First(a => a.Node is Layout.Node.Stack { Axis: Layout.Axis.Vertical });

            panel.Bounds.Width.ShouldBe(SkyMapLayerPalette.PanelWidth, 0.01f);
            panel.Bounds.X.ShouldBe(
                bounds.X + bounds.Width - SkyMapLayerPalette.PanelWidth - SkyMapLayerPalette.Margin, 0.01f);
            (panel.Bounds.Y + panel.Bounds.Height).ShouldBeLessThanOrEqualTo(bounds.Y + bounds.Height);
            panel.Bounds.Y.ShouldBeGreaterThanOrEqualTo(bounds.Y);
        }

        [Fact]
        public void ANarrowMapStillPlacesTheWholePanelInside()
        {
            // A pane narrower than the panel plus its margins is where a hand-rolled "right edge minus
            // width" leaves the thing hanging off the left. The engine's clamp is what stops that.
            var state = StateWithMilkyWay();
            var bounds = new Rect<float>(0f, 0f, 100f, 400f);

            var arranged = Layout.Engine.Arrange(
                SkyMapLayerPalette.Build(state, 12f, _ => { }), bounds, new StubMeasure());
            var panel = arranged.First(a => a.Node is Layout.Node.Stack { Axis: Layout.Axis.Vertical });

            panel.Bounds.X.ShouldBeGreaterThanOrEqualTo(bounds.X);
        }

        /// <summary>Every bound click in the tree, keyed by its <see cref="HitResult.ButtonHit"/> action.</summary>
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

        /// <summary>Every text run in the tree, so a test can assert a row is DRAWN and not merely bound.</summary>
        private static List<string> TextRuns(Layout.Node root)
        {
            var found = new List<string>();
            Walk(root, n =>
            {
                if (n is Layout.Node.Leaf { Content: Layout.Content.Text text })
                {
                    found.Add(text.Value);
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
