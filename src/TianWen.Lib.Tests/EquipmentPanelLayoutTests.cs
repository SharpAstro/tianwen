using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Structural tests for <see cref="EquipmentPanelLayout"/> -- the bridge that turns the data-driven
    /// equipment content models (<see cref="DeviceSlotRow"/> / <see cref="OtaSummaryRow"/>) into the single
    /// <see cref="LayoutNode"/> tree both the GPU and TUI painters consume. Asserts the per-OTA loop, the
    /// whole-row clickable Hit carrying the <see cref="AssignTarget"/>, and the active-slot highlight.
    /// </summary>
    public class EquipmentPanelLayoutTests
    {
        private static DeviceSlotRow[] OtaSlots(int i) =>
        [
            new DeviceSlotRow("Camera", "Cam", true, new AssignTarget.OTALevel(i, "Camera")),
            new DeviceSlotRow("Focuser", "Foc", true, new AssignTarget.OTALevel(i, "Focuser")),
            new DeviceSlotRow("Filter Wheel", "FW", true, new AssignTarget.OTALevel(i, "FilterWheel")),
            new DeviceSlotRow("Cover", "Cov", false, new AssignTarget.OTALevel(i, "Cover")),
        ];

        private static DeviceSlotRow[] ProfileSlots() =>
        [
            new DeviceSlotRow("Mount", "FakeMount", true, new AssignTarget.ProfileLevel("Mount")),
            new DeviceSlotRow("Guider", "None", false, new AssignTarget.ProfileLevel("Guider")),
        ];

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

        [Fact]
        public void Build_LoopsOverEachOta_OneHeaderPerTelescope()
        {
            var otas = new[]
            {
                new OtaSummaryRow(0, "Newt", "f=500mm", OtaSlots(0), null),
                new OtaSummaryRow(1, "Refractor", "f=900mm", OtaSlots(1), null),
            };

            var tree = EquipmentPanelLayout.Build("Rig", ProfileSlots(), otas, EquipmentPanelStyle.Default);

            var headers = TextLeaves(tree).Where(t => t.StartsWith("Telescope #")).ToList();
            headers.ShouldBe(["Telescope #0: Newt", "Telescope #1: Refractor"]);
        }

        [Fact]
        public void Build_AddingAnOta_AddsAnotherSection()
        {
            var one = EquipmentPanelLayout.Build("Rig", ProfileSlots(),
                [new OtaSummaryRow(0, "A", "", OtaSlots(0), null)], EquipmentPanelStyle.Default);
            var two = EquipmentPanelLayout.Build("Rig", ProfileSlots(),
                [new OtaSummaryRow(0, "A", "", OtaSlots(0), null), new OtaSummaryRow(1, "B", "", OtaSlots(1), null)],
                EquipmentPanelStyle.Default);

            TextLeaves(one).Count(t => t.StartsWith("Telescope #")).ShouldBe(1);
            TextLeaves(two).Count(t => t.StartsWith("Telescope #")).ShouldBe(2);
        }

        [Fact]
        public void Build_EachOtaSection_HasFourClickableSubSlotRows()
        {
            var otas = new[]
            {
                new OtaSummaryRow(0, "A", "", OtaSlots(0), null),
                new OtaSummaryRow(1, "B", "", OtaSlots(1), null),
            };

            var tree = EquipmentPanelLayout.Build("Rig", ProfileSlots(), otas, EquipmentPanelStyle.Default);

            var ota0Rows = Flatten(tree).Count(n =>
                n.Hit is HitResult.SlotHit<AssignTarget> { Slot: AssignTarget.OTALevel { OtaIndex: 0 } });
            var ota1Rows = Flatten(tree).Count(n =>
                n.Hit is HitResult.SlotHit<AssignTarget> { Slot: AssignTarget.OTALevel { OtaIndex: 1 } });

            ota0Rows.ShouldBe(4);
            ota1Rows.ShouldBe(4);
        }

        [Fact]
        public void Build_ProfileSlotRow_CarriesSlotHitForAssignment()
        {
            var tree = EquipmentPanelLayout.Build("Rig", ProfileSlots(), [], EquipmentPanelStyle.Default);

            var mount = Flatten(tree).SingleOrDefault(n =>
                n.Hit is HitResult.SlotHit<AssignTarget> { Slot: AssignTarget.ProfileLevel { Field: "Mount" } });

            mount.ShouldNotBeNull();
            mount.ShouldBeOfType<LayoutNode.Stack>(); // the whole row, not just a leaf
        }

        [Fact]
        public void Build_ActiveSlot_GetsActiveBackground_OthersNormal()
        {
            var style = EquipmentPanelStyle.Default;
            var active = new AssignTarget.ProfileLevel("Mount");

            var tree = EquipmentPanelLayout.Build("Rig", ProfileSlots(), [], style, activeSlot: active);

            LayoutNode? RowFor(string field) => Flatten(tree).FirstOrDefault(n =>
                n.Hit is HitResult.SlotHit<AssignTarget> { Slot: AssignTarget.ProfileLevel p } && p.Field == field);

            RowFor("Mount")!.Background.ShouldBe(style.SlotActive);
            RowFor("Guider")!.Background.ShouldBe(style.SlotNormal);
        }

        [Fact]
        public void Build_OnSlotClick_WiresHandlerToEachRow()
        {
            var clicked = new List<AssignTarget>();
            var tree = EquipmentPanelLayout.Build("Rig", ProfileSlots(), [], EquipmentPanelStyle.Default,
                onSlotClick: slot => _ => clicked.Add(slot));

            var mount = Flatten(tree).First(n =>
                n.Hit is HitResult.SlotHit<AssignTarget> { Slot: AssignTarget.ProfileLevel { Field: "Mount" } });

            mount.OnClick.ShouldNotBeNull();
            mount.OnClick!(InputModifier.None);
            clicked.ShouldHaveSingleItem().ShouldBe(new AssignTarget.ProfileLevel("Mount"));
        }

        [Fact]
        public void Build_FilterTable_RendersOneRowPerFilter()
        {
            var filters = new[]
            {
                new FilterSlotRow(1, "L", 0),
                new FilterSlotRow(2, "R", -15),
                new FilterSlotRow(3, "G", 10),
            };
            var otas = new[] { new OtaSummaryRow(0, "A", "", OtaSlots(0), filters) };

            var tree = EquipmentPanelLayout.Build("Rig", ProfileSlots(), otas, EquipmentPanelStyle.Default);

            // Each filter name appears as a text leaf inside the filter sub-table.
            var names = TextLeaves(tree).Where(t => t is "L" or "R" or "G").ToList();
            names.ShouldBe(["L", "R", "G"], ignoreOrder: true);
        }

        [Fact]
        public void ToggleHeaderRow_HasHitAndLabel()
        {
            var row = FormRowLayout.ToggleHeaderRow(
                "    Filters (3) [+]", 20f,
                new RGBAColor32(0x1a, 0x1a, 0x26, 0xff), new RGBAColor32(0xff, 0xff, 0xff, 0xff),
                12f, new HitResult.ButtonHit("ToggleFilters0"), _ => { });
            row.Hit.ShouldNotBeNull();
            row.Hit.ShouldBeOfType<HitResult.ButtonHit>();
            TextLeaves(row).ShouldContain("    Filters (3) [+]");
        }

        [Fact]
        public void LabeledInputRow_HasLabelAndFill()
        {
            var row = FormRowLayout.LabeledInputRow(
                "  Lat:", 50f, 24f, 6f, 12f, new RGBAColor32(0x80, 0x80, 0x80, 0xff));
            TextLeaves(row).ShouldContain("  Lat:");
            var fills = Flatten(row).OfType<LayoutNode.Leaf>().Where(l => l.Content is LayoutContent.Fill).ToList();
            fills.ShouldHaveSingleItem();
        }

        [Fact]
        public void StepperRow_HasDecValueInc()
        {
            var clicked = new List<string>();
            var row = FormRowLayout.StepperRow(
                "Offset", "+5", 20f, 4f, 12f, 24f,
                new RGBAColor32(0x1a, 0x1a, 0x26, 0xff),
                new RGBAColor32(0x2a, 0x40, 0x5a, 0xff),
                new RGBAColor32(0x80, 0x80, 0x80, 0xff),
                new RGBAColor32(0xff, 0xff, 0xff, 0xff),
                new RGBAColor32(0xff, 0xff, 0xff, 0xff),
                "Dec", _ => clicked.Add("dec"),
                "Inc", _ => clicked.Add("inc"));
            TextLeaves(row).ShouldContain("Offset");
            TextLeaves(row).ShouldContain("+5");
            TextLeaves(row).ShouldContain("-");
            TextLeaves(row).ShouldContain("+");
            var dec = Flatten(row).First(n => n.Hit is HitResult.ButtonHit { Action: "Dec" });
            dec.OnClick.ShouldNotBeNull();
            dec.OnClick!(InputModifier.None);
            clicked.ShouldContain("dec");
        }
    }
}
