using System;
using TianWen.Lib.Geometry;
using System.Globalization;

namespace TianWen.Lib.Imaging;

/// <summary>
/// The IRAF section string that FITS uses for <c>DATASEC</c>, <c>BIASSEC</c> and <c>TRIMSEC</c>, and
/// the ONE place its convention is converted.
/// </summary>
/// <remarks>
/// <para><b>A section is 1-based and INCLUSIVE at both ends; a <see cref="PixelRect"/> is 0-based
/// with an exclusive width.</b> <c>[1:16,1:8]</c> is the first sixteen columns of the first eight
/// rows, and lands as <c>X = 0, Y = 0, Width = 16, Height = 8</c>. This is the same trap as CRPIX,
/// which is why it is converted here and nowhere else: the two conventions differ by one in the
/// origin and by one in the extent, so a single-sided fix looks right on a section starting at 1 and
/// is wrong on every other.</para>
/// <para><b>There is no row flip.</b> The section's rows index the FITS data array as stored, and
/// TianWen's reader stores a FITS raster in file order without flipping (<c>ROWORDER</c> travels as
/// metadata on <see cref="ImageMeta.RowOrder"/> and is applied by consumers that care). So a section
/// maps onto the in-memory raster index for index.</para>
/// <para><b>Our own frames are stored TOP-DOWN, which standard FITS is not, and that was measured
/// rather than assumed (2026-09-16).</b> Every archive frame carries <c>ROWORDER = 'TOP-DOWN'</c>,
/// and a plate solve on two independent trains agrees: the SWQ8 Newtonian (two reflections) and the
/// SH61 refractor (none) are both EVEN and therefore non-mirrored on the sky, so a raster in the
/// standard orientation would solve to a NEGATIVE <c>CD</c> determinant; both solve POSITIVE
/// (+1.93e-7 and +6.34e-7), which is the sign a vertical flip produces. <b>The consequence is that
/// the raster, its WCS and its sections are all in the SAME top-down frame, so a consumer flips all
/// three or none.</b> Flipping the picture to the standard orientation while keeping either the WCS
/// or the section is the bug this paragraph exists to prevent.</para>
/// <para>Bounds are not checked against any image here, deliberately: a section that overruns its
/// frame is a fact about the file, and the caller that has the frame is the one that can say so.</para>
/// </remarks>
public static class FitsSection
{
    /// <summary>
    /// Parses <c>[x1:x2,y1:y2]</c> into a 0-based <see cref="PixelRect"/>. The brackets are optional
    /// (some writers omit them) and a third axis, legal on a cube, is accepted and ignored: the
    /// spatial rectangle is what a section means to every consumer here. A strided section
    /// (<c>x1:x2:step</c>) is REFUSED rather than silently read as its bounds, since a stride is not
    /// a rectangle and quietly dropping it would hand back a region the file does not describe.
    /// </summary>
    public static bool TryParse(string? value, out PixelRect section)
    {
        section = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('[') && text.EndsWith(']'))
        {
            text = text[1..^1];
        }

        var axes = text.Split(',');
        if (axes.Length is < 2 or > 3)
        {
            return false;
        }

        if (!TryRange(axes[0], out var x1, out var x2) || !TryRange(axes[1], out var y1, out var y2))
        {
            return false;
        }

        section = new PixelRect(x1 - 1, y1 - 1, x2 - x1 + 1, y2 - y1 + 1);
        return true;
    }

    /// <summary>The 1-based inclusive section string for a 0-based rectangle, as a FITS card value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty or starts before the origin.</exception>
    public static string Format(PixelRect section)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(section.X, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(section.Y, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(section.Width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(section.Height, 1);
        return string.Create(CultureInfo.InvariantCulture,
            $"[{section.X + 1}:{section.X + section.Width},{section.Y + 1}:{section.Y + section.Height}]");
    }

    private static bool TryRange(string axis, out int lo, out int hi)
    {
        lo = hi = 0;
        var parts = axis.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out lo)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hi))
        {
            return false;
        }

        // A section runs low to high and starts at 1. A reversed range is legal IRAF for a mirrored
        // read, and is refused here for the same reason a stride is: it is not this rectangle.
        return lo >= 1 && hi >= lo;
    }

}
