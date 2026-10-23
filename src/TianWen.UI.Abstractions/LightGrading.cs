using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What a grade is measured against: frames of the same field through the same filter, exposure and binning on the
/// same sensor. Unlike <see cref="FrameShape"/> every part must MATCH, a missing name included: a star count is only
/// comparable at one exposure through one filter (a blue sub of the same field shows a fraction of the luminance sub's
/// stars), so the permissive rule the display carry needs would grade a set's every blue frame against its best L.
/// </summary>
public readonly record struct LightGradeKey(
    int Width,
    int Height,
    SensorType SensorType,
    string FilterKey,
    string ObjectKey,
    long ExposureMs,
    int Bin)
{
    public static LightGradeKey Of(Image image)
    {
        var meta = image.ImageMeta;
        return new LightGradeKey(
            image.Width,
            image.Height,
            meta.SensorType,
            meta.Filter.Name is null ? string.Empty : meta.Filter.IdentityKey,
            meta.ObjectName is { Length: > 0 } name ? name.Trim().ToUpperInvariant() : string.Empty,
            (long)Math.Round(meta.ExposureDuration.TotalMilliseconds),
            meta.BinX);
    }

    /// <summary>The key in words, for the panel: the filter and the exposure, which are what tell two groups of one folder apart.</summary>
    public string Describe()
    {
        var exposure = ExposureMs % 1000 == 0 ? $"{ExposureMs / 1000} s" : $"{ExposureMs / 1000.0:0.###} s";
        return FilterKey.Length > 0 ? $"{FilterKey}, {exposure}" : exposure;
    }
}

/// <summary>
/// One frame's quality as the stacker scores it: <see cref="FrameRegistration.ReferenceScore"/> over the stars the viewer
/// found on it, the composite that picks a stack's reference frame (many, sharp and round together).
/// </summary>
public readonly record struct LightGrade(LightGradeKey Key, FrameMetrics Metrics, float Score);

/// <summary>
/// Grades the frames of a folder as the viewer loads them, each RELATIVE to the best frame of its group seen so far:
/// a grade of 1 is the best yet, 0.5 a frame scoring half of it. Relative because an absolute score means nothing
/// across fields, optics and nights, while the question a blink asks is which of THESE subs are the bad ones.
/// </summary>
public static class LightGrading
{
    /// <summary>
    /// The grade of a frame from its detected stars, or null when it has none to grade by: a frame with no stars is
    /// not graded rather than graded zero, since a dark, a flat or a planetary frame is not a bad light.
    /// </summary>
    public static LightGrade? Grade(Image image, StarList stars)
    {
        if (stars.Count == 0)
        {
            return null;
        }

        var metrics = FrameRegistration.MetricsFrom(stars);
        var score = FrameRegistration.ReferenceScore(metrics);
        return float.IsFinite(score) && score > 0f ? new LightGrade(LightGradeKey.Of(image), metrics, score) : null;
    }

    /// <summary>The best score among the graded frames of <paramref name="key"/>'s group, or 0 when none is graded.</summary>
    public static float BestScore(ImmutableDictionary<string, LightGrade> grades, LightGradeKey key)
    {
        var best = 0f;
        foreach (var grade in grades.Values)
        {
            if (grade.Key == key && grade.Score > best)
            {
                best = grade.Score;
            }
        }
        return best;
    }

    /// <summary>A graded frame's score as a share of the best of its group seen so far, in (0, 1].</summary>
    public static float Relative(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
    {
        var best = BestScore(grades, grade.Key);
        return best > 0f ? Math.Clamp(grade.Score / best, 0f, 1f) : 1f;
    }

    /// <summary>
    /// The colour a relative grade reads as, from the palette's own semantic colours: Error below
    /// <see cref="PoorBelow"/>, Success from <see cref="GoodFrom"/>, Warn between, blended continuously so two frames
    /// a few percent apart do not jump a band.
    /// </summary>
    public static RGBAColor32 Colour(UiPalette palette, float relative)
    {
        if (relative <= PoorBelow)
        {
            return palette.Error;
        }
        if (relative >= GoodFrom)
        {
            return palette.Success;
        }

        var middle = (PoorBelow + GoodFrom) * 0.5f;
        return relative < middle
            ? GuiTheme.Mix(palette.Error, palette.Warn, (relative - PoorBelow) / (middle - PoorBelow))
            : GuiTheme.Mix(palette.Warn, palette.Success, (relative - middle) / (GoodFrom - middle));
    }

    /// <summary>A row's background for a relative grade: its colour laid over the panel faintly, so the name stays the thing read.</summary>
    public static RGBAColor32 RowTint(UiPalette palette, float relative) => GuiTheme.Mix(palette.PanelBg, Colour(palette, relative), RowTintShare);

    /// <summary>At or below this share of the best, a frame reads as poor.</summary>
    public const float PoorBelow = 0.5f;

    /// <summary>From this share of the best, a frame reads as good.</summary>
    public const float GoodFrom = 0.85f;

    /// <summary>How much of the grade colour a row's background takes over the panel.</summary>
    private const float RowTintShare = 0.35f;
}
