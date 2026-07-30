using System;
using System.Collections.Immutable;
using System.Linq;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using TianWen.Cli.Tui;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the OTA header's delete affordance. The row is a layout tree, so the region a click resolves
    /// against IS the rect the glyphs were painted into -- these tests assert the tree, and the list's own
    /// geometry (viewport origin, header row, scroll offset, scrollbar column) is pinned once in
    /// Console.Lib rather than re-asserted per consumer.
    /// <para>
    /// The defect being fixed: the [X] was drawn but never bound to a hit, so it looked clickable and was
    /// not -- and the <c>X</c> key that actually removed an OTA was absent from the status bar. The user's
    /// only discoverable route to removing an accidentally-added OTA therefore did nothing.
    /// </para>
    /// </summary>
    public class TuiEquipmentRowTests
    {
        /// <summary>Rows are authored in cells, which is what <see cref="ScrollableList{T}"/> arranges them in.</summary>
        private static ImmutableArray<Layout.ArrangedNode<int>> Arrange(EquipmentFieldItem item, int width)
            => Layout.Engine.Arrange(
                item.BuildRow(RowContext.Single(selected: false)),
                new Rect<int>(0, 0, width, 1),
                CellMeasureContext.CellAuthored);

        /// <summary>An OTA header with its delete action bound, which is how the tab builds one.</summary>
        private static EquipmentFieldItem OtaHeader(string name, int otaIndex = 0, Action<InputModifier>? onRemove = null)
            => new EquipmentFieldItem
            {
                SectionName = name,
                IsOtaHeader = true,
                OtaIndex = otaIndex,
                OnRemoveOta = onRemove ?? (_ => { }),
            };

        /// <summary>An OTA header with NO handler -- the state the row used to ship in unconditionally.</summary>
        private static EquipmentFieldItem UnboundOtaHeader(string name) => new EquipmentFieldItem
        {
            SectionName = name,
            IsOtaHeader = true,
            OtaIndex = 0,
            OnRemoveOta = null,
        };

        /// <summary>The arranged node carrying the delete action's hit, or null when nothing claims one.</summary>
        private static Layout.ArrangedNode<int>? DeleteAction(ImmutableArray<Layout.ArrangedNode<int>> arranged, int otaIndex = 0)
        {
            foreach (var node in arranged)
            {
                if (node.Node.Hit is HitResult.ButtonHit { Action: var action } && action == $"RemoveOta{otaIndex}")
                {
                    return node;
                }
            }
            return null;
        }

        /// <summary>Whether the delete GLYPH was drawn at all, regardless of whether it carries a hit.</summary>
        private static bool DrawsDeleteGlyph(ImmutableArray<Layout.ArrangedNode<int>> arranged)
        {
            foreach (var node in arranged)
            {
                if (node.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: EquipmentFieldItem.DeleteActionLabel } })
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The action sits at the row's right edge, whatever that edge is. This is what the string version
        /// could not do: it had no way to know its usable width (the list yields a column to the scrollbar
        /// once it overflows), so the glyphs were pinned one space after the title and the columns they
        /// occupied had to be published as a static for the click region to be registered over.
        /// </summary>
        [Theory]
        [InlineData("OTA 1", 60)]
        [InlineData("OTA 12", 40)]
        [InlineData("Telescope with a rather long name", 60)]
        [InlineData("OTA 1", 19)]   // the width a 20-column list has once a scrollbar appears
        public void TheDeleteActionIsRightAnchoredAtWhateverWidthTheRowGot(string name, int width)
        {
            var action = DeleteAction(Arrange(OtaHeader(name), width));

            action.ShouldNotBeNull();
            action.Value.Bounds.Width.ShouldBe(EquipmentFieldItem.DeleteActionLabel.Length);
            action.Value.Bounds.X.ShouldBe(width - EquipmentFieldItem.DeleteActionLabel.Length);
        }

        [Fact]
        public void ANonOtaSectionHeaderCarriesNoDeleteAction()
        {
            var arranged = Arrange(new EquipmentFieldItem { SectionName = "Site" }, 60);

            DeleteAction(arranged).ShouldBeNull();
            DrawsDeleteGlyph(arranged).ShouldBeFalse();
        }

        /// <summary>
        /// The header names the OTA it deletes, so two OTA headers in one list cannot both claim the same
        /// hit -- which is the failure the shared static invited, since it keyed only on the section text.
        /// </summary>
        [Fact]
        public void EachOtaHeaderClaimsItsOwnDeleteAction()
        {
            var arranged = Arrange(OtaHeader("Telescope #2", otaIndex: 1), 60);

            DeleteAction(arranged, otaIndex: 1).ShouldNotBeNull();
            DeleteAction(arranged, otaIndex: 0).ShouldBeNull();
        }

        /// <summary>
        /// A click inside the action's own rect runs the row's handler -- draw and hit are the same rect, so
        /// there is no second arithmetic to agree with.
        /// </summary>
        [Fact]
        public void ClickingTheDeleteActionRunsTheRowsHandler()
        {
            var removed = 0;
            var arranged = Arrange(OtaHeader("OTA 1", onRemove: _ => removed++), 60);
            var bounds = DeleteAction(arranged).ShouldNotBeNull().Bounds;

            CellLayout.HitTest(arranged, bounds.X, bounds.Y).ShouldNotBeNull();
            removed.ShouldBe(1);

            // One column left of the action is the title, which is not clickable.
            CellLayout.HitTest(arranged, bounds.X - 1, bounds.Y).ShouldBeNull();
            removed.ShouldBe(1);
        }

        /// <summary>
        /// With no handler the glyph carries no hit at all. That is the state the row shipped in for real --
        /// an affordance that was drawn and did nothing -- and it is now something a test can see.
        /// </summary>
        [Fact]
        public void AnUnboundDeleteActionIsNotClickable()
        {
            var arranged = Arrange(UnboundOtaHeader("OTA 1"), 60);

            DeleteAction(arranged).ShouldBeNull();
            DrawsDeleteGlyph(arranged).ShouldBeTrue();
        }

        /// <summary>The title stays readable -- the action never overlaps it.</summary>
        [Fact]
        public void TheDeleteActionFollowsTheTitleWithoutOverlappingIt()
        {
            const string Name = "OTA 3";
            var arranged = Arrange(OtaHeader(Name), 60);
            var action = DeleteAction(arranged).ShouldNotBeNull();

            var title = arranged.Single(
                n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v.Contains(Name));
            (title.Bounds.X + title.Bounds.Width).ShouldBeLessThanOrEqualTo(action.Bounds.X);
        }

        /// <summary>
        /// Removing an OTA takes a CHORD. A bare letter is what blind key injection walks into, and a run
        /// of them armed then confirmed the delete in alternation -- one OTA gone per pair.
        /// </summary>
        [Theory]
        [InlineData(InputKey.X, InputModifier.None, false)]
        [InlineData(InputKey.X, InputModifier.Shift, false)]
        [InlineData(InputKey.X, InputModifier.Ctrl, true)]
        [InlineData(InputKey.X, InputModifier.Ctrl | InputModifier.Shift, true)]
        [InlineData(InputKey.A, InputModifier.Ctrl, false)]
        public void OnlyCtrlXRemovesAnOta(InputKey key, InputModifier modifiers, bool expected)
            => TuiEquipmentTab.IsRemoveOtaChord(key, modifiers).ShouldBe(expected);
    }
}
