using System.Linq;
using DIR.Lib;
using Shouldly;

namespace TianWen.Lib.Tests;

/// <summary>
/// Reads an open dropdown back from what a widget REGISTERED this frame, which is the geometry the user
/// clicks and the inspector reports, rather than from the tree the widget meant to build.
/// </summary>
internal static class DropdownRows
{
    /// <summary>The first row of the one dropdown the widget painted.</summary>
    /// <remarks>
    /// Every row of a <c>Layout.Builder.Dropdown</c> registers a <see cref="HitResult.ListItemHit"/> under
    /// <see cref="DropdownMenuState{T}.ListId"/>, whatever the item type, so this finds a menu of any
    /// <c>T</c>. Exactly one is required: two open menus would make "the row" ambiguous.
    /// </remarks>
    internal static RectF32 First<TSurface>(PixelWidgetBase<TSurface> widget)
    {
        var rows = widget.GetRegisteredRegions()
            .Where(r => r.Result is HitResult.ListItemHit { ListId: DropdownMenuState<string>.ListId, Index: 0 })
            .ToArray();

        rows.Length.ShouldBe(1, "exactly one open dropdown paints a first row");
        return new RectF32(rows[0].X, rows[0].Y, rows[0].Width, rows[0].Height);
    }
}
