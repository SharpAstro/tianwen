using DIR.Lib;
using TianWen.Lib.Imaging.Grading;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The colours a grade is drawn in, from the palette's own semantic colours. The verdict itself is the Lib's
/// (<see cref="LightGrading"/>, #1981), which the stacker and the node read too; only the drawing stays with the UI.
/// </summary>
public static class LightGradeColours
{
    /// <summary>
    /// The colour a relative grade reads as: Error below <see cref="LightGrading.PoorBelow"/>, Success from
    /// <see cref="LightGrading.GoodFrom"/>, Warn between, blended continuously so two frames a few percent apart do not jump a band.
    /// </summary>
    public static RGBAColor32 Colour(UiPalette palette, float relative)
    {
        if (relative <= LightGrading.PoorBelow)
        {
            return palette.Error;
        }
        if (relative >= LightGrading.GoodFrom)
        {
            return palette.Success;
        }

        var middle = (LightGrading.PoorBelow + LightGrading.GoodFrom) * 0.5f;
        return relative < middle
            ? GuiTheme.Mix(palette.Error, palette.Warn, (relative - LightGrading.PoorBelow) / (middle - LightGrading.PoorBelow))
            : GuiTheme.Mix(palette.Warn, palette.Success, (relative - middle) / (LightGrading.GoodFrom - middle));
    }

    /// <summary>A row's background for a relative grade: its colour laid over the panel faintly, so the name stays the thing read.</summary>
    public static RGBAColor32 RowTint(UiPalette palette, float relative) => GuiTheme.Mix(palette.PanelBg, Colour(palette, relative), RowTintShare);

    /// <summary>How much of the grade colour a row's background takes over the panel.</summary>
    private const float RowTintShare = 0.35f;
}
