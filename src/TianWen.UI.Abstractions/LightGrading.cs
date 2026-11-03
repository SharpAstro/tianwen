using System;
using System.Collections.Generic;
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
/// not say when and where it was taken. <see cref="SkyLevel"/> is the star-masked sky median in the frame's own counts (ADU,
/// NaN without a star mask) and <see cref="Night"/> the local evening it belongs to, which together read a sky BRIGHT against
/// the rest of its night: cloud lit from below over a whole target, which a group-relative measure cannot see. <see cref="Taken"/>
/// is when the exposure started, default where the frame does not say, which places its patchiness against the frames either
/// side of it (<see cref="LightGrading.LocalPatchiness"/>).
/// </summary>
public readonly record struct LightGrade(LightGradeKey Key, FrameMetrics Metrics, float Score, float Patchiness = float.NaN,
    TwilightPeriod? Twilight = null, float SkyLevel = float.NaN, DateOnly Night = default, DateTimeOffset Taken = default);

/// <summary>Why a frame's grade colour was held down, beside what its stars say. A frame may have both.</summary>
[Flags]
public enum GradeCause
{
    None = 0,
    /// <summary>
    /// Cloud: a sky patchier than its group's clearest (<see cref="LightGrading.CloudFrom"/>) and than the frames taken either
    /// side of it (<see cref="LightGrading.LocalCloudFrom"/>), or brighter than its night's
    /// darkest across every target (<see cref="LightGrading.NightSkyCloudFrom"/>).
    /// </summary>
    Cloud = 1,
    /// <summary>Taken past astronomical dark as the Sun rose.</summary>
    Sunrise = 2,
    /// <summary>Taken past astronomical dark as the Sun set.</summary>
    Sunset = 4,
}

