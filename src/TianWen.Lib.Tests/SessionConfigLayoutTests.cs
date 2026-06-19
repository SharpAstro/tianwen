using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Structural tests for <see cref="SessionConfigLayout"/> -- the "full single-panel tree" (Phase 4):
    /// the whole <see cref="SessionConfiguration"/> form built as ONE <see cref="LayoutNode"/> tree
    /// instead of a per-row cursor walk. Asserts one header per group, one selectable row per field with
    /// sequential indices, the right control per <see cref="ConfigFieldKind"/>, the selected-row
    /// highlight, the running-state handler drop, and that the click callbacks are wired.
    /// </summary>
    public class SessionConfigLayoutTests
    {
        private static readonly SessionConfigStyle Style = new(
            Stepper: new FormRowLayout.StepperStyle(
                new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff), new RGBAColor32(0xff, 0xff, 0xff, 0xff),
                new RGBAColor32(0x33, 0x33, 0x3a, 0xff), new RGBAColor32(0x80, 0x80, 0x80, 0xff),
                14f, 28f),
            HeaderBg: new RGBAColor32(0x24, 0x24, 0x32, 0xff), HeaderText: new RGBAColor32(0xff, 0xff, 0xff, 0xff),
            BodyText: new RGBAColor32(0xff, 0xff, 0xff, 0xff), DimText: new RGBAColor32(0x80, 0x80, 0x80, 0xff),
            RowBg: new RGBAColor32(0x10, 0x10, 0x18, 0xff), RowAltBg: new RGBAColor32(0x1a, 0x1a, 0x24, 0xff),
            SelectedRowBg: new RGBAColor32(0x2a, 0x6b, 0xb8, 0xff),
            ToggleOnBg: new RGBAColor32(0x30, 0x60, 0x40, 0xff), ToggleOffBg: new RGBAColor32(0x40, 0x30, 0x30, 0xff),
            CycleBg: new RGBAColor32(0x30, 0x50, 0x80, 0xff),
            DisabledBg: new RGBAColor32(0x33, 0x33, 0x3a, 0xff),
            FontSize: 14f, HeaderHeight: 28f, ItemHeight: 26f,
            LabelWidth: 160f, Padding: 8f,
            ToggleButtonWidth: 60f, CycleButtonWidth: 140f);

        private static ImmutableArray<ConfigGroup> Groups => SessionConfigGroups.Groups;
        private static SessionConfiguration Config => SessionTabState.DefaultConfiguration;

        private static int FieldCount => Groups.Sum(g => g.Fields.Length);

        private static ConfigFieldDescriptor Field(string label) =>
            Groups.SelectMany(g => g.Fields).First(f => f.Label == label);

        private static IEnumerable<LayoutNode> Flatten(LayoutNode node)
        {
            yield return node;
            switch (node)
            {
                case LayoutNode.Stack s:
                    foreach (var child in s.Children)
                    {
                        foreach (var n in Flatten(child)) yield return n;
                    }
                    break;
                case LayoutNode.Dock d:
                    foreach (var dc in d.Docked)
                    {
                        foreach (var n in Flatten(dc.Child)) yield return n;
                    }
                    foreach (var n in Flatten(d.Fill)) yield return n;
                    break;
                case LayoutNode.Grid g:
                    foreach (var cell in g.Cells)
                    {
                        foreach (var n in Flatten(cell)) yield return n;
                    }
                    break;
                case LayoutNode.Overlay o:
                    foreach (var n in Flatten(o.Base)) yield return n;
                    foreach (var n in Flatten(o.Top)) yield return n;
                    break;
            }
        }

        private static IEnumerable<string> TextLeaves(LayoutNode tree) =>
            Flatten(tree)
                .OfType<LayoutNode.Leaf>()
                .Select(l => l.Content)
                .OfType<LayoutContent.Text>()
                .Select(t => t.Value);

        // The top-level row for a given field index: the root child whose subtree carries that ConfigField hit.
        private static LayoutNode RowFor(LayoutNode tree, int index) =>
            ((LayoutNode.Stack)tree).Children.First(c =>
                Flatten(c).Any(n => n.Hit is HitResult.ListItemHit { ListId: "ConfigField" } li && li.Index == index));

        [Fact]
        public void Build_EmitsOneHeaderPerGroup()
        {
            var tree = SessionConfigLayout.Build(Groups, Config, -1, false, 80f, Style);

            var headers = Flatten(tree).Where(n => n.Background is { } bg && bg.Equals(Style.HeaderBg)).ToList();
            headers.Count.ShouldBe(Groups.Length);

            foreach (var g in Groups)
            {
                TextLeaves(tree).ShouldContain(g.Name);
            }
        }

        [Fact]
        public void Build_EmitsOneSelectableRowPerField_WithSequentialIndices()
        {
            var tree = SessionConfigLayout.Build(Groups, Config, -1, false, 80f, Style);

            var indices = Flatten(tree)
                .Select(n => n.Hit)
                .OfType<HitResult.ListItemHit>()
                .Where(h => h.ListId == "ConfigField")
                .Select(h => h.Index)
                .ToList();

            indices.ShouldBe(Enumerable.Range(0, FieldCount).ToList());
        }

        [Fact]
        public void Build_StepperField_HasDecValueIncButtons()
        {
            var tree = SessionConfigLayout.Build(Groups, Config, -1, false, 80f, Style);

            var display = SessionConfigLayout.FormatStepperDisplay(Field("Dither Pixels"), Config);
            TextLeaves(tree).ShouldContain(display);

            var dec = Flatten(tree).First(n => n.Hit is HitResult.ButtonHit { Action: "Dec:Dither Pixels" });
            var inc = Flatten(tree).First(n => n.Hit is HitResult.ButtonHit { Action: "Inc:Dither Pixels" });
            dec.Background.ShouldNotBeNull();
            inc.Background.ShouldNotBeNull();
            dec.OnClick.ShouldNotBeNull();
            inc.OnClick.ShouldNotBeNull();
        }

        [Fact]
        public void Build_ToggleField_IsSingleButtonWithHit_NoStepperButtons()
        {
            var tree = SessionConfigLayout.Build(Groups, Config, -1, false, 80f, Style,
                onIncrement: _ => _ => { });

            var toggles = Flatten(tree).Where(n => n.Hit is HitResult.ButtonHit { Action: "Toggle:Refocus on New" }).ToList();
            toggles.ShouldHaveSingleItem();
            toggles[0].Background.ShouldNotBeNull();
            toggles[0].OnClick.ShouldNotBeNull();

            // The current value (ON / OFF) renders as the button text.
            TextLeaves(tree).ShouldContain(Field("Refocus on New").FormatValue(Config));

            // A toggle has no [-]/[+] stepper buttons.
            Flatten(tree).Any(n => n.Hit is HitResult.ButtonHit { Action: "Dec:Refocus on New" }).ShouldBeFalse();
        }

        [Fact]
        public void Build_CycleField_IsSingleButtonWithHit()
        {
            var tree = SessionConfigLayout.Build(Groups, Config, -1, false, 80f, Style,
                onIncrement: _ => _ => { });

            var cycle = Flatten(tree).Single(n => n.Hit is HitResult.ButtonHit { Action: "Cycle:Focus Filter" });
            cycle.OnClick.ShouldNotBeNull();

            var value = Field("Focus Filter").FormatValue(Config);
            TextLeaves(tree).Any(t => t.Contains(value)).ShouldBeTrue();
        }

        [Fact]
        public void Build_SelectedField_GetsSelectedRowBackground()
        {
            const int selected = 3;
            var tree = SessionConfigLayout.Build(Groups, Config, selected, false, 80f, Style);

            RowFor(tree, selected).Background.ShouldBe(Style.SelectedRowBg);
            RowFor(tree, selected == 0 ? 1 : 0).Background.ShouldNotBe(Style.SelectedRowBg);
        }

        [Fact]
        public void Build_Running_KeepsHitSurfaceButDropsHandlers()
        {
            var tree = SessionConfigLayout.Build(Groups, Config, -1, running: true, 80f, Style,
                onSelectField: _ => _ => { },
                onDecrement: _ => _ => { },
                onIncrement: _ => _ => { });

            var dec = Flatten(tree).First(n => n.Hit is HitResult.ButtonHit { Action: "Dec:Dither Pixels" });
            dec.Hit.ShouldNotBeNull();
            dec.OnClick.ShouldBeNull();

            var toggle = Flatten(tree).First(n => n.Hit is HitResult.ButtonHit { Action: "Toggle:Refocus on New" });
            toggle.OnClick.ShouldBeNull();

            var cycle = Flatten(tree).First(n => n.Hit is HitResult.ButtonHit { Action: "Cycle:Focus Filter" });
            cycle.OnClick.ShouldBeNull();
        }

        [Fact]
        public void Build_OnSelectField_FiresWithFieldIndex()
        {
            var clicked = new List<int>();
            var tree = SessionConfigLayout.Build(Groups, Config, -1, false, 80f, Style,
                onSelectField: i => _ => clicked.Add(i));

            var row = Flatten(tree).First(n => n.Hit is HitResult.ListItemHit { ListId: "ConfigField", Index: 2 });
            row.OnClick.ShouldNotBeNull();
            row.OnClick!(InputModifier.None);
            clicked.ShouldHaveSingleItem().ShouldBe(2);
        }

        [Fact]
        public void Build_OnIncrement_WiresStepperAndToggle()
        {
            var incremented = new List<string>();
            var tree = SessionConfigLayout.Build(Groups, Config, -1, false, 80f, Style,
                onIncrement: f => _ => incremented.Add(f.Label));

            Flatten(tree).First(n => n.Hit is HitResult.ButtonHit { Action: "Inc:Dither Pixels" }).OnClick!(InputModifier.None);
            Flatten(tree).First(n => n.Hit is HitResult.ButtonHit { Action: "Toggle:Refocus on New" }).OnClick!(InputModifier.None);

            incremented.ShouldBe(["Dither Pixels", "Refocus on New"], ignoreOrder: true);
        }
    }
}
