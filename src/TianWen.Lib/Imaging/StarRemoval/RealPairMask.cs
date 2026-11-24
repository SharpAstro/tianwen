using System;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// Which pixels of a real pair count in a star remover's loss (docs/plans/star-remover-training.md, "Real pairs"). A real
/// pair is a master's own cell as the input and its starless plate's cell as the target, so the stars are real and the
/// plate builder's subtraction is the truth. That truth holds only where the builder took a star out and nothing it left
/// in stands beside it: a pixel counts when it lies in the builder's subtracted footprint, further than
/// <see cref="KeptSourceFwhm"/> PSF widths from every source the builder found and left in the plate (a knot, a source it
/// could not fit, a peak merged into a saturated core), and further than <see cref="SaturatedReachPx"/> pixels from every
/// saturated star, whose halo the plate keeps. Everywhere else the plate still holds what the finder never listed (the
/// faint stars under its limit), and a target that counted it would teach a remover to keep a faint star.
/// </summary>
public static class RealPairMask
{
    /// <summary>The sky within this many PSF widths of a source the builder left in the plate does not count.</summary>
    public const double KeptSourceFwhm = 2.0;

    /// <summary>The sky within this many pixels of a saturated star does not count: the plate keeps its halo.</summary>
    public const double SaturatedReachPx = 30.0;

    /// <summary>
    /// The frame's counted pixels: <paramref name="subtracted"/> (the builder's footprint, <c>[row, column]</c>) less the
    /// zones round the kept sources and the saturated stars of <paramref name="catalogue"/>, and less every
    /// <paramref name="absent"/> pixel. A zone is cut at the frame's edge, never dropped for reaching it.
    /// </summary>
    /// <param name="subtracted">Where the builder subtracted a star's model, the plate's own mask.</param>
    /// <param name="catalogue">Every source the builder found, in the frame's pixel coordinates.</param>
    /// <param name="fwhm">The field's PSF width in pixels, the builder's own.</param>
    /// <param name="absent">Pixels with no data (a master's canvas ring), or null for none.</param>
    public static BitMatrix Counted(BitMatrix subtracted, IReadOnlyList<FittedStar> catalogue, double fwhm, BitMatrix? absent = null)
    {
        if (!(fwhm > 0) || !double.IsFinite(fwhm))
        {
            throw new ArgumentOutOfRangeException(nameof(fwhm), fwhm, "the PSF width is a positive number of pixels");
        }
        var height = subtracted.GetLength(0);
        var width = subtracted.GetLength(1);
        if (absent is { } given && (given.GetLength(0) != height || given.GetLength(1) != width))
        {
            throw new ArgumentException("the absent pixels are not the footprint's shape", nameof(absent));
        }

        var blocked = new BitMatrix(height, width);
        foreach (var star in catalogue)
        {
            if (star.Outcome != StarFitOutcome.Subtracted)
            {
                Stamp(blocked, star.X, star.Y, KeptSourceFwhm * fwhm);
            }
            else if (star.Saturated)
            {
                Stamp(blocked, star.X, star.Y, SaturatedReachPx);
            }
        }

        var counted = new BitMatrix(height, width);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                counted[y, x] = subtracted[y, x] && !blocked[y, x] && absent?[y, x] != true;
            }
        }
        return counted;
    }

    // Sets the disc of the radius round a position (pixel centres at integers), cut at the frame's edge.
    private static void Stamp(BitMatrix mask, double cx, double cy, double radius)
    {
        if (!double.IsFinite(cx) || !double.IsFinite(cy))
        {
            return;
        }
        var height = mask.GetLength(0);
        var width = mask.GetLength(1);
        var y0 = Math.Max(0, (int)Math.Ceiling(cy - radius));
        var y1 = Math.Min(height - 1, (int)Math.Floor(cy + radius));
        var x0 = Math.Max(0, (int)Math.Ceiling(cx - radius));
        var x1 = Math.Min(width - 1, (int)Math.Floor(cx + radius));
        var r2 = radius * radius;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= r2)
                {
                    mask[y, x] = true;
                }
            }
        }
    }
}
