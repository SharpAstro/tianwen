using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using TianWen.Cli.Plan;
using TianWen.Cli.Tui;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Cross-cutting guards for every TUI list row, held to the bar a layout refactor is judged by: each
    /// row still draws all of its content, in its own colours, inside the rect it was given.
    /// <para>
    /// <b>Why a row must state its own background.</b> These rows used to be formatted strings that emitted
    /// a foreground and relied on whatever SGR state a previous write happened to leave in effect. A live
    /// terminal forgives that; the diffing cell buffer cannot, because it stores a colour per cell -- so an
    /// inheriting row recorded cells with no colour at all and painted as an empty gap between its
    /// neighbours. An unstated colour is alpha-zero (SGR 39/49, the terminal default), never black, so
    /// "did this row name a background" is exactly an alpha check on the arranged root.
    /// </para>
    /// </summary>
    public class TuiRowLayoutTests
    {
        private const int Width = 60;

        private static ImmutableArray<Layout.ArrangedNode<int>> Arrange(IRowLayout row, bool selected = false)
            => Layout.Engine.Arrange(
                row.BuildRow(RowContext.Single(selected)),
                new Rect<int>(0, 0, Width, 1),
                CellMeasureContext.CellAuthored);

        private static IEnumerable<(string Text, Rect<int> Bounds)> TextLeaves(
            ImmutableArray<Layout.ArrangedNode<int>> arranged)
        {
            foreach (var node in arranged)
            {
                if (node.Node is Layout.Node.Leaf { Content: Layout.Content.Text text })
                {
                    yield return (text.Value, node.Bounds);
                }
            }
        }

        /// <summary>Every row shape the TUI builds, named so a failure says which one.</summary>
        public static TheoryData<string, IRowLayout, string[]> Rows()
        {
            var data = new TheoryData<string, IRowLayout, string[]>();

            data.Add("session group header", new SessionFieldItem { GroupName = "Imaging" }, ["Imaging"]);
            data.Add("session field", new SessionFieldItem
            {
                Field = SessionConfigGroups.Groups[0].Fields[0],
                FieldIndex = 0,
                FormattedValue = "42",
            }, ["42"]);
            data.Add("session OTA field", new SessionFieldItem
            {
                OtaLabel = "Setpoint",
                FieldIndex = 1,
                FormattedValue = "-10°C",
            }, ["Setpoint", "-10°C"]);

            data.Add("equipment section header", new EquipmentFieldItem { SectionName = "Profile Devices" },
                ["Profile Devices"]);
            data.Add("equipment OTA header", new EquipmentFieldItem
            {
                SectionName = "Telescope #1: Newt",
                IsOtaHeader = true,
                OtaIndex = 0,
                OnRemoveOta = _ => { },
            }, ["Telescope #1: Newt", EquipmentFieldItem.DeleteActionLabel]);
            data.Add("equipment action row", new EquipmentFieldItem { ActionLabel = "+ Add OTA" }, ["+ Add OTA"]);
            data.Add("equipment slot row (connected)", new EquipmentFieldItem
            {
                Slot = new AssignTarget.ProfileLevel("Mount"),
                SlotLabel = "Mount",
                SlotDeviceName = "FakeMount1",
                IsSlotActive = true,
                IsConnected = true,
                FieldIndex = 0,
            }, ["Mount", "FakeMount1", "On", "Off", " [>]"]);
            data.Add("equipment slot row (unassigned)", new EquipmentFieldItem
            {
                Slot = new AssignTarget.ProfileLevel("Weather"),
                SlotLabel = "Weather",
                IsSlotActive = false,
                FieldIndex = 1,
            }, ["Weather", "(none)", " [>]"]);
            data.Add("equipment filter row", new EquipmentFieldItem
            {
                FilterIndex = 3,
                FilterName = "Ha",
                FilterOffset = -25,
                FieldIndex = 2,
            }, ["3", "Ha", "-25"]);
            data.Add("equipment property row", new EquipmentFieldItem
            {
                PropertyLabel = "Focal Length",
                PropertyValue = "750",
                FieldIndex = 3,
            }, ["Focal Length", "750"]);

            data.Add("profile picker (active)", new ProfilePickerItem
            {
                Profile = new Profile(Guid.Empty, "Backyard", ProfileData.Empty),
                IsActive = true,
            }, ["Backyard"]);
            data.Add("profile picker (inactive)", new ProfilePickerItem
            {
                Profile = new Profile(Guid.Empty, "Remote Rig", ProfileData.Empty),
                IsActive = false,
            }, ["Remote Rig"]);

            data.Add("planner target", new TargetListItem(new PlannerTargetRow(
                Name: "M 42", Info: "2.5h", ObjectType: "Nebula", IsPinned: true, IsSelected: false,
                HasConflict: false, Index: 0, Rating: 8.5)), ["M 42", "Nebu", "2.5h"]);

            data.Add("notification", new NotificationListItem(
                new NotificationEntry(new DateTimeOffset(2026, 7, 30, 21, 15, 4, TimeSpan.Zero),
                    NotificationSeverity.Warning, "Guide star lost"),
                TimeSpan.Zero), ["21:15:04", "[WARN]", "Guide star lost"]);

            data.Add("info text", new TextRow("Cooling to -10C"), ["Cooling to -10C"]);
            data.Add("info heading", new HeadingRow("OTA 1", IsSelected: true), ["OTA 1"]);
            data.Add("info stepper", new StepperRow("Exposure", "5.0s", _ => { }, _ => { }, "Capture", _ => { }),
                ["[-]", "5.0s", "[+]", "[Capture]"]);
            data.Add("info progress", new ProgressRow("Exposing", 3, 10), ["3/10s"]);
            data.Add("info action", new ActionRow([
                new ActionRow.Button("Save", _ => { }, new VtStyle(SgrColor.BrightWhite, SgrColor.Green)),
                new ActionRow.Button("Solve", _ => { }, new VtStyle(SgrColor.BrightWhite, SgrColor.Blue)),
            ]), ["[Save]", "[Solve]"]);
            data.Add("info blank", new BlankRow(), []);

            return data;
        }

        /// <summary>
        /// Every fragment the row is supposed to show is present. This is the "all the elements still
        /// render" half of the bar -- a row that silently dropped a column would otherwise only show up by
        /// eye, and only on the tab someone happened to open.
        /// </summary>
        [Theory]
        [MemberData(nameof(Rows))]
        public void ARowDrawsAllOfItsContent(string name, IRowLayout row, string[] expected)
        {
            var drawn = TextLeaves(Arrange(row)).Select(l => l.Text).ToList();

            foreach (var fragment in expected)
            {
                drawn.Any(t => t.Contains(fragment, StringComparison.Ordinal)).ShouldBeTrue(
                    $"{name} did not draw \"{fragment}\"; drew: {string.Join(" | ", drawn.Select(d => $"\"{d}\""))}");
            }
        }

        /// <summary>
        /// Nothing is drawn outside the row's own rect. A row cannot pad or truncate any more, so this is
        /// what stands in for the old "MUST emit exactly width visible cells" obligation -- except the
        /// engine enforces it rather than each row promising it.
        /// </summary>
        [Theory]
        [MemberData(nameof(Rows))]
        public void ARowStaysInsideItsRect(string name, IRowLayout row, string[] expected)
        {
            _ = expected;

            foreach (var (text, bounds) in TextLeaves(Arrange(row)))
            {
                bounds.X.ShouldBeGreaterThanOrEqualTo(0, $"{name}: \"{text}\" starts left of the row");
                (bounds.X + bounds.Width).ShouldBeLessThanOrEqualTo(Width, $"{name}: \"{text}\" runs past the row");
                bounds.Height.ShouldBe(1, $"{name}: \"{text}\" is not one row tall");
            }
        }

        /// <summary>
        /// The row names its own background, selected or not. See the class remarks: an unstated colour is
        /// alpha-zero, and a row that inherits one records colourless cells that the diffing buffer paints
        /// as a gap.
        /// <para>
        /// There is deliberately nothing asserted about the root's WIDTH here. <c>Layout.Engine.Arrange</c>
        /// places the root at the rect it was handed and never reads the root's own sizing (only children
        /// are sized by their parent), so "the root spans the row" is the engine's invariant, not the row's,
        /// and asserting it here would pass for every conceivable row.
        /// </para>
        /// </summary>
        [Theory]
        [MemberData(nameof(Rows))]
        public void ARowStatesItsOwnBackground(string name, IRowLayout row, string[] expected)
        {
            _ = expected;

            foreach (var selected in new[] { false, true })
            {
                var root = Arrange(row, selected)[0];
                var background = root.Node.Background.ShouldNotBeNull(
                    $"{name} (selected: {selected}) set no background at all");
                background.Alpha.ShouldNotBe((byte)0,
                    $"{name} (selected: {selected}) named a background with no alpha, which resolves to the terminal default");
            }
        }

        /// <summary>
        /// The cursor row looks different from an ordinary one. Cheap, but it is the whole point of
        /// threading <see cref="RowContext.Selected"/> through, and a row that ignored it would otherwise
        /// pass every other assertion here.
        /// <para>
        /// Compared over every colour in the tree, not just the root background: an action row keeps its
        /// green background either way and brightens only its foreground, so a root-background check would
        /// call that row unstyled.
        /// </para>
        /// </summary>
        [Theory]
        [MemberData(nameof(SelectableRows))]
        public void SelectionChangesHowARowLooks(string name, IRowLayout row)
        {
            Colours(Arrange(row, selected: true))
                .ShouldNotBe(Colours(Arrange(row, selected: false)), $"{name} renders identically when selected");
        }

        /// <summary>Rows whose selection state comes from the list cursor (see the port notes: the planner
        /// and session-config rows carry their own flag instead, because their selected index lives in
        /// shared state the keyboard moves independently).</summary>
        public static TheoryData<string, IRowLayout> SelectableRows()
        {
            var data = new TheoryData<string, IRowLayout>();
            data.Add("equipment slot row", new EquipmentFieldItem
            {
                Slot = new AssignTarget.ProfileLevel("Mount"),
                SlotLabel = "Mount",
                SlotDeviceName = "FakeMount1",
                IsSlotActive = true,
                FieldIndex = 0,
            });
            data.Add("equipment action row", new EquipmentFieldItem { ActionLabel = "+ Add OTA" });
            data.Add("equipment filter row", new EquipmentFieldItem
            {
                FilterIndex = 1,
                FilterName = "L",
                FieldIndex = 0,
            });
            data.Add("profile picker", new ProfilePickerItem
            {
                Profile = new Profile(Guid.Empty, "Backyard", ProfileData.Empty),
                IsActive = false,
            });
            data.Add("notification", new NotificationListItem(
                new NotificationEntry(DateTimeOffset.UnixEpoch, NotificationSeverity.Info, "Slewing"),
                TimeSpan.Zero));
            return data;
        }

        /// <summary>Every colour the tree states, in paint order.</summary>
        private static string Colours(ImmutableArray<Layout.ArrangedNode<int>> arranged)
        {
            var parts = new List<string>(arranged.Length);
            foreach (var node in arranged)
            {
                var fg = node.Node is Layout.Node.Leaf { Content: Layout.Content.Text text }
                    ? text.Color.ToString()
                    : "-";
                parts.Add($"{node.Node.Background?.ToString() ?? "-"}/{fg}");
            }
            return string.Join(",", parts);
        }
    }
}
