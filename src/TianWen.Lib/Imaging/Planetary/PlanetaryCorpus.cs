using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpAstro.Ser;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>What a corpus entry is.</summary>
public enum CaptureKind
{
    /// <summary>A SER video on disk.</summary>
    Ser,

    /// <summary>A SER video inside a 7z archive.</summary>
    SerInArchive,

    /// <summary>An AVI video: listed by size, not read.</summary>
    Avi,

    /// <summary>A folder of FITS frames, one frame a file (a filter-wheel LRGB capture).</summary>
    FitsSequence,
}

/// <summary>A SER header, every field as the file holds it.</summary>
public sealed record SerHeaderRecord(
    int LuId,
    string ColorId,
    int LittleEndianFlag,
    int Width,
    int Height,
    int PixelDepthPerPlane,
    int FrameCount,
    string Observer,
    string Instrument,
    string Telescope,
    long DateTimeTicks,
    long DateTimeUtcTicks)
{
    public static SerHeaderRecord From(in SerHeader header) => new SerHeaderRecord(
        header.LuId, header.ColorId.ToString(), header.LittleEndianFlag, header.Width, header.Height, header.PixelDepthPerPlane,
        header.FrameCount, header.Observer, header.Instrument, header.Telescope, header.DateTimeTicks, header.DateTimeUtcTicks);
}

/// <summary>One capture of the planetary corpus, as the survey read it (docs/plans/planetary-restoration.md, R0).</summary>
public sealed record CaptureRecord
{
    /// <summary>
    /// The capture's content identity (<see cref="PlanetaryCorpus.ContentId"/>): the same bytes have the same id wherever they
    /// are, so a moved or renamed capture keeps it and a copy shows up as a duplicate.
    /// </summary>
    public required string Id { get; init; }

    public required CaptureKind Kind { get; init; }

    /// <summary>The file, the archive holding it, or the folder of a FITS sequence; forward slashes.</summary>
    public required string Path { get; init; }

    /// <summary>The path inside the archive, for <see cref="CaptureKind.SerInArchive"/>.</summary>
    public string? Member { get; init; }

    public required long LengthBytes { get; init; }

    public SerHeaderRecord? Header { get; init; }

    /// <summary>The frames a FITS sequence holds (one a file); a SER's are its header's.</summary>
    public int? Frames { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Where the capture's per-frame times come from: <c>trailer</c>, <c>date-obs</c>, <c>none</c>, or <c>unknown</c> (inside an archive).</summary>
    public required string Timestamps { get; init; }

    /// <summary>The first frame's time, ISO 8601 UTC.</summary>
    public string? FirstUtc { get; init; }

    /// <summary>The last frame's time, ISO 8601 UTC.</summary>
    public string? LastUtc { get; init; }

    public double? FramesPerSecond { get; init; }

    /// <summary>Whether the timestamps never go backwards; a joined SER can.</summary>
    public bool? TimestampsMonotonic { get; init; }

    /// <summary>The capture program's settings file beside it (SharpCap's <c>.CameraSettings.txt</c>, FireCapture's <c>.txt</c>).</summary>
    public string? SettingsFile { get; init; }

    /// <summary>That file's <c>key=value</c> lines, keys sorted; the section header SharpCap writes becomes <c>Camera</c>.</summary>
    public SortedDictionary<string, string>? Settings { get; init; }

    /// <summary>
    /// What the survey noticed, sorted: <c>calibration</c> (a bias, dark or flat video, by its frame type or its folder: the
    /// camera's own noise, which the seeing model measures read noise from), <c>duplicate</c>, <c>no-timestamps</c>,
    /// <c>not-read</c> (an AVI), <c>pipp</c> (a PIPP output, cropped and possibly frame-filtered), <c>synthetic</c>
    /// (FireCapture's DummyCam), <c>truncated</c>.
    /// </summary>
    public required string[] Flags { get; init; }

    /// <summary>The first capture with the same <see cref="Id"/>, for a duplicate.</summary>
    public string? DuplicateOf { get; init; }

    /// <summary>Why the capture could not be read, in words; null when it was.</summary>
    public string? Problem { get; init; }
}

/// <summary>A survey's result: what it looked at and what it found, in a stable order.</summary>
public sealed record CorpusManifest(int Version, string[] Roots, CaptureRecord[] Captures)
{
    /// <summary>Back-to-back captures of one folder and frame size, each a run of several files (<see cref="PlanetaryCorpus.Sessions"/>, #1308).</summary>
    public SessionRecord[] Sessions { get; init; } = [];
}

/// <summary>
/// One session of the corpus (#1308): two or more captures of one folder, frame size and colour, each starting within
/// <see cref="PlanetaryCorpus.SessionGap"/> of the one before ending, which a stack joins in time order as one run.
/// </summary>
/// <param name="Name">The folder's name and the first frame's UTC time, which <c>planetary-stack --session</c> asks for.</param>
/// <param name="Captures">The captures' paths, in time order.</param>
public sealed record SessionRecord(string Name, string Folder, int Width, int Height, string ColorId, int Frames, string FirstUtc, string LastUtc,
    string[] Captures);

/// <summary>How a survey reads the corpus.</summary>
/// <param name="SevenZip">The 7-Zip that lists and streams archives; archives are recorded unread without one.</param>
/// <param name="MinFitsSequence">The FITS files a folder needs before it is looked at as a sequence at all.</param>
/// <param name="ArchiveTimeout">How long one archive call may take (a solid archive decompresses from its start).</param>
/// <param name="MinFramesPerSecond">
/// The rate a FITS folder's DATE-OBS span must show to be a lucky-imaging video, since a count of files cannot tell one from a
/// night of deep-sky subs. Measured on the corpus (2026-09-29): every planetary sequence runs at 17 to 226 fps, every set of
/// deep-sky subs at 0.09 to 0.12 and a set of flats at 2.1.
/// </param>
public sealed record CorpusSurveyOptions(SevenZipTool? SevenZip = null, int MinFitsSequence = 500, TimeSpan? ArchiveTimeout = null,
    double MinFramesPerSecond = 5);

/// <summary>
/// The planetary corpus as a registry (docs/plans/planetary-restoration.md, R0): every capture's header, frames,
/// timestamps and settings, read and never written, in one manifest whose bytes a re-run over unchanged files repeats.
/// </summary>
public static class PlanetaryCorpus
{
    /// <summary>The manifest's format version: 2 adds <see cref="CorpusManifest.Sessions"/> (#1308).</summary>
    public const int ManifestVersion = 2;

