using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;

namespace TianWen.Cli.Tui
{
    /// <summary>
    /// A row in the profile picker list. Shows profile name with active indicator.
    /// </summary>
    internal sealed class ProfilePickerItem : IRowLayout
    {
        /// <summary>The profile.</summary>
        public required Profile Profile { get; init; }

        /// <summary>Whether this profile is the currently active one.</summary>
        public bool IsActive { get; init; }

        public Layout.Node BuildRow(in RowContext context)
        {
            // Cursor first, then active: the cursor can sit on a profile that is not the active one, and
            // which row you are about to switch TO is the more urgent fact of the two.
            var pen = context.Selected ? TuiRowPalette.Selected
                : IsActive ? new RowPen(SgrColor.BrightGreen, SgrColor.Black)
                : TuiRowPalette.Body;

            return pen.Text($" {(IsActive ? "▶ " : "  ")}{Profile.DisplayName}");
        }
    }
}
