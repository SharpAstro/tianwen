using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TwilightPeriod = TianWen.Lib.Sequencing.TwilightPeriod;

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
/// found on it, the composite that picks a stack's reference frame (many, sharp and round together). Beside it, the two
/// things that score cannot see (#1951): how patchy the sky is (<see cref="Image.SkyPatchiness"/>, NaN on a frame too small
/// to read it), which cloud raises, and which twilight the frame was taken in, null in a dark sky or where the frame does
/// not say when and where it was taken.
/// </summary>
public readonly record struct LightGrade(LightGradeKey Key, FrameMetrics Metrics, float Score, float Patchiness = float.NaN, TwilightPeriod? Twilight = null);

/// <summary>Why a frame's grade colour was held down, beside what its stars say. A frame may have both.</summary>
[Flags]
public enum GradeCause
{
    None = 0,
    /// <summary>A patchy sky, read against the group's best (<see cref="LightGrading.CloudFrom"/>): cloud lit from below.</summary>
    Cloud = 1,
    /// <summary>Taken past astronomical dark as the Sun rose.</summary>
    Sunrise = 2,
    /// <summary>Taken past astronomical dark as the Sun set.</summary>
    Sunset = 4,
}

/// <summary>
/// What a graded frame reads as: its <see cref="Relative"/> sharpness against the best of its group, its
/// <see cref="Patchiness"/> as a multiple of its group's clearest tenth (NaN where unread), the causes that hold it down,
/// and the share it is SHOWN at, which those causes cap so a sharp frame through cloud or in twilight cannot read good.
/// </summary>
public readonly record struct LightGradeVerdict(float Relative, float Patchiness, GradeCause Causes, float Shown);

/// <summary>
/// Grades the frames of a folder as the viewer loads them, each RELATIVE to the best frame of its group seen so far:
/// a grade of 1 is the best yet, 0.5 a frame scoring half of it. Relative because an absolute score means nothing
/// across fields, optics and nights, while the question a blink asks is which of THESE subs are the bad ones.
/// </summary>
public static class LightGrading
{
    /// <summary>
    /// The grade of a frame from its detected stars, or null when it has none to grade by: a frame with no stars is
    /// not graded rather than graded zero, since a dark, a flat or a planetary frame is not a bad light. The one exception is
    /// a LIGHT taken past astronomical dark, which has no stars because the sky is daylight, and is graded at zero with its
    /// twilight so it reads poor and carries the sun mark: on 2026-05-14 the last seven dawn frames, every one rejected by
    /// hand, were left ungraded and so read as nothing at all (#1951).
    /// </summary>
    public static LightGrade? Grade(Image image, StarList stars)
    {
        var meta = image.ImageMeta;
        // A default time is a frame that never said when it was taken (an in-memory frame, a header with no DATE-OBS).
        var twilight = meta.ExposureStartTime == default
            ? null
            : SolarAltitude.Twilight(meta.ExposureStartTime, meta.Latitude, meta.Longitude);
        if (stars.Count == 0)
        {
            return twilight is not null && meta.FrameType == FrameType.Light
                ? new LightGrade(LightGradeKey.Of(image), new FrameMetrics(float.NaN, float.NaN, float.NaN, 0), 0f, float.NaN, twilight)
                : null;
        }

        var metrics = FrameRegistration.MetricsFrom(stars);
        var score = FrameRegistration.ReferenceScore(metrics);
        if (!float.IsFinite(score) || score <= 0f)
        {
            return null;
        }
        return new LightGrade(LightGradeKey.Of(image), metrics, score, image.SkyPatchiness(image.ReferenceStarChannel), twilight);
    }

    /// <summary>
    /// What <paramref name="grade"/> reads as among the graded frames of its group: its sharpness share, its patchiness
    /// against the group's clearest tenth, and the colour share those cap. The ONE place a grade becomes a colour, for the
    /// file list's row and the panel's line alike.
    /// </summary>
    public static LightGradeVerdict Verdict(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
    {
        var relative = Relative(grades, grade);
        var patchiness = RelativePatchiness(grades, grade);
        var causes = GradeCause.None;
        var shown = relative;
        if (patchiness >= CloudFrom)
        {
            causes |= GradeCause.Cloud;
            shown = MathF.Min(shown, patchiness >= HeavyCloudFrom ? PoorBelow : (PoorBelow + GoodFrom) * 0.5f);
        }
        if (grade.Twilight is { } twilight)
        {
            causes |= twilight == TwilightPeriod.Dawn ? GradeCause.Sunrise : GradeCause.Sunset;
            shown = MathF.Min(shown, PoorBelow);
        }
        return new LightGradeVerdict(relative, patchiness, causes, shown);
    }

    /// <summary>
    /// A frame's patchiness as a multiple of its group's clearest tenth (the tenth-lowest of the group's patchiness so far,
    /// the lowest while fewer than ten are graded), so the sky a field always has, a galaxy or a nebula filling the frame,
    /// cancels, and only what changed from frame to frame is left. NaN where the frame's patchiness was not read; 1 while the
    /// group has nothing to compare with.
    /// </summary>
    public static float RelativePatchiness(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
    {
        if (!float.IsFinite(grade.Patchiness))
        {
            return float.NaN;
        }
        var key = grade.Key;
        var count = 0;
        foreach (var other in grades.Values)
        {
            if (other.Key == key && float.IsFinite(other.Patchiness) && other.Twilight is null)
            {
                count++;
            }
        }
        if (count == 0)
        {
            return 1f;
        }
        // Twilight frames are left out of the baseline: a bright sky is a cause of its own, and its gradient is not the dark
        // sky the group's patchiness is read against.
        using var values = ArrayPoolHelper.Rent<float>(count);
        var n = 0;
        foreach (var other in grades.Values)
        {
            if (other.Key == key && float.IsFinite(other.Patchiness) && other.Twilight is null)
            {
                values[n++] = other.Patchiness;
            }
        }
        var span = values.AsSpan(0, n);
        span.Sort();
        var clearest = span[n / 10];
        return clearest > 0f ? grade.Patchiness / clearest : 1f;
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

    /// <summary>
    /// From this multiple of the group's clearest tenth, a frame's sky reads as cloud and its colour is held at warn. Over the
    /// two nights #1951 measured, the clear groups' 90th percentile sat at 1.07 to 1.10 and none reached 1.5, the labelled
    /// cloudy frames at 8 to 13, the frames bordering a cloudy stretch at 2.7 to 3.1, and three the owner rejected by hand at
    /// the end of a cloudy run at 1.74 to 1.96, which a cut at 2 let through as clean.
    /// </summary>
    public const float CloudFrom = 1.5f;

    /// <summary>From this multiple, the cloud holds the colour at poor.</summary>
    public const float HeavyCloudFrom = 4f;

    /// <summary>How much of the grade colour a row's background takes over the panel.</summary>
    private const float RowTintShare = 0.35f;
}
