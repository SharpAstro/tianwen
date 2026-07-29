using Console.Lib;
using DIR.Lib;
using Shouldly;
using TianWen.Cli.Tui;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the OTA header's delete affordance. The row is a formatted STRING, not a layout tree, so
    /// there is no arranged rect for the click region to bind to -- draw and hit are kept in agreement
    /// by both deriving from <see cref="EquipmentFieldItem.DeleteActionColumns"/>. That is what these
    /// tests assert: the columns the click region is registered over are the columns the glyphs land on.
    /// <para>
    /// The defect being fixed: the [X] was drawn but never bound to a hit, so it looked clickable and
    /// was not -- and the <c>X</c> key that actually removed an OTA was absent from the status bar. The
    /// user's only discoverable route to removing an accidentally-added OTA therefore did nothing.
    /// </para>
    /// </summary>
    public class TuiEquipmentRowTests
    {
        private static EquipmentFieldItem OtaHeader(string name, int otaIndex = 0) => new EquipmentFieldItem
        {
            SectionName = name,
            IsOtaHeader = true,
            OtaIndex = otaIndex,
        };

        /// <summary>
        /// The visible cells of a formatted row. <see cref="ColorMode.None"/> suppresses the pen escape
        /// but the trailing <see cref="VtStyle.Reset"/> is unconditional, so it is dropped here to leave
        /// column == string index.
        /// </summary>
        private static string Cells(EquipmentFieldItem item, int width)
        {
            var row = item.FormatRow(width, ColorMode.None);
            row.ShouldEndWith(VtStyle.Reset);
            return row[..^VtStyle.Reset.Length];
        }

        [Theory]
        [InlineData("OTA 1")]
        [InlineData("OTA 12")]
        [InlineData("Telescope with a rather long name")]
        public void TheDeleteActionIsDrawnExactlyWhereItsClickRegionIsRegistered(string name)
        {
            const int Width = 60;
            var row = Cells(OtaHeader(name), Width);
            var (start, end) = EquipmentFieldItem.DeleteActionColumns(name);

            row.Length.ShouldBe(Width);
            row[start..end].ShouldBe(EquipmentFieldItem.DeleteActionLabel);
        }

        [Fact]
        public void ANonOtaSectionHeaderCarriesNoDeleteAction()
        {
            var row = Cells(new EquipmentFieldItem { SectionName = "Site" }, 60);

            row.ShouldNotContain(EquipmentFieldItem.DeleteActionLabel);
        }

        /// <summary>
        /// A row too narrow for the action drops it rather than drawing a clipped one: a half-visible
        /// "[X" would still register a region and delete an OTA on a click the user could not read.
        /// (The registration itself is clamped by ScrollableList, which trims a span running past the
        /// content width.)
        /// </summary>
        [Fact]
        public void ANarrowRowDropsTheDeleteActionRatherThanClippingIt()
        {
            const string Name = "OTA 1";
            var (_, end) = EquipmentFieldItem.DeleteActionColumns(Name);
            var tooNarrow = end - 1;

            var row = Cells(OtaHeader(Name), tooNarrow);

            row.Length.ShouldBe(tooNarrow);
            row.ShouldNotContain("[");
        }

        /// <summary>The title stays readable -- the action never overwrites it.</summary>
        [Fact]
        public void TheDeleteActionFollowsTheTitleWithoutOverlappingIt()
        {
            const string Name = "OTA 3";
            var row = Cells(OtaHeader(Name), 60);
            var (start, _) = EquipmentFieldItem.DeleteActionColumns(Name);

            row.ShouldContain(Name);
            row.IndexOf(Name, System.StringComparison.Ordinal).ShouldBeLessThan(start);
        }

        /// <summary>
        /// Removing an OTA takes a CHORD. A bare letter is what blind key injection walks into, and a
        /// run of them armed then confirmed the delete in alternation -- one OTA gone per pair.
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