    /// <summary>
    /// The longest pause between one capture's last frame and the next one's first that still joins them into one session: a capture
    /// program saving a file every few minutes leaves seconds between them, a refocus or a filter change a few minutes.
    /// </summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(10);

    private static readonly string[] Extensions = [".ser", ".avi", ".7z"];
    private static readonly string[] FitsExtensions = [".fits", ".fit", ".fts"];

    /// <summary>
    /// A capture's content identity: the first 16 hex digits of SHA-256 over its 178-byte header, its length (little-endian
    /// 64-bit) and its first frame. Reading one frame is cheap, and it tells two captures with one header apart.
    /// </summary>
    public static string ContentId(ReadOnlySpan<byte> header, long length, ReadOnlySpan<byte> firstFrame)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(header);
        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        sha.AppendData(lengthBytes);
        sha.AppendData(firstFrame);
        Span<byte> hash = stackalloc byte[32];
        sha.GetHashAndReset(hash);
        return Convert.ToHexStringLower(hash[..8]);
    }

    /// <summary>
    /// A capture program's settings: every <c>key=value</c> (or <c>key: value</c>) line, keys sorted, the first of a repeated
    /// key kept. SharpCap's <c>[camera name]</c> section header becomes <c>Camera</c> unless the file names one itself.
    /// </summary>
    public static SortedDictionary<string, string> ParseSettings(string text)
    {
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section ??= line[1..^1].Trim();
                continue;
            }
            var separator = line.IndexOf('=');
            var width = 1;
            if (separator < 0)
            {
                separator = line.IndexOf(": ", StringComparison.Ordinal);
                width = 2;
            }
            if (separator <= 0)
            {
                continue;
            }
            var key = line[..separator].Trim();
            var value = line[(separator + width)..].Trim();
            settings.TryAdd(key, value);
        }
        if (section is not null)
        {
            settings.TryAdd("Camera", section);
        }
        return settings;
    }

    /// <summary>Surveys every capture under <paramref name="roots"/>, read-only.</summary>
    public static async Task<CorpusManifest> SurveyAsync(IReadOnlyList<string> roots, CorpusSurveyOptions options, ILogger logger,
        CancellationToken cancellationToken)
    {
        var records = new List<CaptureRecord>();
        var archiveTimeout = options.ArchiveTimeout ?? TimeSpan.FromMinutes(30);
        foreach (var root in roots)
        {
            var fitsByFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in FileEnumeration.EnumerateFiles(root, [.. Extensions, .. FitsExtensions], recursive: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = System.IO.Path.GetExtension(file);
                if (FitsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    var folder = System.IO.Path.GetDirectoryName(file) ?? root;
                    if (!fitsByFolder.TryGetValue(folder, out var files))
                    {
                        fitsByFolder[folder] = files = [];
                    }
                    files.Add(file);
                }
                else if (extension.Equals(".ser", StringComparison.OrdinalIgnoreCase))
                {
                    records.Add(ReadSer(file));
                }
                else if (extension.Equals(".avi", StringComparison.OrdinalIgnoreCase))
                {
                    records.Add(Avi(file));
                }
                else
                {
                    records.AddRange(await ReadArchiveAsync(file, options.SevenZip, archiveTimeout, logger, cancellationToken).ConfigureAwait(false));
                }
            }
            foreach (var (folder, files) in fitsByFolder)
            {
                if (files.Count >= options.MinFitsSequence && ReadFitsSequence(folder, files, options, logger) is { } sequence)
                {
                    records.Add(sequence);
                }
            }
        }

        records.Sort(static (a, b) =>
        {
            var byPath = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : string.Compare(a.Member, b.Member, StringComparison.OrdinalIgnoreCase);
        });

        // The first of a set of identical captures, in that order, is the one the others duplicate.
        var firstById = new Dictionary<string, CaptureRecord>(StringComparer.Ordinal);
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Problem is not null || record.Kind is CaptureKind.Avi or CaptureKind.FitsSequence)
            {
                continue;
            }
            if (firstById.TryGetValue(record.Id, out var first))
            {
                records[i] = record with
                {
                    DuplicateOf = first.Member is { } member ? $"{first.Path}!{member}" : first.Path,
                    Flags = [.. record.Flags.Append("duplicate").Order(StringComparer.Ordinal)],
                };
            }
            else
            {
                firstById[record.Id] = record;
            }
        }

        return new CorpusManifest(ManifestVersion, [.. roots.Select(Normalise)], [.. records]) { Sessions = [.. Sessions(records)] };
    }

    /// <summary>
    /// The sessions among <paramref name="captures"/> (#1308): plain SER files, not a duplicate, a calibration or a synthetic capture,
    /// with their frames' times, grouped by folder, frame size and colour and chained in time order while each starts within
    /// <see cref="SessionGap"/> of the previous one's last frame. A chain of one capture is no session.
    /// </summary>
    public static IEnumerable<SessionRecord> Sessions(IEnumerable<CaptureRecord> captures)
    {
        static bool Joins(CaptureRecord c) =>
            c.Kind == CaptureKind.Ser && c.Problem is null && c.Header is not null && c.FirstUtc is not null && c.LastUtc is not null
            && !c.Flags.Any(f => f is "duplicate" or "calibration" or "synthetic");

        var groups = captures.Where(Joins).GroupBy(c => (
            Folder: System.IO.Path.GetDirectoryName(c.Path)?.Replace('\\', '/') ?? "",
            c.Header?.Width, c.Header?.Height, c.Header?.ColorId));
        var sessions = new List<SessionRecord>();
        foreach (var group in groups)
        {
            var chain = new List<CaptureRecord>();
            foreach (var capture in group.OrderBy(c => Utc(c.FirstUtc)))
            {
                if (chain.Count > 0 && Utc(capture.FirstUtc) - Utc(chain[^1].LastUtc) > SessionGap)
                {
                    AddIfSession(chain);
                    chain.Clear();
                }
                chain.Add(capture);
            }
            AddIfSession(chain);

            void AddIfSession(List<CaptureRecord> run)
            {
                if (run.Count < 2)
                {
                    return;
                }
                var first = run[0];
                var folder = group.Key.Folder;
                var name = $"{System.IO.Path.GetFileName(folder)} {Utc(first.FirstUtc).ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}";
                sessions.Add(new SessionRecord(name, folder, group.Key.Width ?? 0, group.Key.Height ?? 0, group.Key.ColorId ?? "",
                    run.Sum(c => c.Header?.FrameCount ?? 0), first.FirstUtc ?? "", run.Max(c => c.LastUtc) ?? "", [.. run.Select(c => c.Path)]));
            }
        }
        sessions.Sort(static (a, b) => string.Compare(a.FirstUtc, b.FirstUtc, StringComparison.Ordinal) is var byTime and not 0
            ? byTime : string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase));
        return sessions;

        static DateTimeOffset Utc(string? iso) => DateTimeOffset.Parse(iso ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    /// <summary>A manifest the survey wrote, read back; null when the file holds none.</summary>
    public static async Task<CorpusManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, PlanetaryCorpusJsonContext.Default.CorpusManifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The manifest's bytes: indented UTF-8 JSON with a final newline, the same bytes for the same manifest.</summary>
    public static byte[] ManifestBytes(CorpusManifest manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, PlanetaryCorpusJsonContext.Default.CorpusManifest);
        return [.. json, (byte)'\n'];
    }

    /// <summary>A path as the manifest writes it: full, with forward slashes.</summary>
    public static string Normalise(string path) => System.IO.Path.GetFullPath(path).Replace('\\', '/');

    private static CaptureRecord ReadSer(string path)
    {
        var length = new FileInfo(path).Length;
        var settingsFile = FindSettings(path);
        var settings = settingsFile is not null ? ParseSettings(File.ReadAllText(settingsFile)) : null;
        try
        {
            var raw = new byte[SerHeader.Size];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.ReadExactly(raw);
            }
            var parsed = SerHeader.Parse(raw);
            var expected = SerHeader.Size + (parsed.FrameCount * parsed.FrameSizeBytes);
            if (length < expected || parsed.FrameCount <= 0)
            {
                return Unreadable(path, null, length, raw, settingsFile, settings,
                    parsed.FrameCount <= 0 ? "the header counts no frames" : $"the file is {length} bytes, shorter than the {expected} its header describes",
                    "truncated", parsed);
            }

            using var reader = SerReader.Open(path);
            var firstFrame = new byte[reader.FrameSizeBytes];
            reader.ReadFrameBytes(0, firstFrame);
            var flags = new List<string>();
            AddCommonFlags(path, settings, reader.Header, flags);
            string timestampsSource;
            string? firstUtc = null, lastUtc = null;
            bool? monotonic = null;
            if (reader.HasTimestamps)
            {
                var times = reader.Timestamps;
                timestampsSource = "trailer";
                firstUtc = Iso(times[0]);
                lastUtc = Iso(times[^1]);
                monotonic = true;
                for (var i = 1; i < times.Length; i++)
                {
                    if (times[i] < times[i - 1])
                    {
                        monotonic = false;
                        break;
                    }
                }
            }
            else
            {
                timestampsSource = "none";
                flags.Add("no-timestamps");
            }

            return new CaptureRecord
            {
                Id = ContentId(raw, length, firstFrame),
                Kind = CaptureKind.Ser,
                Path = Normalise(path),
                LengthBytes = length,
                Header = SerHeaderRecord.From(reader.Header),
                Timestamps = timestampsSource,
                FirstUtc = firstUtc,
                LastUtc = lastUtc,
                FramesPerSecond = reader.FramesPerSecond is { } fps && double.IsFinite(fps) ? Math.Round(fps, 3) : null,
                TimestampsMonotonic = monotonic,
                SettingsFile = settingsFile is not null ? Normalise(settingsFile) : null,
                Settings = settings,
                Flags = [.. flags.Distinct().Order(StringComparer.Ordinal)],
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            return Unreadable(path, null, length, [], settingsFile, settings, ex.Message, null, null);
        }
    }

    private static async Task<IEnumerable<CaptureRecord>> ReadArchiveAsync(string archive, SevenZipTool? sevenZip, TimeSpan timeout, ILogger logger,
        CancellationToken cancellationToken)
    {
        var length = new FileInfo(archive).Length;
        if (sevenZip is null)
        {
            return [Unreadable(archive, null, length, [], null, null, "no 7-Zip to read the archive with", null, null)];
        }
        ImmutableArray<ArchiveMember> members;
        try
        {
            members = await sevenZip.ListAsync(archive, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return [Unreadable(archive, null, length, [], null, null, ex.Message, null, null)];
        }

        var records = new List<CaptureRecord>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = System.IO.Path.GetExtension(member.Path);
            if (extension.Equals(".avi", StringComparison.OrdinalIgnoreCase))
            {
                records.Add(new CaptureRecord
                {
                    Id = ContentId(Encoding.UTF8.GetBytes(member.Path), member.Size, []),
                    Kind = CaptureKind.Avi,
                    Path = Normalise(archive),
                    Member = member.Path.Replace('\\', '/'),
                    LengthBytes = member.Size,
                    Timestamps = "unknown",
                    Flags = [.. PathFlags(member.Path).Append("not-read").Order(StringComparer.Ordinal)],
                });
                continue;
            }
            if (!extension.Equals(".ser", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            logger.LogInformation("Reading {Member} from {Archive}", member.Path, archive);
            try
            {
                var raw = await sevenZip.ReadPrefixAsync(archive, member.Path, SerHeader.Size, timeout, cancellationToken).ConfigureAwait(false);
                if (raw.Length < SerHeader.Size)
                {
                    records.Add(Unreadable(archive, member.Path, member.Size, raw, null, null, "the member is shorter than a SER header", "truncated", null));
                    continue;
                }
                var header = SerHeader.Parse(raw);
                var frameBytes = (int)Math.Min(int.MaxValue - SerHeader.Size, header.FrameSizeBytes);
                var prefix = header.FrameCount > 0
                    ? await sevenZip.ReadPrefixAsync(archive, member.Path, SerHeader.Size + frameBytes, timeout, cancellationToken).ConfigureAwait(false)
                    : raw;
                var flags = new List<string>();
                AddCommonFlags(member.Path, null, header, flags);
                if (member.Size < SerHeader.Size + (header.FrameCount * header.FrameSizeBytes))
                {
                    flags.Add("truncated");
                }
                records.Add(new CaptureRecord
                {
                    Id = ContentId(raw, member.Size, prefix.AsSpan(SerHeader.Size)),
                    Kind = CaptureKind.SerInArchive,
                    Path = Normalise(archive),
                    Member = member.Path.Replace('\\', '/'),
                    LengthBytes = member.Size,
                    Header = SerHeaderRecord.From(header),
                    Timestamps = "unknown",
                    Flags = [.. flags.Distinct().Order(StringComparer.Ordinal)],
                });
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or ArgumentException)
            {
                records.Add(Unreadable(archive, member.Path, member.Size, [], null, null, ex.Message, null, null));
            }
        }
        return records;
    }

    private static CaptureRecord Avi(string path)
    {
        var length = new FileInfo(path).Length;
        return new CaptureRecord
        {
            Id = ContentId(Encoding.UTF8.GetBytes(System.IO.Path.GetFileName(path)), length, []),
            Kind = CaptureKind.Avi,
            Path = Normalise(path),
            LengthBytes = length,
            Timestamps = "unknown",
            Flags = [.. PathFlags(path).Append("not-read").Order(StringComparer.Ordinal)],
        };
    }

    // A folder of FITS frames, when they are a video: lucky imaging is short exposures at a high rate, so the folder is one when
    // its timestamps show a video's rate or, with none, its frames say they are a video's sub-second exposures. Deep-sky subs,
    // flats and a processed set say neither, and are left out, logged with the numbers that decided it.
    private static CaptureRecord? ReadFitsSequence(string folder, List<string> unordered, CorpusSurveyOptions options, ILogger logger)
    {
        var (files, id, length) = SequenceOf(unordered);
        var first = Image.TryReadFitsHeader(files[0], out var firstInfo) ? firstInfo : null;
        var last = Image.TryReadFitsHeader(files[^1], out var lastInfo) ? lastInfo : null;
        string? firstUtc = null, lastUtc = null;
        if (first is { Meta.ExposureStartTime: var firstTime } && firstTime != default
            && last is { Meta.ExposureStartTime: var lastTime } && lastTime != default)
        {
            firstUtc = Iso(firstTime);
            lastUtc = Iso(lastTime);
        }
        var hasTimes = firstUtc is not null;
        var span = hasTimes && first is not null && last is not null ? (last.Meta.ExposureStartTime - first.Meta.ExposureStartTime).TotalSeconds : 0;
        double? rate = span > 0 && files.Length > 1 ? (files.Length - 1) / span : null;
        var exposure = first?.Meta.ExposureDuration ?? TimeSpan.Zero;
        var isVideo = rate is { } fps ? fps >= options.MinFramesPerSecond : exposure > TimeSpan.Zero && exposure < TimeSpan.FromSeconds(1);
        if (!isVideo)
        {
            logger.LogInformation("Not a planetary sequence: {Folder}, {Frames} FITS frames at {Rate} fps, {Exposure} s exposures",
                folder, files.Length, rate is { } r ? Math.Round(r, 3) : "unknown", exposure.TotalSeconds);
            return null;
        }
        var calibration = first?.Meta.FrameType is FrameType.Bias or FrameType.Dark or FrameType.Flat or FrameType.DarkFlat;
        return new CaptureRecord
        {
            Id = id,
            Kind = CaptureKind.FitsSequence,
            Path = Normalise(folder),
            LengthBytes = length,
            Frames = files.Length,
            Width = first?.Width,
            Height = first?.Height,
            Timestamps = hasTimes ? "date-obs" : "none",
            FirstUtc = firstUtc,
            LastUtc = lastUtc,
            FramesPerSecond = rate is { } measured ? Math.Round(measured, 3) : null,
            Flags = [.. PathFlags(folder).Union(calibration ? ["calibration"] : []).Union(hasTimes ? [] : ["no-timestamps"]).Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// A FITS sequence's frames in their order (by name, ignoring case: SharpCap numbers them) with its content id and size:
    /// one answer for the survey and for a conversion, so a converted SER names its source by the manifest's id.
    /// </summary>
    internal static (string[] Files, string Id, long LengthBytes) SequenceOf(IEnumerable<string> files)
    {
        var ordered = files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        long length = 0;
        foreach (var file in ordered)
        {
            length += new FileInfo(file).Length;
        }
        return (ordered, ContentId(Encoding.UTF8.GetBytes(string.Join('\n', ordered.Select(System.IO.Path.GetFileName))), length, []), length);
    }

    /// <summary>A folder's own FITS frames, not its sub-folders', as the survey groups them.</summary>
    internal static IEnumerable<string> FitsFramesIn(string folder) => FileEnumeration.EnumerateFiles(folder, FitsExtensions, recursive: false);

    private static CaptureRecord Unreadable(string path, string? member, long length, byte[] header, string? settingsFile,
        SortedDictionary<string, string>? settings, string problem, string? flag, SerHeader? parsed) => new CaptureRecord
    {
        Id = ContentId(header.Length > 0 ? header : Encoding.UTF8.GetBytes(member ?? System.IO.Path.GetFileName(path)), length, []),
        Kind = member is null ? CaptureKind.Ser : CaptureKind.SerInArchive,
        Path = Normalise(path),
        Member = member?.Replace('\\', '/'),
        LengthBytes = length,
        Header = parsed is { } h ? SerHeaderRecord.From(h) : null,
        Timestamps = "unknown",
        SettingsFile = settingsFile is not null ? Normalise(settingsFile) : null,
        Settings = settings,
        Flags = flag is null ? [] : [flag],
        Problem = problem,
    };

    private static void AddCommonFlags(string path, SortedDictionary<string, string>? settings, in SerHeader header, List<string> flags)
    {
        flags.AddRange(PathFlags(path));
        var synthetic = header.Instrument.Contains("DummyCam", StringComparison.OrdinalIgnoreCase)
            || (settings?.Values.Any(v => v.Contains("DummyCam", StringComparison.OrdinalIgnoreCase)) ?? false);
        if (synthetic)
        {
            flags.Add("synthetic");
        }
    }

    // What a capture's path says of it: "pipp" for a PIPP output (PIPP names its folders and files), "calibration" for one under
    // a folder named for a bias, dark or flat set (SharpCap's bias videos carry no frame type of their own).
    private static IEnumerable<string> PathFlags(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(static part => part.Contains("pipp", StringComparison.OrdinalIgnoreCase)))
        {
            yield return "pipp";
        }
        if (parts.Any(static part => CalibrationFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
        {
            yield return "calibration";
        }
    }

    private static readonly string[] CalibrationFolders = ["Bias", "Biases", "Dark", "Darks", "Flat", "Flats", "DarkFlat", "DarkFlats", "FlatDark", "FlatDarks"];

    // SharpCap writes "<name>.CameraSettings.txt" beside "<name>.ser"; FireCapture writes "<name>.txt".
    private static string? FindSettings(string path)
    {
        var stem = System.IO.Path.ChangeExtension(path, null);
        foreach (var candidate in new[] { stem + ".CameraSettings.txt", stem + ".txt" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string Iso(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}
