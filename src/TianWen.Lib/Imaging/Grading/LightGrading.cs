using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging.Stacking;
using TwilightPeriod = TianWen.Lib.Sequencing.TwilightPeriod;

namespace TianWen.Lib.Imaging.Grading;

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
/// One frame's quality: <see cref="FrameRegistration.ReferenceScore"/> (many, sharp and round together) over the stars found on it
/// that clear <see cref="ScoredSnrFloor"/> (<see cref="ScoredStars"/>), whoever grades it: the viewer, the stacker's cloud filter or the
/// live judge. The stacker's own reference choice still counts every star (#2011). Beside it, the two
/// things that score cannot see (#1951): how patchy the sky is (<see cref="Image.SkyPatchiness"/>, NaN on a frame too small
/// to read it), which cloud raises, and which twilight the frame was taken in, null in a dark sky or where the frame does
/// not say when and where it was taken. <see cref="SkyLevel"/> is the star-masked sky median in the frame's own counts (ADU,
/// NaN without a star mask) and <see cref="Night"/> the local evening it belongs to, which together read a sky BRIGHT against
/// the rest of its night: cloud lit from below over a whole target, which a group-relative measure cannot see. <see cref="Taken"/>
/// is when the exposure started, default where the frame does not say, which places its patchiness against the frames either
/// side of it (<see cref="LightGrading.LocalPatchiness"/>). <see cref="Rig"/> names what else a sky level in counts scales with (gain,
/// offset, instrument and telescope), which the star-score group (<see cref="Key"/>) does not carry: the night's darkest is read
/// per rig, so two rigs or two gains of one night are never compared with each other.
/// </summary>
public readonly record struct LightGrade(LightGradeKey Key, FrameMetrics Metrics, float Score, float Patchiness = float.NaN,
    TwilightPeriod? Twilight = null, float SkyLevel = float.NaN, DateOnly Night = default, DateTimeOffset Taken = default, string Rig = "",
    double SiteLatitude = double.NaN, double SiteLongitude = double.NaN);

/// <summary>What a rig's clearest tenth of patchiness read on one clear night, for one object.</summary>
public readonly record struct LightGradeObjectClearest(string ObjectKey, float Clearest);

/// <summary>
/// A clear night of one rig (#1976): what a run's own baselines are checked against, so a run that is cloud from first frame to
/// last cannot be its own clear reference. A plain record, to be stored and read back as JSON, so it ends on a non-nullable value.
/// <see cref="Key"/> is compared without its object (the sky level in counts is the rig's, not the target's) and the site is
/// rounded to 0.1 degree (<see cref="LightGrading.RoundSite"/>), NaN where unknown. <see cref="NightDarkestSky"/> is the night's
/// darkest tenth of sky level in counts and <see cref="Objects"/> each object's clearest patchiness, where known.
/// </summary>
public readonly record struct LightGradeReference(LightGradeKey Key, string Rig, double SiteLatitude, double SiteLongitude, DateOnly Night,
    ImmutableArray<LightGradeObjectClearest> Objects, float NightDarkestSky);

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
/// sharp frame through cloud or in twilight cannot read good. <see cref="ReferenceNight"/> is the night of the rig's clear reference
/// the cloud was judged against because the run's own baselines were implausible (default when they were its own), and
/// <see cref="CloudUnjudged"/> says references were asked and the rig has none, so cloud is judged on the run's own baselines alone (the colour is unchanged).
/// </summary>
public readonly record struct LightGradeVerdict(float Relative, float Patchiness, GradeCause Causes, float Shown, float NightSky = float.NaN,
    float LocalPatchiness = float.NaN, DateOnly ReferenceNight = default, bool CloudUnjudged = false, float LocalNoise = float.NaN,
    float StarRatio = float.NaN, bool InCloudRun = false, float SkyOverTrend = float.NaN, bool SkyRising = false);

