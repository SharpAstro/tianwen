using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// A hand-authored declaration of capture metadata the capture software never recorded, dropped
/// into an archive directory as <see cref="FrameMetaSidecarResolver.FileName"/>.
///
/// <para><b>Why this exists.</b> N.I.N.A. models a motorised filter wheel and writes its slot name
/// to <c>FILTER</c>. It does not model a filter you screwed onto the nosepiece by hand, so those
/// frames carry no <c>FILTER</c> card at all. That is the worst case for grouping, because a manual
/// holder is exactly how most people shoot a dual-band on an OSC: the frames that most need to be
/// separated from the broadband ones are the frames with nothing to separate them by.</para>
///
/// <para>Declaring it in a sidecar rather than rewriting the FITS headers keeps the archive
/// byte-identical. The archive is the irreplaceable artifact; a text file beside it is not.</para>
/// </summary>
/// <param name="Filter">The filter that was physically in the optical path, written the way you
/// would have configured it in the capture software (e.g. <c>"Antlia ALP-T"</c>, <c>"Ha 3nm"</c>).
/// It is stored exactly as written and canonicalised through <see cref="Imaging.Filter.FromName"/>
/// like any header value, so a recognised name folds onto the canonical filter and anything else
/// stands as its own identity. Null or empty declares nothing.</param>
public sealed record FrameMetaSidecar(string? Filter);

/// <summary>Counters for what the sidecars did during one scan, so a declaration is never silently
/// inert and a typo is never silently ignored.</summary>
/// <param name="Files">Sidecar files successfully parsed.</param>
/// <param name="Malformed">Sidecar files found but unreadable or not valid JSON. A scan never fails
/// on one (that would abort a whole archive sweep over a stray character), so this counter is the
/// only thing standing between a typo and a silently unfiltered night.</param>
/// <param name="FilterFilled">Frames that had no filter of their own and took the declared one.</param>
/// <param name="FilterAlreadyPresent">Frames under a declaring directory that already carried their
/// own filter and were therefore left alone. A large number here means the sidecar is doing nothing
/// and is probably in the wrong place.</param>
public sealed record FrameMetaSidecarStats(int Files, int Malformed, int FilterFilled, int FilterAlreadyPresent)
{
    /// <summary>The zero value, for folding over several archive roots.</summary>
    public static readonly FrameMetaSidecarStats Empty = new(0, 0, 0, 0);

    /// <summary>Sums two scans' counters.</summary>
    public FrameMetaSidecarStats Add(FrameMetaSidecarStats other) => new(
        Files + other.Files,
        Malformed + other.Malformed,
        FilterFilled + other.FilterFilled,
        FilterAlreadyPresent + other.FilterAlreadyPresent);

    /// <summary>True when nothing at all was found, so a caller can skip reporting entirely.</summary>
    public bool IsEmpty => this == Empty;
}

/// <summary>
/// Resolves the <see cref="FrameMetaSidecar"/> in effect for a directory, cascading from the scan
/// root the way <c>.gitignore</c> does: a file applies to its own directory and everything beneath
/// it, and the **nearest** file wins wholesale (a deeper declaration replaces a shallower one rather
/// than merging with it, which stays predictable as fields are added).
///
/// <para>Resolution never escapes the root it was constructed with, so scanning two archive roots
/// cannot leak one's declaration into the other. Results are cached per directory, so a 10,000-frame
/// scan does one <c>File.Exists</c> per directory rather than per frame.</para>
///
/// <para>Not thread-safe: one instance belongs to one sequential scan.</para>
/// </summary>
public sealed class FrameMetaSidecarResolver
{
    /// <summary>The sidecar's file name. Dot-prefixed so it sorts out of the way and reads as
    /// tooling rather than data, and suffixed <c>.json</c> so editors syntax-highlight it.</summary>
    public const string FileName = ".tianwen-meta.json";

    // Windows paths are case-insensitive and the enumerated casing need not match the root the
    // caller passed; elsewhere they are not, and folding case would alias distinct directories.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _root;
    private readonly Dictionary<string, FrameMetaSidecar?> _effective = new(PathComparer);

    /// <param name="root">The scan root. Declarations outside it are never consulted.</param>
    public FrameMetaSidecarResolver(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = Path.GetFullPath(root);
    }

    /// <summary>Sidecar files successfully parsed so far.</summary>
    public int FilesLoaded { get; private set; }

    /// <summary>Sidecar files found but unparseable so far.</summary>
    public int FilesMalformed { get; private set; }

    /// <summary>The declaration in effect for the directory holding <paramref name="framePath"/>.</summary>
    public FrameMetaSidecar? ResolveForFrame(string framePath) => Resolve(Path.GetDirectoryName(framePath));

    /// <summary>The declaration in effect for <paramref name="directory"/>, or null when neither it
    /// nor any ancestor up to the scan root declares one.</summary>
    public FrameMetaSidecar? Resolve(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }
        var full = Path.GetFullPath(directory);
        if (_effective.TryGetValue(full, out var cached))
        {
            return cached;
        }

        // GetRelativePath returns "." for the root itself and a rooted path when there is no
        // relative route at all (a different drive), so this one test covers both escapes.
        var relative = Path.GetRelativePath(_root, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var effective = TryLoad(full)
            ?? (relative == "." ? null : Resolve(Path.GetDirectoryName(full)));
        _effective[full] = effective;
        return effective;
    }

    private FrameMetaSidecar? TryLoad(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var parsed = JsonSerializer.Deserialize(stream, FrameMetaSidecarJsonContext.Default.FrameMetaSidecar);
            if (parsed is null)
            {
                FilesMalformed++;
                return null;
            }
            FilesLoaded++;
            return parsed;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Counted, never thrown: one stray character must not abort a sweep over a whole
            // archive, but it must not pass unnoticed either.
            FilesMalformed++;
            return null;
        }
    }
}

/// <summary>Source-generated (reflection-free, AOT-safe) JSON context for the sidecar. Comments and
/// trailing commas are allowed because this file is written by hand.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(FrameMetaSidecar))]
internal partial class FrameMetaSidecarJsonContext : JsonSerializerContext;
