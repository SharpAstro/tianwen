using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>What a group of a detected capture is (#2553).</summary>
public enum DetectedGroupKind
{
    /// <summary>Light frames of one target, exposure, filter and gain.</summary>
    Lights,

    /// <summary>Dark frames of one exposure, gain and camera.</summary>
    Darks,

    /// <summary>Flat frames of one filter and camera.</summary>
    Flats,

    /// <summary>Bias frames.</summary>
    Bias,

    /// <summary>Dark flats (flat darks): darks of the flats' exposure.</summary>
    FlatDarks,

    /// <summary>A comet track file (<see cref="CometTrack"/>).</summary>
    CometTrack,

    /// <summary>The files a person should look at: those whose kind is a guess, and lights no dark matches. Each also stays in
    /// the group of the kind it was taken for.</summary>
    NeedsALook,

    /// <summary>Files the stack does not take: a focus run, a scout, a processed product, and what the person left out.</summary>
    NotStacked,
}

/// <summary>What a light's grade reads as among the graded lights of its group (<see cref="LightGrading.Verdict"/>).</summary>
/// <param name="Share">The grade's share, in [0, 1], that the file list shows (<see cref="LightGradeVerdict.Shown"/>).</param>
/// <param name="Causes">What the grade found wrong, if anything.</param>
public readonly record struct DetectedGrade(float Share, GradeCause Causes);

/// <summary>One file of a detected capture, with the kind it was taken for and why.</summary>
/// <param name="Place">The file's place inside the capture folder, forward slashes: what a reassignment names it by.</param>
/// <param name="FullPath">The file's full path.</param>
/// <param name="Kind">What the file is taken for: the person's reassignment, else the detection. <see cref="FrameType.None"/>
/// for a file left out, and for a comet track.</param>
/// <param name="DetectedKind">What the detection took it for before any reassignment.</param>
/// <param name="Why">Why, in words: <c>IMAGETYP = Light</c>, <c>no IMAGETYP; folder named darks</c>, <c>no IMAGETYP; even field at
/// 48 % of full scale</c>, <c>set by you (detected: Light)</c>.</param>
/// <param name="Unsure">The kind is a guess from the pixels, not read off the cards or the folder's name.</param>
/// <param name="Reassigned">The person set the kind.</param>
/// <param name="ExposureSeconds">The exposure; 0 where the file says none.</param>
/// <param name="Filter">The filter as the file names it; empty where it names none.</param>
/// <param name="SensorTemperatureC">The sensor temperature; NaN where the file says none.</param>
/// <param name="Gain">The gain; -1 where the file says none.</param>
/// <param name="Camera">The camera; empty where the file says none.</param>
/// <param name="Grade">A light's grade; null for another kind, and for a light not graded.</param>
/// <param name="Note">A thing to look at that is not the kind: no dark matches this light. Null otherwise.</param>
/// <param name="IsCometTrack">The file is a comet track, not a frame.</param>
/// <param name="Colour">The camera is colour (a Bayer or colour sensor); false for a mono one; null where the file says none.</param>
public sealed record DetectedFile(
    string Place,
    string FullPath,
    FrameType Kind,
    FrameType DetectedKind,
    string Why,
    bool Unsure,
    bool Reassigned,
    double ExposureSeconds,
    string Filter,
    float SensorTemperatureC,
    int Gain,
    string Camera,
    DetectedGrade? Grade = null,
    string? Note = null,
    bool IsCometTrack = false,
    bool? Colour = null)
{
    /// <summary>A person should look at this file: its kind is a guess, or it has a note.</summary>
    public bool NeedsALook => Unsure || Note is not null;
}

/// <summary>A group of a detected capture: what it is, its detail in words, and its files.</summary>
/// <param name="Kind">What the group is.</param>
/// <param name="Title">The group's name: <c>Lights</c>, <c>Darks</c>, <c>Needs a look</c>.</param>
/// <param name="Detail">What sets the group apart: <c>10P/Tempel 2 · 60 s · IDAS LPS-D3 · gain 1600</c>; empty for a group of one kind.</param>
/// <param name="Files">The group's files, in the order of their places.</param>
/// <param name="Slug">For a lights group: the name the stack gives that group's results (<see cref="LightGroupKey.Slug"/>), which an image that starts from one of them names; empty for another group.</param>
public sealed record DetectedGroup(DetectedGroupKind Kind, string Title, string Detail, ImmutableArray<DetectedFile> Files, string Slug = "")
{
    /// <summary>The group as one line: <c>Lights · 10P/Tempel 2 · 60 s · IDAS LPS-D3 · gain 1600</c>.</summary>
    public string Heading => Detail.Length == 0 ? Title : Title + " · " + Detail;
}

/// <summary>What a capture folder holds (<see cref="CaptureDetection.DetectAsync"/>).</summary>
/// <param name="Folder">The capture folder.</param>
/// <param name="Groups">The groups found, lights first, then the calibration, the comet track, what needs a look, what is not stacked.</param>
/// <param name="Notes">What the scan left out, in words (an integration, a map, a master calibration frame).</param>
public sealed record DetectedCapture(string Folder, ImmutableArray<DetectedGroup> Groups, ImmutableArray<string> Notes)
{
    /// <summary>Every file once, in the order of its place, with the kind it is taken for.</summary>
    public ImmutableArray<DetectedFile> Files => [.. Groups
        .Where(static group => group.Kind != DetectedGroupKind.NeedsALook)
        .SelectMany(static group => group.Files)
        .OrderBy(static file => file.Place, StringComparer.OrdinalIgnoreCase)];
}

/// <summary>How <see cref="CaptureDetection.DetectAsync"/> runs.</summary>
public sealed record CaptureDetectionOptions
{
    /// <summary>The kinds the person set (the stack step's <see cref="StackSettings.Reassign"/>); they stand over the detection.</summary>
    public ImmutableArray<FrameReassignment> Reassigned { get; set; } = [];

    /// <summary>Grade each light (<see cref="LightGrading"/>): reads the pixels of every light up to <see cref="MaxGradedLights"/>.</summary>
    public bool GradeLights { get; set; } = true;

    /// <summary>The most lights graded, taken evenly through the run; the rest show no grade. A grade finds stars in a frame, which costs a second or so.</summary>
    public int MaxGradedLights { get; set; } = 48;
}

/// <summary>
/// What a capture folder holds (#2553): lights, darks, flats, bias, a comet track, each file's kind and why, and the files to
/// look at. Read off the FITS cards first (<c>IMAGETYP</c>, exposure, filter, temperature, gain, camera), then the folder
/// names, then, for a file both leave unsure, its pixels.
/// </summary>
/// <remarks>
/// <para><b>One scan, one matcher.</b> The files are the stack's own scan (<see cref="StackingPipeline.ScanFramesAsync"/>, which
/// leaves out <c>workdir</c>, maps, masters and integrations), the kinds are the scan's frame types
/// (<see cref="FrameTypeEx.FromFITSValue"/> read them), the calibration sets are
/// <see cref="CalibrationResolver.GroupCalibration"/>'s, and a light has a dark when <see cref="CalibrationResolver.BestDark"/>
/// finds one. There is no second classifier: what this says a frame is, is what the stack will take it for, except where
/// the person reassigned it, which the stack takes too.</para>
/// <para><b>A guess from the pixels is always marked unsure</b> and gathered under Needs a look, since a frame the cards and
/// the folder leave open is one only a person can settle. The rule is plain (an even field mid-scale is a flat, a
/// near-black frame of no exposure a bias, stars a light); its numbers are the constants below.</para>
/// </remarks>
public static class CaptureDetection
{
    /// <summary>The share of full scale the middle of a flat sits within.</summary>
    internal const double FlatLevelLow = 0.10, FlatLevelHigh = 0.95;

    /// <summary>A field is even when its darker tenth is at least this share of its brighter tenth.</summary>
    internal const double EvenFieldRatio = 0.55;

    /// <summary>A frame is near black under this share of full scale.</summary>
    internal const double NearBlack = 0.10;

    /// <summary>A pixel is a star's when it stands this many noise widths above the median; a frame is "with stars" when more than <see cref="StarShare"/> of its pixels do.</summary>
    internal const double StarSigmas = 20, StarShare = 0.0005;

    // Words in a folder's name that say what the frames in it are. The deepest folder that holds one decides.
    private static readonly (string Word, FrameType Kind)[] FolderWords =
    [
        ("lights", FrameType.Light), ("light", FrameType.Light),
        ("darkflats", FrameType.DarkFlat), ("darkflat", FrameType.DarkFlat), ("flatdarks", FrameType.DarkFlat), ("flatdark", FrameType.DarkFlat),
        ("darks", FrameType.Dark), ("dark", FrameType.Dark),
        ("flats", FrameType.Flat), ("flat", FrameType.Flat), ("flatfield", FrameType.Flat), ("flatfields", FrameType.Flat),
        ("biases", FrameType.Bias), ("bias", FrameType.Bias), ("offsets", FrameType.Bias), ("offset", FrameType.Bias),
    ];

    /// <summary>
    /// What <paramref name="folder"/> holds. Refuses (throws) a folder that is not there.
    /// </summary>
    /// <param name="folder">The capture folder.</param>
    /// <param name="options">How to run; null is the defaults.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<DetectedCapture> DetectAsync(string folder, CaptureDetectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        options ??= new CaptureDetectionOptions();
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"the capture folder {root} does not exist");
        }

        // The stack's own scan, with no reassignment: the detection is what the cards and the folder say, and the person's
        // word is laid over it below, so both can be shown.
        var scan = await StackingPipeline.ScanFramesAsync(root, outputDir: null, includeIntegrations: false, includeSuspectedCloud: false, reassigned: default, cancellationToken);
        var kinds = FrameReassignment.LookupOf(options.Reassigned);
        var files = new List<(FrameInfo Frame, DetectedFile File)>();
        foreach (var frame in scan.Frames.OrderBy(static frame => frame.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var place = FrameReassignment.PlaceOf(root, frame.Path) ?? Path.GetFileName(frame.Path);
            var (detected, why, unsure) = await KindOfAsync(frame, place, cancellationToken);
            var kind = detected;
            var reassigned = kinds.TryGetValue(place, out var set);
            if (reassigned)
            {
                kind = set;
                why = $"set by you (detected: {NameOf(detected)}; {why})";
                unsure = false;
            }

            var meta = frame.Meta;
            files.Add((frame with { Meta = meta with { FrameType = kind } }, new DetectedFile(
                place, frame.Path, kind, detected, why, unsure, reassigned,
                meta.ExposureDuration.TotalSeconds,
                meta.Filter.IdentityKey.Length > 0 ? meta.Filter.RawName ?? meta.Filter.DisplayName : string.Empty,
                meta.CCDTemperature, meta.Gain, meta.Instrument ?? string.Empty,
                Colour: meta.SensorType switch { SensorType.Monochrome => false, SensorType.Unknown => null, _ => true })));
        }

        var notes = NotesOf(scan);
        var groups = new List<DetectedGroup>();
        var lights = files.Where(static each => each.File.Kind == FrameType.Light).ToList();
        var calibration = CalibrationResolver.GroupCalibration(files.Select(static each => each.Frame));
        var gradeOf = options.GradeLights ? await GradeAsync(lights, options.MaxGradedLights, cancellationToken) : [];
        var withNotes = new Dictionary<string, DetectedFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var set in lights.GroupBy(static each => LightGroupKey(each.Frame)).OrderBy(static set => set.Key, StringComparer.Ordinal))
        {
            var setFrames = set.Select(static each => each.Frame).ToList();
            var applied = PreCalibration.Across(setFrames, out _);
            var key = CalibrationResolver.SessionKey(setFrames);
            var dark = applied.HasFlag(CalibrationApplied.Dark)
                ? null
                : CalibrationResolver.BestDark(calibration.GetValueOrDefault(FrameType.Dark), setFrames[0], requireGainMatch: true, maxTempDelta: null, key);
            var noDark = !applied.HasFlag(CalibrationApplied.Dark) && dark is null
                ? $"no dark matches {Seconds(key.Exposure.TotalSeconds)}{(key.Gain >= 0 ? $", gain {key.Gain}" : string.Empty)}{(key.TemperatureC is { } t ? $" at {t} °C" : string.Empty)}"
                : null;
            var members = set
                .Select(each => each.File with
                {
                    Grade = gradeOf.GetValueOrDefault(each.File.Place),
                    Note = noDark,
                })
                .ToImmutableArray();
            foreach (var member in members.Where(static member => member.Note is not null))
            {
                withNotes[member.Place] = member;
            }

            var first = setFrames[0].Meta;
            groups.Add(new DetectedGroup(DetectedGroupKind.Lights, "Lights", Join(
                first.ObjectName, Seconds(first.ExposureDuration.TotalSeconds), FilterOf(first), GainOf(first.Gain), first.Instrument), members,
                LightGroupKey.FromFrame(setFrames[0]).Slug()));
        }

        AddCalibration(groups, files, FrameType.Dark, DetectedGroupKind.Darks, "Darks");
        AddCalibration(groups, files, FrameType.Flat, DetectedGroupKind.Flats, "Flats");
        AddCalibration(groups, files, FrameType.Bias, DetectedGroupKind.Bias, "Bias");
        AddCalibration(groups, files, FrameType.DarkFlat, DetectedGroupKind.FlatDarks, "Flat darks");

        var track = TracksIn(root);
        if (track.Length > 0)
        {
            groups.Add(new DetectedGroup(DetectedGroupKind.CometTrack, "Comet track", string.Empty, track));
        }

        var notStacked = files.Where(static each => each.File.Kind is not (FrameType.Light or FrameType.Dark or FrameType.Flat or FrameType.Bias or FrameType.DarkFlat))
            .Select(static each => each.File).ToImmutableArray();
        var look = groups.Where(static group => group.Kind is not (DetectedGroupKind.CometTrack))
            .SelectMany(static group => group.Files)
            .Where(static file => file.NeedsALook)
            .GroupBy(static file => file.Place, StringComparer.OrdinalIgnoreCase)
            .Select(static each => each.First())
            .OrderBy(static file => file.Place, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (look.Length > 0)
        {
            groups.Add(new DetectedGroup(DetectedGroupKind.NeedsALook, "Needs a look", string.Empty, look));
        }

        if (notStacked.Length > 0)
        {
            groups.Add(new DetectedGroup(DetectedGroupKind.NotStacked, "Not stacked", string.Empty, notStacked));
        }

        return new DetectedCapture(root, [.. groups], notes);
    }

    /// <summary>The detection answered in words, one line for a group and one for each file: the form the verb prints.</summary>
    public static IEnumerable<string> Describe(DetectedCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        foreach (var group in capture.Groups)
        {
            yield return $"{group.Heading} ({group.Files.Length})";
            foreach (var file in group.Files)
            {
                var facts = Join(
                    file.ExposureSeconds > 0 ? Seconds(file.ExposureSeconds) : string.Empty,
                    file.Filter,
                    float.IsFinite(file.SensorTemperatureC) ? $"{file.SensorTemperatureC.ToString("0.#", CultureInfo.InvariantCulture)} °C" : string.Empty,
                    GainOf(file.Gain),
                    file.Camera);
                var grade = file.Grade is { } g ? $"grade {Math.Round(g.Share * 100):0} %{(g.Causes != GradeCause.None ? " " + g.Causes.ToString().ToLowerInvariant() : string.Empty)}" : string.Empty;
                yield return "  " + Join(file.Place, NameOf(file.Kind) + (file.Unsure ? "?" : string.Empty), file.Why, facts, grade, file.Note ?? string.Empty);
            }
        }

        foreach (var note in capture.Notes)
        {
            yield return "note: " + note;
        }
    }

    private static string NameOf(FrameType kind) => kind == FrameType.None ? "none" : kind.ToString();

    private static string Seconds(double seconds) => seconds <= 0 ? string.Empty : $"{seconds.ToString("0.###", CultureInfo.InvariantCulture)} s";

    private static string GainOf(int gain) => gain >= 0 ? $"gain {gain}" : string.Empty;

    private static string FilterOf(ImageMeta meta) => meta.Filter.IdentityKey.Length > 0 ? meta.Filter.RawName ?? meta.Filter.DisplayName : string.Empty;

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));

    // The lights of one group share a target, an exposure, a filter, a gain, a camera and a frame shape.
    private static string LightGroupKey(FrameInfo frame)
        => $"{frame.Meta.ObjectName.Trim().ToUpperInvariant()}|{CalibrationEpochs.SetGroupKey(frame)}|{frame.Meta.Instrument}";

    private static ImmutableArray<string> NotesOf(StackingPipeline.FrameScan scan)
    {
        var notes = ImmutableArray.CreateBuilder<string>();
        if (scan.IntegrationsSkipped > 0)
        {
            notes.Add($"left out {scan.IntegrationsSkipped} integration(s) or other TianWen product(s)");
        }

        if (scan.RejectionMapsSkipped > 0)
        {
            notes.Add($"left out {scan.RejectionMapsSkipped} rejection or coverage map(s)");
        }

        if (scan.MastersSkipped > 0)
        {
            notes.Add($"left out {scan.MastersSkipped} master calibration frame(s): a stack builds its own from the raw frames");
        }

        if (scan.SuspectedCloudSkipped > 0)
        {
            notes.Add($"left out {scan.SuspectedCloudSkipped} frame(s) named MAYBE_CLOUD_");
        }

        return notes.ToImmutable();
    }

    private static void AddCalibration(List<DetectedGroup> groups, List<(FrameInfo Frame, DetectedFile File)> files, FrameType kind, DetectedGroupKind groupKind, string title)
    {
        // The calibration sets the stack's own scan finds are by sensor configuration (CalibrationEpochs.SetGroupKey):
        // exposure, filter (for flats), frame shape and gain.
        foreach (var set in files.Where(each => each.File.Kind == kind)
            .GroupBy(static each => $"{CalibrationEpochs.SetGroupKey(each.Frame)}|{each.Frame.Meta.Instrument}")
            .OrderBy(static set => set.Key, StringComparer.Ordinal))
        {
            var meta = set.First().Frame.Meta;
            var detail = kind switch
            {
                FrameType.Flat => Join(FilterOf(meta), GainOf(meta.Gain), meta.Instrument),
                FrameType.Bias => Join(GainOf(meta.Gain), meta.Instrument),
                _ => Join(Seconds(meta.ExposureDuration.TotalSeconds), GainOf(meta.Gain), meta.Instrument),
            };
            groups.Add(new DetectedGroup(groupKind, title, detail, [.. set.Select(static each => each.File)]));
        }
    }

    // The files that are comet tracks: any .json under the folder (never under workdir) that reads as one.
    private static ImmutableArray<DetectedFile> TracksIn(string root)
    {
        var tracks = ImmutableArray.CreateBuilder<DetectedFile>();
        var workdir = Path.Combine(root, StackingPipeline.WorkdirName) + Path.DirectorySeparatorChar;
        foreach (var stamp in FileEnumeration.EnumerateFileStamps(root, [".json"], recursive: true).OrderBy(static stamp => stamp.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (stamp.Path.StartsWith(workdir, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !CometTrack.TryRead(stamp.Path, out var track, out _))
            {
                continue;
            }

            tracks.Add(new DetectedFile(
                FrameReassignment.PlaceOf(root, stamp.Path) ?? Path.GetFileName(stamp.Path), stamp.Path, FrameType.None, FrameType.None,
                $"comet track of {track.Designation}, {track.Samples.Length} positions from {track.Source}",
                Unsure: false, Reassigned: false, ExposureSeconds: 0, Filter: string.Empty, SensorTemperatureC: float.NaN, Gain: -1, Camera: string.Empty, IsCometTrack: true));
        }

        return tracks.ToImmutable();
    }

    // The kind a file is taken for, and why: the cards, then the folder's names, then the pixels.
    private static async Task<(FrameType Kind, string Why, bool Unsure)> KindOfAsync(FrameInfo frame, string place, CancellationToken cancellationToken)
    {
        if (frame.Meta.FrameType != FrameType.None)
        {
            return (frame.Meta.FrameType, $"IMAGETYP = {frame.Meta.FrameType.ToFITSValue()}", false);
        }

        var directory = Path.GetDirectoryName(place.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        if (KindOfFolder(directory) is { } named)
        {
            return (named.Kind, $"no IMAGETYP; folder named {named.Folder}", false);
        }

        var (kind, why) = await Task.Run(() => KindOfPixels(frame), cancellationToken);
        return (kind, why, true);
    }

    // The deepest folder of the place whose name says what its frames are.
    private static (FrameType Kind, string Folder)? KindOfFolder(string directory)
    {
        foreach (var folder in directory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            // Whole words: "Darks_2026" says darks, "Flatland" says nothing.
            var words = folder.Split([' ', '_', '-', '.', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static word => word.ToLowerInvariant()).ToArray();
            var joined = string.Concat(words);
            foreach (var (word, kind) in FolderWords)
            {
                if (words.Contains(word) || joined == word)
                {
                    return (kind, folder);
                }
            }

            // "Flat Darks" and "Dark Flats" are two words.
            if (words.Length >= 2 && (words.Contains("dark") && words.Contains("flat") || words.Contains("darks") && words.Contains("flats")))
            {
                return (FrameType.DarkFlat, folder);
            }
        }

        return null;
    }

    // The pixels of a file the cards and the folder leave open: the rule is in the class's remarks.
    private static (FrameType Kind, string Why) KindOfPixels(FrameInfo frame)
    {
        const string No = "no IMAGETYP";
        if (!Image.TryReadFitsFile(frame.ReadPath, out var image))
        {
            return (FrameType.Light, $"{No}, no folder name, and the pixels could not be read; taken for a light");
        }

        var histogram = image.Statistics(0, removePedestral: false, pixelStride: 4);
        var bins = histogram.Histogram;
        if (bins.IsDefaultOrEmpty || histogram.Total == 0)
        {
            return (FrameType.Light, $"{No}, no folder name, and no pixels; taken for a light");
        }

        var fullScale = histogram.RescaledMaxValue
            ?? (frame.Meta.SensorFullScaleAdu is > 0f ? frame.Meta.SensorFullScaleAdu.Value
                : frame.BitDepth switch { BitDepth.Int8 => 255f, BitDepth.Int16 => 65535f, _ => Math.Max(1f, image.MaxValue) });
        fullScale = Math.Max(fullScale, 1f);
        double Level(int bin) => Math.Clamp(bin / (double)fullScale, 0, 1);
        int Percentile(double share)
        {
            var target = histogram.Total * share;
            long seen = 0;
            for (var bin = 0; bin < bins.Length; bin++)
            {
                seen += bins[bin];
                if (seen >= target)
                {
                    return bin;
                }
            }

            return bins.Length - 1;
        }

        var (low, middle, high) = (Percentile(0.10), Percentile(0.50), Percentile(0.90));
        var level = Level(middle);
        var even = level is >= FlatLevelLow and <= FlatLevelHigh && high > 0 && low / (double)high >= EvenFieldRatio;
        var mad = Math.Max(1.0, histogram.MAD ?? 1.0);
        var brightFrom = (int)Math.Min(bins.Length, middle + (StarSigmas * mad) + 1);
        long bright = 0;
        for (var bin = brightFrom; bin < bins.Length; bin++)
        {
            bright += bins[bin];
        }

        var withStars = bright / (double)histogram.Total > StarShare;
        var percent = $"{Math.Round(level * 100):0} %";
        if (frame.Meta.ExposureDuration.TotalSeconds <= 0.001 && level < 0.2 && !withStars)
        {
            return (FrameType.Bias, $"{No}; no exposure and near black ({percent} of full scale)");
        }

        if (even && !withStars)
        {
            return (FrameType.Flat, $"{No}; even field at {percent} of full scale");
        }

        if (withStars)
        {
            return (FrameType.Light, $"{No}; stars in the field");
        }

        return level < NearBlack
            ? (FrameType.Dark, $"{No}; no stars and near black ({percent} of full scale)")
            : (FrameType.Light, $"{No}; an uneven field with no stars found ({percent} of full scale)");
    }

    // Grades the lights evenly through the run (at most maxGraded of them): the grade is read against the graded lights of its
    // group, so it needs several. Returned by place; a light that could not be graded is left out.
    private static async Task<Dictionary<string, DetectedGrade>> GradeAsync(List<(FrameInfo Frame, DetectedFile File)> lights, int maxGraded, CancellationToken cancellationToken)
    {
        var answers = new Dictionary<string, DetectedGrade>(StringComparer.OrdinalIgnoreCase);
        if (maxGraded <= 0 || lights.Count == 0)
        {
            return answers;
        }

        var step = Math.Max(1, (int)Math.Ceiling(lights.Count / (double)maxGraded));
        var grades = ImmutableDictionary.CreateBuilder<string, LightGrade>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lights.Count; i += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (frame, file) = lights[i];
            if (!Image.TryReadFitsFile(frame.ReadPath, out var image))
            {
                continue;
            }

            var stars = await image.FindStarsAsync(0, snrMin: 10f, cancellationToken: cancellationToken);
            if (LightGrading.Grade(image, stars) is { } grade)
            {
                grades[file.Place] = grade;
            }
        }

        var all = grades.ToImmutable();
        foreach (var (place, grade) in all)
        {
            var verdict = LightGrading.Verdict(all, grade, selfFit: false);
            answers[place] = new DetectedGrade(verdict.Shown, verdict.Causes);
        }

        return answers;
    }
}