/// <summary>
/// Grades the frames of a folder as the viewer loads them, each RELATIVE to the best frame of its group seen so far:
/// a grade of 1 is the best yet, 0.5 a frame scoring half of it. Relative because an absolute score means nothing
/// across fields, optics and nights, while the question a blink asks is which of THESE subs are the bad ones.
/// In the Lib so the viewer, the stacker and the node read ONE verdict (#1981); only the palette colours a grade is drawn in
/// stay with the UI (<c>LightGradeColours</c>). A caller outside the viewer grades a frame BEFORE background normalization,
/// which erases the night-sky term.
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
    /// <c>AstroImageDocument.DetectStarsAsync</c>'s <c>whileResident</c>. Called on an evicted image it restores the
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

        var metrics = FrameRegistration.MetricsFrom(ScoredStars(stars));
        var score = FrameRegistration.ReferenceScore(metrics);
        if (!float.IsFinite(score) || score <= 0f)
        {
            return null;
        }
        return new LightGrade(LightGradeKey.Of(image), metrics, score, image.SkyPatchiness(image.ReferenceStarChannel) * (fullScaleAdu ?? 1f), twilight,
            SkyLevelOf(image, stars, fullScaleAdu), NightOf(meta), meta.ExposureStartTime, RigOf(meta),
            SiteKnown(meta) ? RoundSite(meta.Latitude) : double.NaN, SiteKnown(meta) ? RoundSite(meta.Longitude) : double.NaN);
    }

    // The patchiness is read in the plane's own value space, so it is put back into counts by the same scale as the sky level
    // (above): left in each frame's own peak units, two frames of one sky read a different patchiness by their peak ratio.
    /// <summary>
    /// The star-masked sky median in the frame's own counts (ADU): the median itself, not above the frame's darkest pixel, in the
    /// file's counts. The viewer grades the image <c>AstroImageDocument.AdoptImageAsync</c> already scaled to [0, 1] by
    /// the frame's own observed peak, which differs from frame to frame, so <paramref name="fullScaleAdu"/> (the count a sample
    /// of 1 stood for, <c>AstroImageDocument.FullScaleAdu</c>) puts it back; null for an image still in counts or
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
    /// The SNR a star needs to count towards a frame's score. Detection is a ladder that lowers its threshold until it has
    /// <c>maxStars</c>, so how DEEP a frame is read depends on whether its first pass fell short: on 2026-09-10 one clear frame's
    /// first pass accepted 1,911 stars, retried at 7 sigma and ended with 4,346, while its neighbour a minute later accepted 2,056,
    /// stopped and ended with 1,737, and the score (stars over HFD) made the few frames that retried the night's best and every
    /// other clear frame 0.4 of it. Above 30 the two neighbours agree to 1 % (1,343 against 1,357, both passes hold every such
    /// star), and a frame through cloud still loses its share (1,001).
    /// </summary>
    public const float ScoredSnrFloor = 30f;

    /// <summary>
    /// The stars a frame is scored on: those over <see cref="ScoredSnrFloor"/>, and when fewer than <see cref="MinScoredStars"/>
    /// clear it, the <see cref="MinScoredStars"/> brightest by SNR (all of them on a field with fewer). Never ALL its stars once a
    /// few clear the bar: that fallback was per frame and not continuous, so a frame with 19 strong stars and 280 faint ones scored
    /// on 299 and one with 21 strong ones on 21, and the first became the group's yardstick.
    /// </summary>
    internal static StarList ScoredStars(StarList stars)
    {
        var scored = new System.Collections.Concurrent.ConcurrentBag<ImagedStar>();
        foreach (var star in stars)
        {
            if (star.SNR >= ScoredSnrFloor)
            {
                scored.Add(star);
            }
        }
        if (scored.Count >= MinScoredStars || stars.Count <= MinScoredStars)
        {
            return scored.Count >= MinScoredStars ? new StarList(scored) : stars;
        }
        var brightest = new System.Collections.Concurrent.ConcurrentBag<ImagedStar>();
        foreach (var star in stars.OrderByDescending(static s => s.SNR).Take(MinScoredStars))
        {
            brightest.Add(star);
        }
        return new StarList(brightest);
    }

    /// <summary>The fewest stars a frame is scored on: below it the brightest are taken, never every faint one.</summary>
    internal const int MinScoredStars = 20;

    /// <summary>
    /// The sky level of a RAW light in its own counts, for a caller that grades the frame after calibration (the stacker, #2003): the
    /// viewer grades the raw file, records its clear-night references from raw frames, and <see cref="NightSkyCloudFrom"/> was measured
    /// on raw levels, while a calibrated sky has the bias and dark taken out (2026-09-10, QHY294C, 60 s: 3300 raw against 1996
    /// calibrated at the night's darkest), which would read every ratio against the darkest high and every ratio against a reference
    /// low. Read before calibration, which consumes the frame, so before its stars are known: the median over the whole frame (the
    /// green photosites of a mosaic), which a field's stars move little, beside the viewer's star-masked one.
    /// </summary>
    public static float RawSkyLevel(Image raw)
    {
        var (_, median, _) = raw.IsCfaMosaic
            ? raw.GetPedestralMedianAndMADScaledToUnit(0, cfa: CfaChannel.Green)
            : raw.GetPedestralMedianAndMADScaledToUnit(raw.ReferenceStarChannel);
        var divisor = raw.HasUnitScalePeak ? 1f : raw.MaxValue;
        return median * divisor + raw.MinValue;
    }

    /// <summary>
    /// Whether the header names a site: a finite latitude and longitude that are not both exactly 0, which is what a capture
    /// profile with no site set writes. Read as unknown like NaN, since nothing else lies at (0, 0) and every frame of a night
    /// far from it would read as taken in twilight.
    /// </summary>
    private static bool SiteKnown(in ImageMeta meta)
        => double.IsFinite(meta.Latitude) && double.IsFinite(meta.Longitude) && !(meta.Latitude == 0 && meta.Longitude == 0);

    /// <summary>A site coordinate to 0.1 degree (about 11 km), what a reference's site is matched on.</summary>
    public static double RoundSite(double degrees) => Math.Round(degrees, 1);

    /// <summary>
    /// What a sky level in counts scales with beyond the group's key: gain, offset, ISO (a camera's gain), instrument and telescope. Part of the night's
    /// group (<see cref="NightGroup"/>), not of <see cref="LightGradeKey"/>, which stays the star score's group.
    /// </summary>
    internal static string RigOf(in ImageMeta meta)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{meta.Gain}|{meta.Offset}|{meta.Iso}|{meta.Instrument?.Trim().ToUpperInvariant()}|{meta.Telescope?.Trim().ToUpperInvariant()}");

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
    public static LightGradeVerdict Verdict(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade,
        ImmutableArray<LightGradeReference> references = default)
    {
        var relative = Relative(grades, grade);
        var patchiness = RelativePatchiness(grades, grade);
        var local = LocalPatchiness(grades, grade);
        var localNoise = LocalPatchinessNoise(grades, grade);
        var starRatio = LocalStarRatio(grades, grade);
        var inRun = InCloudRun(grades, grade);
        var skyOverTrend = SkyOverTrend(grades, grade);
        var skyRising = SkyRising(grades, grade);
        var causes = GradeCause.None;
        var shown = relative;
        var nightSky = RelativeNightSky(grades, grade);
        DateOnly referenceNight = default;
        var unjudged = false;
        if (!references.IsDefault && grade.Twilight is null)
        {
            (patchiness, local, nightSky, referenceNight, unjudged) = AgainstReferences(grades, grade, references, patchiness, local, nightSky);
        }
        // A frame in twilight has a bright sky of its own cause, the Sun: neither cloud term judges it (its patchiness and sky
        // level are left out of the baselines the same way), or one dawn frame would read Cloud as well as Sunrise.
        if (grade.Twilight is { } twilight)
        {
            causes |= twilight == TwilightPeriod.Dawn ? GradeCause.Sunrise : GradeCause.Sunset;
            shown = MathF.Min(shown, PoorBelow);
        }
        else
        {
            if (PatchyCloud(patchiness, local, localNoise, starRatio) || ((inRun || skyRising) && referenceNight == default))
            {
                causes |= GradeCause.Cloud;
                shown = MathF.Min(shown, patchiness >= HeavyCloudFrom ? PoorBelow : (PoorBelow + GoodFrom) * 0.5f);
            }
            if (nightSky >= NightSkyCloudFrom)
            {
                causes |= GradeCause.Cloud;
                shown = MathF.Min(shown, PoorBelow);
            }
        }
        return new LightGradeVerdict(relative, patchiness, causes, shown, nightSky, local, referenceNight, unjudged, localNoise, starRatio,
            inRun && referenceNight == default, skyOverTrend, skyRising && referenceNight == default);
    }

    /// <summary>
    /// A run is not its own clear baseline (#1976). The group's night darkest tenth of sky and clearest tenth of patchiness are a
    /// run's own frames, so a run cloudy from first frame to last (the 2026-07-06 LMC, with only its folder open) reads clear against
    /// itself. When the darkest tenth sits <see cref="NightSkyCloudFrom"/> or more above the most recent OTHER night's clear
    /// reference of the same rig and site, and the clearest patchiness <see cref="CloudFrom"/> or more above that reference's for the
    /// same object (where it knows the object), the frame is judged against the reference instead, with no local test (a local floor
    /// of the same cloudy run is no floor). With no reference for the rig the run is judged for cloud on its own baselines alone, and says so; nothing
    /// else about the verdict changes. A run whose baselines are plausible keeps its numbers exactly.
    /// </summary>
    private static (float Patchiness, float Local, float NightSky, DateOnly ReferenceNight, bool Unjudged) AgainstReferences(
        ImmutableDictionary<string, LightGrade> grades, in LightGrade grade, ImmutableArray<LightGradeReference> references,
        float patchiness, float local, float nightSky)
    {
        var readable = (float.IsFinite(grade.SkyLevel) && grade.SkyLevel > 0f) || float.IsFinite(grade.Patchiness);
        if (!readable)
        {
            return (patchiness, local, nightSky, default, false);
        }
        var groupKey = grade.Key with { ObjectKey = string.Empty };
        LightGradeReference? best = null;
        foreach (var reference in references)
        {
            if (reference.Key with { ObjectKey = string.Empty } == groupKey && reference.Rig == grade.Rig
                && SameSite(reference.SiteLatitude, grade.SiteLatitude) && SameSite(reference.SiteLongitude, grade.SiteLongitude)
                && reference.Night != grade.Night && reference.NightDarkestSky > 0f
                && (best is null || reference.Night > best.Value.Night))
            {
                best = reference;
            }
        }
        if (best is not { } chosen)
        {
            return (patchiness, local, nightSky, default, true);
        }

        var baselines = BaselinesOf(grades);
        if (grade.Night == default || !float.IsFinite(grade.SkyLevel) || grade.SkyLevel <= 0f
            || !baselines.NightDarkest.TryGetValue(new NightGroup(groupKey, grade.Rig, grade.Night), out var darkest)
            || darkest / chosen.NightDarkestSky < NightSkyCloudFrom)
        {
            return (patchiness, local, nightSky, default, false);
        }
        var referenceClearest = 0f;
        foreach (var o in chosen.Objects.IsDefault ? [] : chosen.Objects)
        {
            if (string.Equals(o.ObjectKey, grade.Key.ObjectKey, StringComparison.Ordinal) && o.Clearest > 0f)
            {
                referenceClearest = o.Clearest;
            }
        }
        var patchinessKnown = referenceClearest > 0f && float.IsFinite(grade.Patchiness);
        if (patchinessKnown
            && !(baselines.ClearestPatchiness.TryGetValue(new RigGroup(grade.Key, grade.Rig), out var clearest) && clearest / referenceClearest >= CloudFrom))
        {
            return (patchiness, local, nightSky, default, false);
        }
        return (patchinessKnown ? grade.Patchiness / referenceClearest : patchiness, float.NaN, grade.SkyLevel / chosen.NightDarkestSky, chosen.Night, false);
    }

    /// <summary>The fewest clear frames of one night and rig that make a reference of it (#1976).</summary>
    public const int MinClearFramesPerReference = 10;

    /// <summary>The most recent clear nights kept per rig and site; an older one is dropped when a newer is added.</summary>
    public const int MaxReferenceNightsPerRig = 5;

    /// <summary>The fewest clear frames of one object that give a night a clearest patchiness for it.</summary>
    private const int MinClearFramesPerObject = 3;

    /// <summary>
    /// The nights in <paramref name="grades"/> clear enough to be a rig's reference (#1976), each as the reference its CLEAR frames
    /// make. A frame counts only when its verdict, read through <paramref name="references"/>, carries no cloud and was judged on
    /// the run's own baselines (a frame taken in twilight never counts); a night is a reference only when it has at least
    /// <see cref="MinClearFramesPerReference"/> such frames and NONE of its frames was judged against another night's reference,
    /// since a night that needed one is not itself clear. Where the rig has no reference from another night to check against, the
    /// night must also look clear on its own terms: its median sky within <see cref="NightSkyCloudFrom"/> of its darkest tenth, and
    /// at least half its frames clear (a night that clears and then clouds over is evidence its clear stretch is clear: 2026-09-10
    /// had two clear hours and cloud after midnight, and the rule that demanded no cloud at all left that rig with no reference
    /// ever). A uniform overcast cannot be told from a clear night by one night alone, so the first reference
    /// of a rig can be wrong; it is replaced as soon as a later clear night of the rig is graded, since the most recent other
    /// night wins. The sky level is the clear frames' darkest tenth, and each object's clearest patchiness their tenth for it.
    /// </summary>
    public static ImmutableArray<LightGradeReference> ClearNights(ImmutableDictionary<string, LightGrade> grades,
        ImmutableArray<LightGradeReference> references = default)
    {
        Dictionary<NightGroup, (List<LightGrade> Clear, int Frames, bool Needed)> nights = [];
        foreach (var grade in grades.Values)
        {
            if (grade.Twilight is not null || grade.Night == default || !float.IsFinite(grade.SkyLevel) || grade.SkyLevel <= 0f)
            {
                continue;
            }
            var group = new NightGroup(grade.Key with { ObjectKey = string.Empty }, grade.Rig, grade.Night);
            var (clear, frames, needed) = nights.GetValueOrDefault(group, ([], 0, false));
            var verdict = Verdict(grades, grade, references);
            if (verdict.ReferenceNight != default)
            {
                needed = true;
            }
            else if ((verdict.Causes & GradeCause.Cloud) == 0)
            {
                clear.Add(grade);
            }
            nights[group] = (clear, frames + 1, needed);
        }

        var made = ImmutableArray.CreateBuilder<LightGradeReference>();
        foreach (var (group, (clear, frames, needed)) in nights)
        {
            if (needed || clear.Count < MinClearFramesPerReference)
            {
                continue;
            }
            var site = (clear[0].SiteLatitude, clear[0].SiteLongitude);
            var checkedAgainstAnother = false;
            if (!references.IsDefault)
            {
                foreach (var reference in references)
                {
                    if (reference.Key with { ObjectKey = string.Empty } == group.Key && reference.Rig == group.Rig && reference.Night != group.Night
                        && reference.NightDarkestSky > 0f && SameSite(reference.SiteLatitude, site.SiteLatitude) && SameSite(reference.SiteLongitude, site.SiteLongitude))
                    {
                        checkedAgainstAnother = true;
                        break;
                    }
                }
            }
            List<float> sky = [.. clear.Select(static g => g.SkyLevel)];
            sky.Sort();
            if (!checkedAgainstAnother && (clear.Count * 2 < frames || sky[sky.Count / 2] / sky[sky.Count / 10] >= NightSkyCloudFrom))
            {
                continue;
            }

            Dictionary<string, List<float>> byObject = [];
            foreach (var grade in clear)
            {
                if (float.IsFinite(grade.Patchiness) && grade.Patchiness > 0f)
                {
                    if (!byObject.TryGetValue(grade.Key.ObjectKey, out var list))
                    {
                        byObject[grade.Key.ObjectKey] = list = [];
                    }
                    list.Add(grade.Patchiness);
                }
            }
            var objects = ImmutableArray.CreateBuilder<LightGradeObjectClearest>();
            foreach (var (objectKey, values) in byObject.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                if (values.Count >= MinClearFramesPerObject)
                {
                    values.Sort();
                    objects.Add(new LightGradeObjectClearest(objectKey, values[values.Count / 10]));
                }
            }
            made.Add(new LightGradeReference(group.Key, group.Rig, RoundSite(site.SiteLatitude), RoundSite(site.SiteLongitude), group.Night,
                objects.ToImmutable(), sky[sky.Count / 10]));
        }
        return made.ToImmutable();
    }

    /// <summary>
    /// <paramref name="added"/> over <paramref name="existing"/>: a night of the same rig and site is replaced, and each rig and site
    /// keeps only its <see cref="MaxReferenceNightsPerRig"/> most recent nights. Ordered by rig, then newest night first.
    /// </summary>
    public static ImmutableArray<LightGradeReference> MergeReferences(IEnumerable<LightGradeReference> existing, IEnumerable<LightGradeReference> added)
    {
        static (LightGradeKey, string, double, double) RigOfReference(LightGradeReference r)
            => (r.Key with { ObjectKey = string.Empty }, r.Rig, r.SiteLatitude, r.SiteLongitude);
        var merged = new Dictionary<((LightGradeKey, string, double, double), DateOnly), LightGradeReference>();
        foreach (var reference in existing.Concat(added))
        {
            merged[(RigOfReference(reference), reference.Night)] = reference with { Key = reference.Key with { ObjectKey = string.Empty } };
        }
        return [.. merged.Values
            .GroupBy(static r => (r.Key, r.Rig, r.SiteLatitude, r.SiteLongitude))
            .SelectMany(static g => g.OrderByDescending(static r => r.Night).Take(MaxReferenceNightsPerRig))
            .OrderBy(static r => r.Rig, StringComparer.Ordinal).ThenByDescending(static r => r.Night)];
    }

    private static bool SameSite(double a, double b)
        => double.IsNaN(a) ? double.IsNaN(b) : !double.IsNaN(b) && Math.Abs(a - b) < 0.05;

    /// <summary>
    /// Whether a patchiness reads as cloud: past <see cref="CloudFrom"/> against the group's clearest AND past
    /// <see cref="LocalCloudFrom"/> against the frames either side of it, since a target's sky grows patchier the lower it is,
    /// and a rising target's first hour read as cloud against its clearest (2026-09-10: 72 of 176 clear frames). Heavy cloud
    /// (<see cref="HeavyCloudFrom"/>) needs no local test, which a cloud bank lasting past the window would pass; nor does a
    /// frame with no time to place it by (a NaN local).
    /// <para>Past <see cref="LocalCloudFrom"/> by no more than its neighbours' own frame-to-frame jitter (<see cref="LocalNoiseSigmas"/>
    /// times <paramref name="localNoise"/>) a frame needs a SECOND sign, starlight lost against its neighbours
    /// (<paramref name="starRatio"/> under <see cref="StarLossFrom"/>): on 2026-09-10 nine clear frames sat 1.20 to 1.24 against their
    /// floor, a percent from frames read clear, their stars at 0.995 of their neighbours', while the owner's cloud frames lost a third
    /// of theirs (median 0.67). The rule took the unmarked frames flagged from 9 to 0 and the owner's 44 marks caught from 40 to 39.</para>
    /// </summary>
    internal static bool PatchyCloud(float patchiness, float local, float localNoise = float.NaN, float starRatio = float.NaN)
    {
        if (patchiness >= HeavyCloudFrom)
        {
            return true;
        }
        if (!(patchiness >= CloudFrom))
        {
            return false;
        }
        if (float.IsNaN(local))
        {
            return true;
        }
        var bar = LocalCloudFrom * (1f + LocalNoiseSigmas * (float.IsFinite(localNoise) ? localNoise : 0f));
        return local >= bar || (local >= LocalCloudFrom && starRatio < StarLossFrom);
    }

    /// <summary>How many of its neighbours' frame-to-frame jitters past <see cref="LocalCloudFrom"/> a frame must stand to be cloud alone.</summary>
    public const float LocalNoiseSigmas = 3f;

    /// <summary>The share of its neighbours' stars (over <see cref="ScoredSnrFloor"/>) under which a frame has lost starlight, a second sign of cloud.</summary>
    public const float StarLossFrom = 0.9f;

    /// <summary>The cloud cause in words, saying which measure found it, for the Grade line and the file list's mark alike.</summary>
    public static string CloudInWords(in LightGradeVerdict verdict)
    {
        // A frame that joined a run frame by frame is past the plain patchiness bar; one the bracket filled need not be, so it is
        // worded by the cloud around it, never by a patchiness that reads clear.
        var patchy = PatchyCloud(verdict.Patchiness, verdict.LocalPatchiness, verdict.LocalNoise, verdict.StarRatio)
            || (verdict.InCloudRun && verdict.Patchiness >= CloudFrom);
        var bright = verdict.NightSky >= NightSkyCloudFrom;
        var words = (patchy, bright) switch
        {
            (false, false) when verdict.SkyRising => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Cloud: the sky is {(verdict.SkyOverTrend - 1f) * 100f:0}% brighter than its trend over the hour before"),
            (false, false) when verdict.InCloudRun => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Cloud: between cloud frames of this group at most {CloudBracketSpan.TotalMinutes:0} minutes apart"),
            (true, true) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Cloud: the sky is {verdict.Patchiness:0.0}x as patchy as the group's clearest and {verdict.NightSky:0.0}x as bright as the night's darkest"),
            (false, true) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Cloud: the sky is {verdict.NightSky:0.0}x as bright as the night's darkest"),
            _ => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Cloud: the sky is {verdict.Patchiness:0.0}x as patchy as the group's clearest"),
        };
        return verdict.ReferenceNight == default
            ? words
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{words}; no clear frame in this run: judged against {verdict.ReferenceNight:yyyy-MM-dd}");
    }

    /// <summary>The state of a run whose rig has no clear reference, for the Grade line and a cloud mark's tooltip alike.</summary>
    public const string CloudUnjudgedInWords = "Cloud judged on this night alone: no clear night of this rig on file yet";

    /// <summary>
    /// A frame's sky level as a multiple of its NIGHT's darkest tenth (the 10th percentile of the night's sky levels): every
    /// graded frame of the same night through the same rig (sensor, gain, offset, ISO, instrument, telescope), filter, exposure and
    /// binning, whatever its target, twilight
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
        return BaselinesOf(grades).NightDarkest.TryGetValue(new NightGroup(grade.Key with { ObjectKey = string.Empty }, grade.Rig, grade.Night), out var darkest)
            ? grade.SkyLevel / darkest
            : float.NaN;
    }

    /// <summary>
    /// A frame's patchiness as a multiple of its group's (and rig's: gain, ISO, instrument, telescope) clearest tenth (the 10th percentile of the group's patchiness so far,
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
        return BaselinesOf(grades).ClearestPatchiness.TryGetValue(new RigGroup(grade.Key, grade.Rig), out var clearest) && clearest > 0f
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
            && BaselinesOf(grades).LocalFloor.TryGetValue(new TimedGroup(new RigGroup(grade.Key, grade.Rig), grade.Taken), out var floor) && floor > 0f
            ? grade.Patchiness / floor
            : float.NaN;

    /// <summary>
    /// The frame-to-frame jitter of <paramref name="grade"/>'s neighbours' patchiness as a share of its floor (successive differences,
    /// so a target's smooth climb does not count), NaN with too few neighbours or no time.
    /// </summary>
    public static float LocalPatchinessNoise(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
        => grade.Taken != default && BaselinesOf(grades).LocalNoise.TryGetValue(new TimedGroup(new RigGroup(grade.Key, grade.Rig), grade.Taken), out var noise)
            ? noise : float.NaN;

    /// <summary>
    /// Whether <paramref name="grade"/> belongs to a run of cloud (the owner's 'backtracking', 2026-11-05): a frame confirmed by
    /// <see cref="PatchyCloud"/> starts it, and consecutive frames, each within <see cref="CloudRunGap"/> of the last, still past the
    /// plain bars (<see cref="CloudFrom"/> against the clearest, <see cref="LocalCloudFrom"/> against the floor) join it either way.
    /// Cloud creeps in and fades out around its peak, and inside a bank a frame's neighbours are cloudy too, so neither the margin
    /// nor a star loss against them can confirm it (2026-09-11 01:15:11, the owner's mark: 1.25 against its floor, between 01:14:10
    /// at 1.33 and 01:20:29 at 1.48). A marginal frame with no confirmed cloud beside it (the evening's flicker) stays clear.
    /// </summary>
    public static bool InCloudRun(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
        => grade.Taken != default && BaselinesOf(grades).CloudRun.Contains(new TimedGroup(new RigGroup(grade.Key, grade.Rig), grade.Taken));

    /// <summary>
    /// A frame's sky level as a multiple of the line its group's sky followed over the <see cref="LocalWindow"/> before it (frames
    /// not already called cloud, at least <see cref="SkyTrendMinFrames"/> of them), NaN where it cannot be read. A target's sky
    /// moves smoothly all night as it climbs or sets, so a sky that turns against its own line is something else: on 2026-09-12 the
    /// second target's sky fell from 1.39 to 1.12 times the night's darkest over three hours, then turned at 03:55 and climbed to
    /// 1.21 by 04:37, the owner's end-of-night cloud marks, with the Moon down (new on 09-11) and the Sun still below -18 degrees.
    /// <para>It sees a CHANGE, so a haze that stays is caught for its first hour or so only: a risen frame leaves every later frame's
    /// line, and once too few clear frames remain in the window the frame is not read (at least <see cref="SkyTrendMinFrames"/>, or
    /// <see cref="SkyTrendMinFramesOverSpan"/> spanning <see cref="SkyTrendMinSpan"/>). The reach therefore depends on the cadence: a
    /// +6 % step held for two hours reads cloud for its first 51 minutes at 1 minute subs, and for roughly the window less the
    /// span the fit needs at longer ones. Deliberately: a line latched at the last clear frame would call any
    /// lasting change cloud for the rest of the night, and one that re-admits risen frames would absorb a slow haze. A sky that stays
    /// bright for good is the night-sky term's (<see cref="NightSkyCloudFrom"/>) or a reference's (#1976).</para>
    /// </summary>
    public static float SkyOverTrend(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
        => grade.Taken != default && BaselinesOf(grades).SkyTrend.TryGetValue(new TimedGroup(new RigGroup(grade.Key, grade.Rig), grade.Taken), out var ratio)
            ? ratio : float.NaN;

    /// <summary>Whether <paramref name="grade"/>'s sky stands above its trend (<see cref="SkyOverTrend"/>) by at least
    /// <see cref="SkyRiseFrom"/> and by <see cref="SkyRiseSigmas"/> times the trend's own scatter, which the baselines then call cloud.</summary>
    public static bool SkyRising(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
        => grade.Taken != default && BaselinesOf(grades).SkyRise.Contains(new TimedGroup(new RigGroup(grade.Key, grade.Rig), grade.Taken));

    /// <summary>
    /// The least rise of a frame's sky over its trend that reads as cloud. Measured on the owner's marks (2026-11-06): at 4 % with
    /// <see cref="SkyRiseSigmas"/> 6 neither 2026-09-10 nor 2026-09-12 flagged an unmarked frame by it; at 2 % 2026-09-10 flagged
    /// eight frames before midnight (1.025 to 1.063 over trend) the owner left clear.
    /// </summary>
    public const float SkyRiseFrom = 0.04f;

    /// <summary>How many times the trend's own residual scatter (MAD) a frame's sky must stand above it to read as cloud.</summary>
    public const float SkyRiseSigmas = 6f;

    /// <summary>The fewest earlier frames a sky trend is fitted to.</summary>
    public const int SkyTrendMinFrames = 10;

    /// <summary>The fewest earlier frames a sky trend is fitted to when they span at least <see cref="SkyTrendMinSpan"/>: a
    /// count alone made the rule's reach depend on the exposure (60 min less ten frames: 51 min at 1 min subs, 11 frames at 3 min,
    /// 3 at 5 min, none from 6 min up), so long subs would never read. At 1 min cadence ten frames are 9 minutes, so the
    /// count path decides there and the measured nights (2026-09-10, 09-12) are read exactly as before.</summary>
    public const int SkyTrendMinFramesOverSpan = 4;

    /// <summary>The least time the earlier frames must span for a fit of fewer than <see cref="SkyTrendMinFrames"/> to be read.</summary>
    public static readonly TimeSpan SkyTrendMinSpan = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The longest span between two cloud frames of one group across which every frame between them is cloud too (the owner's
    /// backtracking, 2026-11-05): broken cloud leaves clear-looking frames inside a bank, and on 2026-09-12 the owner marked all of
    /// 04:06 to 04:37 though its patchiness flickered between 1.0 and 1.76 from frame to frame. Wider than <see cref="CloudRunGap"/>,
    /// which joins frames one at a time and only past the plain bars.
    /// </summary>
    public static readonly TimeSpan CloudBracketSpan = TimeSpan.FromMinutes(15);

    /// <summary>The longest gap between two frames of one cloud run.</summary>
    public static readonly TimeSpan CloudRunGap = TimeSpan.FromMinutes(10);

    /// <summary>A frame's scored stars as a share of its neighbours' median (within <see cref="LocalWindow"/>), NaN where unread.</summary>
    public static float LocalStarRatio(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
        => grade.Taken != default && grade.Metrics.StarCount > 0
            && BaselinesOf(grades).LocalStars.TryGetValue(new TimedGroup(new RigGroup(grade.Key, grade.Rig), grade.Taken), out var stars) && stars > 0f
            ? grade.Metrics.StarCount / stars : float.NaN;

    /// <summary>The best score among the graded frames of <paramref name="key"/>'s group that the run's own baselines call clear
    /// (the plain best where none is), or 0 when none is graded: the yardstick a share is of.</summary>
    public static float BestScore(ImmutableDictionary<string, LightGrade> grades, LightGradeKey key)
        => BaselinesOf(grades).BestScore.GetValueOrDefault(key);

    /// <summary>A graded frame's score as a share of the best of its group seen so far, in (0, 1].</summary>
    public static float Relative(ImmutableDictionary<string, LightGrade> grades, in LightGrade grade)
    {
        var best = BestScore(grades, grade.Key);
        return best > 0f ? Math.Clamp(grade.Score / best, 0f, 1f) : 1f;
    }

    private readonly record struct NightGroup(LightGradeKey Key, string Rig, DateOnly Night);

    private readonly record struct RigGroup(LightGradeKey Key, string Rig);

    private readonly record struct TimedGroup(RigGroup Group, DateTimeOffset Taken);

    /// <summary>
    /// What every verdict of one grade table is read against: each group's best score and clearest patchiness, each night's
    /// darkest sky, each frame's local patchiness floor. Built in one pass over the table, ONCE per snapshot (<see cref="BaselinesOf"/>), so a verdict is a lookup and a
    /// repaint of a long file list stays O(rows), where each row used to scan the table three times and sort it twice.
    /// </summary>
    private sealed class Baselines(ImmutableDictionary<string, LightGrade> grades)
    {
        public Dictionary<LightGradeKey, float> BestScore { get; } = [];
        public Dictionary<RigGroup, float> ClearestPatchiness { get; } = [];
        public Dictionary<NightGroup, float> NightDarkest { get; } = [];
        public Dictionary<TimedGroup, float> LocalFloor { get; } = [];
        public Dictionary<TimedGroup, float> LocalNoise { get; } = [];
        public Dictionary<TimedGroup, float> LocalStars { get; } = [];
        public HashSet<TimedGroup> CloudRun { get; } = [];
        public Dictionary<TimedGroup, float> SkyTrend { get; } = [];
        public HashSet<TimedGroup> SkyRise { get; } = [];
        private readonly Dictionary<TimedGroup, float> _starCount = [];
        private readonly Dictionary<TimedGroup, float> _sky = [];

        public Baselines Build()
        {
            Dictionary<RigGroup, List<float>> patchiness = [];
            Dictionary<NightGroup, List<float>> sky = [];
            Dictionary<RigGroup, List<(DateTimeOffset Taken, float Patchiness)>> timed = [];
            Dictionary<RigGroup, List<(DateTimeOffset Taken, float Patchiness)>> timedStars = [];
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
                    Add(patchiness, new RigGroup(grade.Key, grade.Rig), grade.Patchiness);
                    if (grade.Taken != default)
                    {
                        Add(timed, new RigGroup(grade.Key, grade.Rig), (grade.Taken, grade.Patchiness));
                        Add(timedStars, new RigGroup(grade.Key, grade.Rig), (grade.Taken, (float)grade.Metrics.StarCount));
                        if (float.IsFinite(grade.SkyLevel) && grade.SkyLevel > 0f)
                        {
                            _sky[new TimedGroup(new RigGroup(grade.Key, grade.Rig), grade.Taken)] = grade.SkyLevel;
                        }
                    }
                }
                if (float.IsFinite(grade.SkyLevel) && grade.SkyLevel > 0f && grade.Night != default)
                {
                    Add(sky, new NightGroup(grade.Key with { ObjectKey = string.Empty }, grade.Rig, grade.Night), grade.SkyLevel);
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
            foreach (var (key, frames) in timedStars)
            {
                AddLocalStars(key, frames);
            }
            foreach (var (key, frames) in timed)
            {
                AddCloudRuns(key, frames);
            }

            // The yardstick is the best frame the run's own baselines call CLEAR: thin cloud can take the faint, broad stars away
            // and leave a tighter median, so the best score of 2026-09-10 was one of its cloud frames (BAD_ 01:06:52), and every
            // clear frame read as a share of it. With no clear frame in a group the plain best stands.
            Dictionary<LightGradeKey, float> clearBest = [];
            foreach (var grade in grades.Values)
            {
                if (grade.Twilight is null && !CloudyOnOwnBaselines(grade) && grade.Score > clearBest.GetValueOrDefault(grade.Key))
                {
                    clearBest[grade.Key] = grade.Score;
                }
            }
            foreach (var (key, best) in clearBest)
            {
                BestScore[key] = best;
            }
            return this;
        }

        /// <summary>Whether the run's own baselines call <paramref name="grade"/> cloud, by either term, as <see cref="Verdict"/> reads them.</summary>
        private bool CloudyOnOwnBaselines(in LightGrade grade)
        {
            var rig = new RigGroup(grade.Key, grade.Rig);
            var patchiness = float.IsFinite(grade.Patchiness) && ClearestPatchiness.TryGetValue(rig, out var clearest) && clearest > 0f
                ? grade.Patchiness / clearest : float.NaN;
            var local = float.IsFinite(grade.Patchiness) && grade.Taken != default
                && LocalFloor.TryGetValue(new TimedGroup(rig, grade.Taken), out var floor) && floor > 0f
                ? grade.Patchiness / floor : float.NaN;
            var nightSky = grade.Night != default && float.IsFinite(grade.SkyLevel) && grade.SkyLevel > 0f
                && NightDarkest.TryGetValue(new NightGroup(grade.Key with { ObjectKey = string.Empty }, grade.Rig, grade.Night), out var darkest) && darkest > 0f
                ? grade.SkyLevel / darkest : float.NaN;
            var timed = new TimedGroup(rig, grade.Taken);
            var noise = grade.Taken != default && LocalNoise.TryGetValue(timed, out var n) ? n : float.NaN;
            var starRatio = grade.Taken != default && grade.Metrics.StarCount > 0 && LocalStars.TryGetValue(timed, out var st) && st > 0f
                ? grade.Metrics.StarCount / st : float.NaN;
            return PatchyCloud(patchiness, local, noise, starRatio) || CloudRun.Contains(timed) || SkyRise.Contains(timed) || nightSky >= NightSkyCloudFrom;
        }

        // A window sliding over the group's frames in time order: O(n w log w) for w frames in a window, never a scan of the
        // group per frame. The plateau test's two side windows are found by binary search (O(log n + w log w) each).
        private void AddLocalFloors(RigGroup key, List<(DateTimeOffset Taken, float Patchiness)> frames)
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
                var floor = window[window.Count / 5];
                // A stretch longer than the window that is cloudy throughout is its own floor, so its cloud reads as normal. It is
                // told from a target's climb or fall by its SHAPE: a plateau, its local floor at least CloudFrom above the floors of
                // the frames one to three windows before AND after it, which a smooth climb never is (it stays at or below the floor
                // behind it). The clear part around it is then the floor. A stretch running to either end of the run has no clear
                // part on that side to compare with and is left as measured.
                if (floor > 0f && FloorOf(frames, taken - 3 * LocalWindow, taken - LocalWindow, window) is { } before
                    && FloorOf(frames, taken + LocalWindow, taken + 3 * LocalWindow, window) is { } after
                    && MathF.Max(before, after) > 0f && floor >= CloudFrom * MathF.Max(before, after))
                {
                    floor = MathF.Max(before, after);
                }
                LocalFloor[new TimedGroup(key, taken)] = floor;
                LocalNoise[new TimedGroup(key, taken)] = SuccessiveJitter(frames, start, end, floor, window);
            }
        }

        // The window's frame-to-frame jitter as a share of the floor: the MAD of successive differences over sqrt 2, which a smooth
        // climb (a rising target) leaves out; NaN under four differences.
        private static float SuccessiveJitter(List<(DateTimeOffset Taken, float Patchiness)> frames, int start, int end, float floor, List<float> scratch)
        {
            if (end - start < 5 || !(floor > 0f))
            {
                return float.NaN;
            }
            scratch.Clear();
            for (var i = start + 1; i < end; i++)
            {
                scratch.Add((frames[i].Patchiness - frames[i - 1].Patchiness) / floor);
            }
            scratch.Sort();
            var median = scratch[scratch.Count / 2];
            for (var i = 0; i < scratch.Count; i++)
            {
                scratch[i] = MathF.Abs(scratch[i] - median);
            }
            scratch.Sort();
            return 1.4826f * scratch[scratch.Count / 2] / MathF.Sqrt(2f);
        }

        // The cloud runs of one rig's group (InCloudRun), its frames in time order: confirmed frames, then two passes that let a
        // frame past the plain bars join a run that its neighbour within CloudRunGap belongs to.
        private void AddCloudRuns(RigGroup key, List<(DateTimeOffset Taken, float Patchiness)> frames)
        {
            if (!ClearestPatchiness.TryGetValue(key, out var clearest) || !(clearest > 0f))
            {
                return;
            }
            frames.Sort(static (a, b) => a.Taken.CompareTo(b.Taken));
            var n = frames.Count;
            var strong = new bool[n];
            var weak = new bool[n];
            for (var k = 0; k < n; k++)
            {
                var timed = new TimedGroup(key, frames[k].Taken);
                var patchiness = frames[k].Patchiness / clearest;
                var local = LocalFloor.TryGetValue(timed, out var floor) && floor > 0f ? frames[k].Patchiness / floor : float.NaN;
                var noise = LocalNoise.TryGetValue(timed, out var jitter) ? jitter : float.NaN;
                var stars = _starCount.TryGetValue(timed, out var count) && LocalStars.TryGetValue(timed, out var around) && around > 0f && count > 0f
                    ? count / around : float.NaN;
                strong[k] = PatchyCloud(patchiness, local, noise, stars);
                // Joining a run asks the plain bars and nothing less. A looser test (above its own floor by more than the jitter)
                // chained down a slow trend: on 2026-09-12 the sky's patchiness fell for an hour, every frame sat ~10 % over a +-60
                // minute floor, and a run from one short event took 49 clear frames; it recovered no onset frame on 09-10 either.
                weak[k] = patchiness >= CloudFrom && local >= LocalCloudFrom;
            }
            bool[] run = [.. strong];
            for (var k = 1; k < n; k++)
            {
                if (!run[k] && weak[k] && run[k - 1] && frames[k].Taken - frames[k - 1].Taken <= CloudRunGap)
                {
                    run[k] = true;
                }
            }
            for (var k = n - 2; k >= 0; k--)
            {
                if (!run[k] && weak[k] && run[k + 1] && frames[k + 1].Taken - frames[k].Taken <= CloudRunGap)
                {
                    run[k] = true;
                }
            }
            // Bracket fill: every frame between two cloud frames no more than CloudBracketSpan apart joins them, whatever its own
            // bars. The sky's rises are read after the runs, from frames outside them, and are not ends of a bracket.
            var last = -1;
            for (var k = 0; k < n; k++)
            {
                if (!run[k])
                {
                    continue;
                }
                if (last >= 0 && k - last > 1 && frames[k].Taken - frames[last].Taken <= CloudBracketSpan)
                {
                    for (var j = last + 1; j < k; j++)
                    {
                        run[j] = true;
                    }
                }
                last = k;
            }
            for (var k = 0; k < n; k++)
            {
                if (run[k] && !strong[k])
                {
                    CloudRun.Add(new TimedGroup(key, frames[k].Taken));
                }
            }
            AddSkyRises(key, frames, run);
        }

        // Each frame's sky over the line the group's sky followed in the LocalWindow before it (SkyOverTrend), in time order, so a
        // frame read as risen stays out of every later frame's line as a frame of a cloud run does: a least-squares line over the
        // earlier frames' minutes, its scatter the MAD of their residuals.
        private void AddSkyRises(RigGroup key, List<(DateTimeOffset Taken, float Patchiness)> frames, bool[] cloud)
        {
            List<(double Minutes, double Sky)> prior = [];
            List<double> residuals = [];
            var risen = new bool[frames.Count];
            var start = 0;
            for (var k = 0; k < frames.Count; k++)
            {
                var timed = new TimedGroup(key, frames[k].Taken);
                if (!_sky.TryGetValue(timed, out var sky))
                {
                    continue;
                }
                while (frames[start].Taken < frames[k].Taken - LocalWindow)
                {
                    start++;
                }
                prior.Clear();
                for (var i = start; i < k; i++)
                {
                    if (!cloud[i] && !risen[i] && _sky.TryGetValue(new TimedGroup(key, frames[i].Taken), out var earlier))
                    {
                        prior.Add(((frames[i].Taken - frames[k].Taken).TotalMinutes, earlier));
                    }
                }
                if (prior.Count < SkyTrendMinFrames
                    && !(prior.Count >= SkyTrendMinFramesOverSpan && prior[^1].Minutes - prior[0].Minutes >= SkyTrendMinSpan.TotalMinutes))
                {
                    continue;
                }
                double mx = 0, my = 0;
                foreach (var (x, y) in prior)
                {
                    mx += x;
                    my += y;
                }
                mx /= prior.Count;
                my /= prior.Count;
                double sxx = 0, sxy = 0;
                foreach (var (x, y) in prior)
                {
                    sxx += (x - mx) * (x - mx);
                    sxy += (x - mx) * (y - my);
                }
                var slope = sxx > 0 ? sxy / sxx : 0;
                // The line at this frame's own time, which is minute 0.
                var at = my - slope * mx;
                if (!(at > 0))
                {
                    continue;
                }
                residuals.Clear();
                foreach (var (x, y) in prior)
                {
                    residuals.Add(y - (my + slope * (x - mx)));
                }
                var scatter = 1.4826 * MedianAbsoluteDeviation(residuals);
                var ratio = (float)(sky / at);
                SkyTrend[timed] = ratio;
                if (!cloud[k] && ratio >= 1f + MathF.Max(SkyRiseFrom, (float)(SkyRiseSigmas * scatter / at)))
                {
                    risen[k] = true;
                    SkyRise.Add(timed);
                }
            }
        }

        private static double MedianAbsoluteDeviation(List<double> values)
        {
            values.Sort();
            var median = values[values.Count / 2];
            for (var i = 0; i < values.Count; i++)
            {
                values[i] = Math.Abs(values[i] - median);
            }
            values.Sort();
            return values[values.Count / 2];
        }

        // Each frame's neighbours' median star count within the local window, the frame itself left out.
        private void AddLocalStars(RigGroup key, List<(DateTimeOffset Taken, float Patchiness)> frames)
        {
            frames.Sort(static (a, b) => a.Taken.CompareTo(b.Taken));
            List<float> window = [];
            var start = 0;
            var end = 0;
            for (var k = 0; k < frames.Count; k++)
            {
                var taken = frames[k].Taken;
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
                    if (i != k)
                    {
                        window.Add(frames[i].Patchiness);
                    }
                }
                _starCount[new TimedGroup(key, taken)] = frames[k].Patchiness;
                if (window.Count > 0)
                {
                    window.Sort();
                    LocalStars[new TimedGroup(key, taken)] = window[window.Count / 2];
                }
            }
        }

        // The 20th percentile of the frames in [from, to], null when there are none. The frames are sorted by time, so the
        // bounds are two binary searches and only the frames inside are sorted: O(log n + w log w).
        private static float? FloorOf(List<(DateTimeOffset Taken, float Patchiness)> frames, DateTimeOffset from, DateTimeOffset to, List<float> scratch)
        {
            scratch.Clear();
            for (var i = FirstAtOrAfter(frames, from); i < frames.Count && frames[i].Taken <= to; i++)
            {
                scratch.Add(frames[i].Patchiness);
            }
            if (scratch.Count == 0)
            {
                return null;
            }
            scratch.Sort();
            return scratch[scratch.Count / 5];
        }

        // The index of the first frame taken at or after <from>, or frames.Count.
        private static int FirstAtOrAfter(List<(DateTimeOffset Taken, float Patchiness)> frames, DateTimeOffset from)
        {
            int lo = 0, hi = frames.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (frames[mid].Taken < from)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
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
    /// rest of the grade was not tuned on) and the cloud marks of 07-06, it took the clear frames flagged from 10, 72, 14 and 0
    /// to 0, 9, 4 and 0 (09-10: 72 to 9 on the shipping grade), for 40 of 44 catches kept on 09-10 and 3 of 52 lost on 07-06, where 1.15 left 50 flagged on 09-10.
    /// </summary>
    public const float LocalCloudFrom = 1.2f;

    /// <summary>How far either side of a frame its neighbours are taken from: shorter than a target's climb, longer than a cloud.</summary>
    public static readonly TimeSpan LocalWindow = TimeSpan.FromMinutes(60);

    /// <summary>From this multiple, the cloud holds the colour at poor.</summary>
    public const float HeavyCloudFrom = 4f;
}
