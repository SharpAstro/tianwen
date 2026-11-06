using System.Collections.Concurrent;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Lunar;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.IO;

namespace SkyTrendProbe;

public sealed record SkyAstro(double SunAltDeg, double MoonAltDeg, double MoonIllum, double MoonSepDeg, double ObjAltDeg, double Lat, double Lon, string SiteSource)
{
    public static readonly SkyAstro Unknown = new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, "none");
}

/// <summary>One light as graded: the viewer's grade, the live judge's grade, and what the file says.</summary>
public sealed record Graded(string Folder, string Path, string Series, string Group, string Rig, ImageMeta Meta, LightGrade? Viewer, LightGrade? Live,
    float Flux, SkyAstro Astro);

public static class Grader
{
    /// <summary>The number of brightest stars whose median flux stands for a frame's star flux.</summary>
    public const int FluxStars = 50;

    private static readonly string[] Extensions = [".fits", ".fit", ".fts", ".fz", ".fits.gz", ".fit.gz"];

    public static IReadOnlyList<string> Enumerate(string folder)
        => [.. FileEnumeration.EnumerateFiles(folder, Extensions, recursive: true).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Reads a file as the two paths do. LIVE: the camera's image as it is, <c>LightGrading.Grade(image, stars)</c> with no full-scale ADU
    /// (<c>Session.Imaging.cs</c>, snrMin 10, 1000 stars). VIEWER: the document's adoption (the file scaled to [0, 1] by its peak),
    /// stars at snrMin 10 and 2000, <c>LightGrading.Grade(image, stars, fullScaleAdu)</c>.
    /// </summary>
    public static async Task<Graded?> GradeFileAsync(string folder, string path, CancellationToken ct)
    {
        if (!Image.TryReadFitsFile(path, out var image) || image is null)
        {
            return null;
        }
        if (image.ImageMeta.FrameType != FrameType.Light)
        {
            return null;
        }
        var meta = image.ImageMeta;
        var liveStars = await image.FindStarsAsync(image.ReferenceStarChannel, snrMin: 10, maxStars: 1000, cancellationToken: ct);
        var live = LightGrading.Grade(image, liveStars);
        var flux = FluxOf(liveStars);

        float? fullScaleAdu = image.HasUnitScalePeak || image.UnitScaleDivisor <= 0f ? null : image.UnitScaleDivisor;
        var viewImage = image.HasUnitScalePeak ? image : image.ScaleFloatValuesToUnitInPlace();
        viewImage.FillInteriorHolesInPlace();
        var viewStars = await viewImage.FindStarsAsync(viewImage.ReferenceStarChannel, snrMin: 10, maxStars: 2000, cancellationToken: ct);
        var viewer = LightGrading.Grade(viewImage, viewStars, fullScaleAdu);

        var key = (viewer ?? live)?.Key;
        var group = key is { } k
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{k.FilterKey}|{k.ObjectKey}|{k.ExposureMs}ms|bin{k.Bin}|{k.Width}x{k.Height}")
            : "ungraded";
        var rig = LightGrading.RigOf(meta);
        return new Graded(folder, path, $"{System.IO.Path.GetFileName(folder.TrimEnd('/', '\\'))}/{group}/{rig}", group, rig, meta, viewer, live, flux, SkyAstro.Unknown);
    }

    // LightGrading.SiteKnown is private; the same test, so a (0, 0) site and an absent one both read as unknown.
    private static bool SiteKnown(in ImageMeta meta)
        => double.IsFinite(meta.Latitude) && double.IsFinite(meta.Longitude) && !(meta.Latitude == 0 && meta.Longitude == 0);

    /// <summary>The median flux of the brightest <see cref="FluxStars"/> stars (counts as read); NaN with none.</summary>
    public static float FluxOf(StarList stars)
    {
        var top = stars.Select(s => (double)s.Flux).Where(double.IsFinite).OrderByDescending(f => f).Take(FluxStars).ToList();
        return top.Count == 0 ? float.NaN : (float)SkyTrendAnalysis.Median(top);
    }

    /// <summary>Fills each frame's site (the header's, else the same rig's from another frame, else <paramref name="profileSite"/>) and the Sun and Moon at it.</summary>
    public static IReadOnlyList<Graded> WithAstro(IReadOnlyList<Graded> frames, (double Lat, double Lon)? profileSite)
    {
        Dictionary<string, (double, double)> rigSites = [];
        foreach (var g in frames)
        {
            if (SiteKnown(g.Meta) && !rigSites.ContainsKey(g.Rig))
            {
                rigSites[g.Rig] = (g.Meta.Latitude, g.Meta.Longitude);
            }
        }
        return [.. frames.Select(g =>
        {
            double lat, lon;
            string source;
            if (SiteKnown(g.Meta))
            {
                (lat, lon, source) = (g.Meta.Latitude, g.Meta.Longitude, "header");
            }
            else if (rigSites.TryGetValue(g.Rig, out var s))
            {
                (lat, lon, source) = (s.Item1, s.Item2, "borrowed-rig");
            }
            else if (profileSite is { } p)
            {
                (lat, lon, source) = (p.Lat, p.Lon, "profile");
            }
            else
            {
                return g with { Astro = SkyAstro.Unknown };
            }
            return g with { Astro = AstroAt(g.Meta, lat, lon, source) };
        })];
    }

    public static SkyAstro AstroAt(in ImageMeta meta, double lat, double lon, string source)
    {
        var t = meta.ExposureStartTime;
        if (t == default)
        {
            return SkyAstro.Unknown with { Lat = lat, Lon = lon, SiteSource = source };
        }
        var sun = SolarAltitude.Degrees(t, lat, lon);
        double moonAlt = double.NaN, illum = double.NaN, sep = double.NaN, objAlt = double.NaN;
        if (VSOP87a.Reduce(CatalogIndex.Moon, t, lat, lon, out var moonRa, out var moonDec, out _, out var alt, out _))
        {
            moonAlt = alt;
            illum = MeeusMoon.GetPhase(t.ToJulian()).Illumination;
            if (double.IsFinite(meta.TargetRA) && double.IsFinite(meta.TargetDec))
            {
                sep = CoordinateUtils.AngularSeparationDeg(meta.TargetRA, meta.TargetDec, moonRa, moonDec);
            }
        }
        if (double.IsFinite(meta.TargetRA) && double.IsFinite(meta.TargetDec))
        {
            var ha = CoordinateUtils.ConditionHA(SiteContext.ComputeLST(t, lon) - meta.TargetRA);
            objAlt = SiteContext.AltitudeDegrees(lat, ha, meta.TargetDec);
        }
        return new SkyAstro(sun, moonAlt, illum, sep, objAlt, lat, lon, source);
    }

    /// <summary>The owner's mark in a file's name: empty, MAYBE_CLOUD, BAD (unspecified), or BAD_ plus the cause.</summary>
    public static (string Mark, bool Cloud) MarkOf(string path)
    {
        if (CaptureRejection.IsSuspectedCloud(path))
        {
            return ("MAYBE_CLOUD", true);
        }
        if (CaptureRejection.CauseOf(path) is { } cause)
        {
            return cause == CaptureRejection.Cause.Unspecified
                ? ("BAD", true)
                : ("BAD_" + cause.ToString().ToUpperInvariant(), cause == CaptureRejection.Cause.Cloud);
        }
        return ("", false);
    }

    /// <summary>
    /// Verdicts the frames of ONE folder as the viewer does (over the whole folder's grades) and as the live judge does (frame by frame
    /// in time order, one judge per rig), and returns a row per frame with a grade. <paramref name="references"/> default means none.
    /// </summary>
    public static IReadOnlyList<FrameRow> Evaluate(IReadOnlyList<Graded> folderFrames, ImmutableArray<LightGradeReference> references)
    {
        var viewerGrades = ImmutableDictionary.CreateBuilder<string, LightGrade>(StringComparer.Ordinal);
        foreach (var g in folderFrames.Where(g => g.Viewer is not null))
        {
            viewerGrades[g.Path] = g.Viewer!.Value;
        }
        var snapshot = viewerGrades.ToImmutable();

        // Live: one judge per rig (an OTA's), the frames fed in time order.
        ConcurrentDictionary<string, LiveCloudJudge> judges = [];
        Dictionary<string, (bool Judged, bool Cloud, float OverTrend, bool Rising)> live = [];
        var n = 0;
        foreach (var g in folderFrames.Where(g => g.Meta.ExposureStartTime != default).OrderBy(g => g.Meta.ExposureStartTime))
        {
            var judge = judges.GetOrAdd(g.Rig, _ => new LiveCloudJudge(references));
            var verdict = judge.Add($"f{n++}", g.Live);
            var liveOverTrend = float.NaN;
            var liveRising = false;
#if SKY_TREND
            if (verdict is { } lv)
            {
                liveOverTrend = lv.SkyOverTrend;
                liveRising = lv.SkyRising;
            }
#endif
            live[g.Path] = (verdict is not null, LiveCloudJudge.IsCloud(verdict), liveOverTrend, liveRising);
        }

        List<FrameRow> rows = [];
        foreach (var g in folderFrames.Where(g => g.Viewer is not null))
        {
            var grade = g.Viewer!.Value;
            var v = LightGrading.Verdict(snapshot, grade, references);
            var (mark, markedCloud) = MarkOf(g.Path);
            var cloud = (v.Causes & GradeCause.Cloud) != 0;
            var night = float.IsFinite(v.NightSky) && v.NightSky >= LightGrading.NightSkyCloudFrom;
            float overTrend = float.NaN;
            var rising = false;
#if SKY_TREND
            overTrend = v.SkyOverTrend;
            rising = v.SkyRising;
#endif
            var (judged, liveCloud, liveOverTrend2, liveRising2) = live.GetValueOrDefault(g.Path, (false, false, float.NaN, false));
            var a = g.Astro;
            rows.Add(new FrameRow(g.Series, System.IO.Path.GetFileName(g.Path), mark, markedCloud, g.Meta.ExposureStartTime,
                g.Meta.ExposureDuration.TotalSeconds, g.Group, g.Rig, grade.SkyLevel, grade.Patchiness, grade.Metrics.StarCount, g.Flux,
                v.Causes.ToString(), cloud, v.InCloudRun, v.NightSky, v.Shown, overTrend, rising,
                cloud && !v.InCloudRun && !night && !rising, night, judged, liveCloud,
                a.SunAltDeg, a.MoonAltDeg, a.MoonIllum, a.MoonSepDeg, a.ObjAltDeg, a.Lat, a.Lon, a.SiteSource, liveOverTrend2, liveRising2));
        }
        return rows;
    }
}