/// <summary>
/// What a graded frame reads as: its <see cref="Relative"/> sharpness against the best of its group, its
/// <see cref="Patchiness"/> as a multiple of its group's clearest tenth (NaN where unread), the causes that hold it down,
/// its <see cref="NightSky"/> as a multiple of its night's darkest tenth (NaN where unread), its <see cref="LocalPatchiness"/>
/// against the frames taken within an hour of it (NaN where unread), and the share it is SHOWN at, which those causes cap so a
/// sharp frame through cloud or in twilight cannot read good.
/// </summary>
public readonly record struct LightGradeVerdict(float Relative, float Patchiness, GradeCause Causes, float Shown, float NightSky = float.NaN,
    float LocalPatchiness = float.NaN);

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
    /// <remarks>
    /// <b>It reads the planes</b> (the sky's patchiness and level), so call it while they are resident: the viewer does, inside
    /// <see cref="AstroImageDocument.DetectStarsAsync"/>'s <c>whileResident</c>. Called on an evicted image it restores the
    /// planes and leaves them so (<c>DocumentResidencyTests</c>).
    /// </remarks>
    public static LightGrade? Grade(Image image, StarList stars, float? fullScaleAdu = null)
    {
        var meta = image.ImageMeta;
        // A default time is a frame that never said when it was taken (an in-memory frame, a header with no DATE-OBS).
        var twilight = meta.ExposureStartTime == default || !SiteKnown(meta)
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
        return new LightGrade(LightGradeKey.Of(image), metrics, score, image.SkyPatchiness(image.ReferenceStarChannel), twilight,
            SkyLevelOf(image, stars, fullScaleAdu), NightOf(meta), meta.ExposureStartTime);
    }

    /// <summary>
    /// The star-masked sky median in the frame's own counts (ADU): the median itself, not above the frame's darkest pixel, in the
    /// file's counts. The viewer grades the image <see cref="AstroImageDocument.AdoptImageAsync"/> already scaled to [0, 1] by
    /// the frame's own observed peak, which differs from frame to frame, so <paramref name="fullScaleAdu"/> (the count a sample
    /// of 1 stood for, <see cref="AstroImageDocument.FullScaleAdu"/>) puts it back; null for an image still in counts or
    /// genuinely on [0, 1]. A mosaic is read at its green photosites, the masked statistics' own convention. NaN without a star mask.
    /// </summary>
    private static float SkyLevelOf(Image image, StarList stars, float? fullScaleAdu)
    {
        if (stars.StarMask is not { } mask)
        {
            return float.NaN;
        }
        var (_, median, _) = image.IsCfaMosaic
            ? image.GetStarMaskedMedianAndMADScaledToUnit(0, mask, cfa: CfaChannel.Green)
            : image.GetStarMaskedMedianAndMADScaledToUnit(image.ReferenceStarChannel, mask);
        // The statistic is (median - MinValue) / divisor, the divisor 1 on a scaled image and the observed peak on one in counts:
        // undo both to the image's own units, then by the scale the document spent.
        var divisor = image.HasUnitScalePeak ? 1f : image.MaxValue;
        return (median * divisor + image.MinValue) * (fullScaleAdu ?? 1f);
    }

    /// <summary>
    /// Whether the header names a site: a finite latitude and longitude that are not both exactly 0, which is what a capture
    /// profile with no site set writes. Read as unknown like NaN, since nothing else lies at (0, 0) and every frame of a night
    /// far from it would read as taken in twilight.
    /// </summary>
    private static bool SiteKnown(in ImageMeta meta)
        => double.IsFinite(meta.Latitude) && double.IsFinite(meta.Longitude) && !(meta.Latitude == 0 && meta.Longitude == 0);

    /// <summary>
    /// The local evening a frame belongs to: its time less twelve hours at the site's mean solar time (its longitude), so the
    /// frames either side of midnight are one night; the timestamp's own offset where the site is unknown. Default for a frame
    /// that never said when it was taken.
    /// </summary>
    internal static DateOnly NightOf(in ImageMeta meta)
    {
        if (meta.ExposureStartTime == default)
        {
            return default;
        }
        var local = SiteKnown(meta)
            ? meta.ExposureStartTime.UtcDateTime.AddHours(meta.Longitude / 15.0)
            : meta.ExposureStartTime.DateTime;
        return DateOnly.FromDateTime(local.AddHours(-12));
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
        var local = LocalPatchiness(grades, grade);
        var causes = GradeCause.None;
        var shown = relative;
        if (PatchyCloud(patchiness, local))
        {
            causes |= GradeCause.Cloud;
            shown = MathF.Min(shown, patchiness >= HeavyCloudFrom ? PoorBelow : (PoorBelow + GoodFrom) * 0.5f);
        }
        var nightSky = RelativeNightSky(grades, grade);
        if (nightSky >= NightSkyCloudFrom)
        {
            causes |= GradeCause.Cloud;
            shown = MathF.Min(shown, PoorBelow);
        }
        if (grade.Twilight is { } twilight)
        {
            causes |= twilight == TwilightPeriod.Dawn ? GradeCause.Sunrise : GradeCause.Sunset;
            shown = MathF.Min(shown, PoorBelow);
        }
        return new LightGradeVerdict(relative, patchiness, causes, shown, nightSky, local);
    }

    /// <summary>
    /// Whether a patchiness reads as cloud: past <see cref="CloudFrom"/> against the group's clearest AND past
    /// <see cref="LocalCloudFrom"/> against the frames either side of it, since a target's sky grows patchier the lower it is,
    /// and a rising target's first hour read as cloud against its clearest (2026-09-10: 72 of 176 clear frames). Heavy cloud
    /// (<see cref="HeavyCloudFrom"/>) needs no local test, which a cloud bank lasting past the window would pass; nor does a
    /// frame with no time to place it by (a NaN local).
    /// </summary>
    private static bool PatchyCloud(float patchiness, float local)
        => patchiness >= HeavyCloudFrom || (patchiness >= CloudFrom && !(local < LocalCloudFrom));

    /// <summary>The cloud cause in words, saying which measure found it, for the Grade line and the file list's mark alike.</summary>
    public static string CloudInWords(in LightGradeVerdict verdict)
    {
        var patchy = PatchyCloud(verdict.Patchiness, verdict.LocalPatchiness);
        var bright = verdict.NightSky >= NightSkyCloudFrom;
        return (patchy, bright) switch
        {
            (true, true) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Cloud: the sky is {verdict.Patchiness:0.0}x as patchy as the group's clearest and {verdict.NightSky:0.0}x as bright as the night's darkest"),
            (false, true) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Cloud: the sky is {verdict.NightSky:0.0}x as bright as the night's darkest"),
            _ => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Cloud: the sky is {verdict.Patchiness:0.0}x as patchy as the group's clearest"),
        };
    }

    /// <summary>
    /// A frame's sky level as a multiple of its NIGHT's darkest tenth (the 10th percentile of the night's sky levels): every
    /// graded frame of the same night through the same sensor, filter, exposure and binning, whatever its target, twilight
    /// frames left out. A target cloudy from its first frame to its last is invisible against its own group (its clearest tenth
    /// is cloud too) and plain against the night: on 2026-07-06 the owner's LMC run read 1.89 to 3.17 times the night's
    /// darkest, against at most 1.57 for any clear frame. NaN where the frame's sky was not read or the night has nothing to
    /// compare with.
    /// </summary>
    public static float RelativeNightSky(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
    {
        if (!float.IsFinite(grade.SkyLevel) || grade.SkyLevel <= 0f || grade.Night == default)
        {
            return float.NaN;
        }
        return BaselinesOf(grades).NightDarkest.TryGetValue(new NightGroup(grade.Key with { ObjectKey = string.Empty }, grade.Night), out var darkest)
            ? grade.SkyLevel / darkest
            : float.NaN;
    }

    /// <summary>
    /// A frame's patchiness as a multiple of its group's clearest tenth (the 10th percentile of the group's patchiness so far,
    /// the lowest while fewer than ten are graded), so the sky a field always has, a galaxy or a nebula filling the frame,
    /// cancels, and only what changed from frame to frame is left. NaN where the frame's patchiness was not read; 1 while the
    /// group has nothing to compare with. Twilight frames are left out of the baseline: a bright sky is a cause of its own,
    /// and its gradient is not the dark sky the group's patchiness is read against.
    /// </summary>
    public static float RelativePatchiness(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
    {
        if (!float.IsFinite(grade.Patchiness))
        {
            return float.NaN;
        }
        return BaselinesOf(grades).ClearestPatchiness.TryGetValue(grade.Key, out var clearest) && clearest > 0f
            ? grade.Patchiness / clearest
            : 1f;
    }

    /// <summary>
    /// A frame's patchiness as a multiple of the 20th percentile of its group's patchiness over the frames taken within
    /// <see cref="LocalWindow"/> of it, itself included. Cloud comes and goes over minutes, while a target's sky grows less
    /// patchy over hours as it climbs (the 2026-09-10 Helix fell from 1.73 to 1.29 times its clearest as it rose from 47 to 64
    /// degrees), so a frame patchier than its neighbours is cloud and one as patchy as its neighbours is its altitude. NaN where
    /// the frame's patchiness or time was not read; twilight frames are left out of the neighbours as of the clearest tenth.
    /// </summary>
    public static float LocalPatchiness(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
        => float.IsFinite(grade.Patchiness) && grade.Taken != default
            && BaselinesOf(grades).LocalFloor.TryGetValue(new TimedGroup(grade.Key, grade.Taken), out var floor) && floor > 0f
            ? grade.Patchiness / floor
            : float.NaN;

    /// <summary>The best score among the graded frames of <paramref name="key"/>'s group, or 0 when none is graded.</summary>
    public static float BestScore(ImmutableDictionary<string, LightGrade> grades, LightGradeKey key)
        => BaselinesOf(grades).BestScore.GetValueOrDefault(key);

    /// <summary>A graded frame's score as a share of the best of its group seen so far, in (0, 1].</summary>
    public static float Relative(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
    {
        var best = BestScore(grades, grade.Key);
        return best > 0f ? Math.Clamp(grade.Score / best, 0f, 1f) : 1f;
    }

    private readonly record struct NightGroup(LightGradeKey Key, DateOnly Night);

    private readonly record struct TimedGroup(LightGradeKey Key, DateTimeOffset Taken);

    /// <summary>
    /// What every verdict of one grade table is read against: each group's best score and clearest patchiness, each night's
    /// darkest sky, each frame's local patchiness floor. Built in one pass over the table, ONCE per snapshot (<see cref="BaselinesOf"/>), so a verdict is a lookup and a
    /// repaint of a long file list stays O(rows), where each row used to scan the table three times and sort it twice.
    /// </summary>
    private sealed class Baselines(ImmutableDictionary<string, LightGrade> grades)
    {
        public Dictionary<LightGradeKey, float> BestScore { get; } = [];
        public Dictionary<LightGradeKey, float> ClearestPatchiness { get; } = [];
        public Dictionary<NightGroup, float> NightDarkest { get; } = [];
        public Dictionary<TimedGroup, float> LocalFloor { get; } = [];

        public Baselines Build()
        {
            Dictionary<LightGradeKey, List<float>> patchiness = [];
            Dictionary<NightGroup, List<float>> sky = [];
            Dictionary<LightGradeKey, List<(DateTimeOffset Taken, float Patchiness)>> timed = [];
            foreach (var grade in grades.Values)
            {
                if (grade.Score > BestScore.GetValueOrDefault(grade.Key))
                {
                    BestScore[grade.Key] = grade.Score;
                }
                if (grade.Twilight is not null)
                {
                    continue;
                }
                if (float.IsFinite(grade.Patchiness))
                {
                    Add(patchiness, grade.Key, grade.Patchiness);
                    if (grade.Taken != default)
                    {
                        Add(timed, grade.Key, (grade.Taken, grade.Patchiness));
                    }
                }
                if (float.IsFinite(grade.SkyLevel) && grade.SkyLevel > 0f && grade.Night != default)
                {
                    Add(sky, new NightGroup(grade.Key with { ObjectKey = string.Empty }, grade.Night), grade.SkyLevel);
                }
            }
            foreach (var (key, values) in patchiness)
            {
                ClearestPatchiness[key] = Tenth(values);
            }
            foreach (var (key, values) in sky)
            {
                NightDarkest[key] = Tenth(values);
            }
            foreach (var (key, frames) in timed)
            {
                AddLocalFloors(key, frames);
            }
            return this;
        }

        // A window sliding over the group's frames in time order: O(n w log w) for w frames in a window, never a scan of the
        // group per frame.
        private void AddLocalFloors(LightGradeKey key, List<(DateTimeOffset Taken, float Patchiness)> frames)
        {
            frames.Sort(static (a, b) => a.Taken.CompareTo(b.Taken));
            List<float> window = [];
            var start = 0;
            var end = 0;
            foreach (var (taken, _) in frames)
            {
                while (frames[start].Taken < taken - LocalWindow)
                {
                    start++;
                }
                while (end < frames.Count && frames[end].Taken <= taken + LocalWindow)
                {
                    end++;
                }
                window.Clear();
                for (var i = start; i < end; i++)
                {
                    window.Add(frames[i].Patchiness);
                }
                window.Sort();
                LocalFloor[new TimedGroup(key, taken)] = window[window.Count / 5];
            }
        }

        private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> into, TKey key, TValue value) where TKey : notnull
        {
            if (!into.TryGetValue(key, out var list))
            {
                into[key] = list = [];
            }
            list.Add(value);
        }

        private static float Tenth(List<float> values)
        {
            values.Sort();
            return values[values.Count / 10];
        }
    }

    // Keyed on the snapshot's REFERENCE: every write to the table is one swap of an immutable dictionary, so a snapshot's
    // baselines never go stale, and an old snapshot's are collected with it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImmutableDictionary<string, LightGrade>, Baselines> _baselines = new();

    private static Baselines BaselinesOf(ImmutableDictionary<string, LightGrade> grades)
        => _baselines.GetValue(grades, static g => new Baselines(g).Build());

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
    /// the end of a cloudy run at 1.74 to 1.96. Against the owner's frame-by-frame cloud marks of 2026-07-06 (700 frames, the
    /// Frame Cloud Check page), 1.3 flagged no clear frame of 588 and caught 52 cloudy of 109, where 1.5 caught 37 and 1.2
    /// flagged its first clear one.
    /// </summary>
    public const float CloudFrom = 1.3f;

    /// <summary>
    /// From this multiple of its night's darkest tenth, a frame's sky reads as cloud and its colour is held at poor
    /// (<see cref="RelativeNightSky"/>). On the owner's marks of 2026-07-06 it caught all 75 frames of a run cloudy from start
    /// to end and flagged no clear frame: the brightest clear sky that night, a target low in the town's glow, read 1.57.
    /// </summary>
    public const float NightSkyCloudFrom = 1.8f;

    /// <summary>
    /// From this multiple of the frames either side of it (<see cref="LocalPatchiness"/>), a patchiness past
    /// <see cref="CloudFrom"/> is cloud rather than altitude. On the owner's BAD_ marks of 2026-08-31, 09-10 and 09-12 (nights the
    /// rest of the grade was not tuned on) and the cloud marks of 07-06, it took the clear frames flagged from 10, 73, 14 and 0
    /// to 0, 11, 4 and 0, for 3 of 42 catches on 09-10 and 3 of 52 on 07-06, where 1.15 left 50 flagged on 09-10.
    /// </summary>
    public const float LocalCloudFrom = 1.2f;

    /// <summary>How far either side of a frame its neighbours are taken from: shorter than a target's climb, longer than a cloud.</summary>
    public static readonly TimeSpan LocalWindow = TimeSpan.FromMinutes(60);

    /// <summary>From this multiple, the cloud holds the colour at poor.</summary>
    public const float HeavyCloudFrom = 4f;

    /// <summary>How much of the grade colour a row's background takes over the panel.</summary>
    private const float RowTintShare = 0.35f;
}
