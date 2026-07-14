using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Enumeration;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Scans archive roots for raw light frames and groups them into <see cref="ImagingSession"/>s.
/// All gating is header-based (frame type, exposure range, INSTRUME, provenance) with a
/// path-segment exclusion as belt-and-braces for processed-data directories whose frames still
/// carry Light-like headers; duplicates (same camera + DATE-OBS + exposure + dimensions) are
/// kept once, first archive root winning — so callers pass the canonical root first.
/// </summary>
public static class SessionDiscovery
{
    /// <summary>Directory names that hold frames of one type inside a session
    /// (…/&lt;session&gt;/LIGHT/…). The parent of the shallowest such component is the session
    /// directory; such a directory sitting directly under an archive root is a shared
    /// calibration library, not a session.</summary>
    private static readonly ImmutableHashSet<string> FrameDirNames = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "light", "lights", "dark", "darks", "bias", "biases",
        "flat", "flats", "darkflat", "darkflats", "rawframes");

    /// <summary>Per-gate drop counters — reported by the CLI so exclusions are never silent.</summary>
    public sealed record DiscoveryStats(
        int Scanned,
        int NotLight,
        int ExposureOutOfRange,
        int InstrumentExcluded,
        int SoftwareExcluded,
        int ObjectExcluded,
        int PathExcluded,
        int ProductExcluded,
        int Duplicates,
        int SessionsTooSmall,
        int Sessions,
        int Lights);

    /// <summary>Enumerates all archive roots (header-only reads) and groups into sessions.</summary>
    public static async Task<(ImmutableArray<ImagingSession> Sessions, DiscoveryStats Stats)> DiscoverAsync(
        DatasetBuildOptions options, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var frames = new List<(FrameInfo Frame, string Root)>();
        foreach (var root in options.ArchiveRoots)
        {
            var source = new FitsFolderFrameSource(root, true);
            await foreach (var frame in source.EnumerateAsync(cancellationToken))
            {
                frames.Add((frame, root));
            }
            logger?.LogInformation("Scanned {Root}: {Count} FITS headers so far", root, frames.Count);
        }
        return GroupSessions(frames, options);
    }

    /// <summary>Pure grouping core (unit-testable without disk): gate → dedup → session grouping.</summary>
    public static (ImmutableArray<ImagingSession> Sessions, DiscoveryStats Stats) GroupSessions(
        IReadOnlyList<(FrameInfo Frame, string Root)> frames, DatasetBuildOptions options)
    {
        int notLight = 0, exposureOut = 0, instrumentExcluded = 0, softwareExcluded = 0, objectExcluded = 0, pathExcluded = 0, productExcluded = 0, duplicates = 0;
        var seen = new HashSet<(string Camera, DateTimeOffset Start, TimeSpan Exposure, int Width, int Height)>();
        // Grouped per target as well as per directory + camera: a single dated LIGHT folder
        // routinely holds several pointings distinguished only by OBJECT, and mixing them would
        // both break registration and poison the session-relative star-count gate.
        var bySession = new Dictionary<(string SessionDir, string Camera, string Target), (string Root, List<FrameInfo> Lights)>();

        foreach (var (frame, root) in frames)
        {
            switch (ClassifyLight(frame, options))
            {
                case LightGate.NotLight: notLight++; continue;
                case LightGate.ExposureOutOfRange: exposureOut++; continue;
                case LightGate.InstrumentExcluded: instrumentExcluded++; continue;
                case LightGate.SoftwareExcluded: softwareExcluded++; continue;
                case LightGate.ObjectExcluded: objectExcluded++; continue;
                case LightGate.Product: productExcluded++; continue;
            }
            if (IsUnderExcludedPath(frame.Path, root, options))
            {
                pathExcluded++;
                continue;
            }
            if (!seen.Add((frame.Meta.Instrument, frame.Meta.ExposureStartTime, frame.Meta.ExposureDuration, frame.Width, frame.Height)))
            {
                duplicates++;
                continue;
            }
            if (SessionDirOf(frame.Path, root) is not { } sessionDir)
            {
                pathExcluded++;
                continue;
            }
            var key = (sessionDir, frame.Meta.Instrument, TargetOf(frame));
            if (!bySession.TryGetValue(key, out var entry))
            {
                bySession[key] = entry = (root, new List<FrameInfo>());
            }
            entry.Lights.Add(frame);
        }

        int tooSmall = 0, lightCount = 0;
        var sessions = ImmutableArray.CreateBuilder<ImagingSession>();
        foreach (var ((sessionDir, camera, target), (root, lights)) in bySession)
        {
            if (lights.Count < options.MinSubsPerSession)
            {
                tooSmall++;
                continue;
            }
            lights.Sort(static (a, b) => a.Meta.ExposureStartTime.CompareTo(b.Meta.ExposureStartTime));
            var relative = Path.GetRelativePath(root, sessionDir).Replace(Path.DirectorySeparatorChar, '/');
            sessions.Add(new ImagingSession(sessionDir, relative, camera, target, [.. lights]));
            lightCount += lights.Count;
        }
        // Deterministic order regardless of dictionary iteration: by portable id.
        sessions.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));

        var stats = new DiscoveryStats(
            frames.Count, notLight, exposureOut, instrumentExcluded, softwareExcluded, objectExcluded, pathExcluded,
            productExcluded, duplicates, tooSmall, sessions.Count, lightCount);
        return (sessions.ToImmutable(), stats);
    }

    /// <summary>The session-grouping target key: the trimmed OBJECT header, or empty when unset.</summary>
    private static string TargetOf(FrameInfo frame) => frame.Meta.ObjectName?.Trim() ?? "";

    private enum LightGate { Pass, NotLight, ExposureOutOfRange, InstrumentExcluded, SoftwareExcluded, ObjectExcluded, Product }

    private static LightGate ClassifyLight(FrameInfo frame, DatasetBuildOptions options)
    {
        if (frame.FrameType != FrameType.Light)
        {
            return LightGate.NotLight;
        }
        // A stacked product is never a raw sub, whatever tool authored it: TianWen's own outputs
        // carry STACK_N / SWCREATE markers, but a FOREIGN integration (e.g. PixInsight's
        // IMAGETYP='Master Light', which parses as FrameType.Light + IsMaster) has neither --
        // gate on the master flag itself so it can't be ingested as a session frame.
        if (frame.IsMaster || frame.StackedFrameCount > 0 || IntegrationFitsWriter.IsTianWenProduct(frame.Meta.SWCreator))
        {
            return LightGate.Product;
        }
        if (frame.Meta.ExposureDuration < options.MinExposure || frame.Meta.ExposureDuration > options.MaxExposure)
        {
            return LightGate.ExposureOutOfRange;
        }
        if (FileSystemName.MatchesSimpleExpression(options.ExcludeInstrumePattern, frame.Meta.Instrument, ignoreCase: true))
        {
            return LightGate.InstrumentExcluded;
        }
        // SWCREATE include-filter (lights only): keep only lights authored by matching software. An
        // empty pattern disables it; a set pattern (e.g. "*N.I.N.A.*") drops SharpCap/other captures
        // that carry Light-like headers. Calibration frames are never filtered here (GroupCalibration
        // ignores authoring software), so a master dark from any tool still resolves.
        if (options.SoftwareIncludePattern.Length > 0
            && !FileSystemName.MatchesSimpleExpression(options.SoftwareIncludePattern, frame.Meta.SWCreator, ignoreCase: true))
        {
            return LightGate.SoftwareExcluded;
        }
        // Empty pattern disables the gate (MatchesSimpleExpression("", x) only matches empty x,
        // which would never fire on a real OBJECT, but guard explicitly for clarity).
        if (options.ExcludeObjectPattern.Length > 0
            && FileSystemName.MatchesSimpleExpression(options.ExcludeObjectPattern, TargetOf(frame), ignoreCase: true))
        {
            return LightGate.ObjectExcluded;
        }
        return LightGate.Pass;
    }

    internal static bool IsUnderExcludedPath(string path, string root, DatasetBuildOptions options)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return true; // outside the root it was reported under — never ingest
        }
        var directory = Path.GetDirectoryName(relative) ?? "";
        foreach (var segment in EnumerateDirSegments(directory))
        {
            foreach (var pattern in options.ExcludePathSegments)
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, segment.Span, ignoreCase: true))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The session directory owning a frame: the parent of the shallowest frame-type
    /// folder in its relative path, or the file's own directory when frames sit loose. Returns
    /// null for shared libraries (frame-type folder directly under the root) and for files
    /// directly in the root.</summary>
    internal static string? SessionDirOf(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }
        var components = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirCount = components.Length - 1; // last component is the file name
        for (var i = 0; i < dirCount; i++)
        {
            if (FrameDirNames.Contains(components[i]))
            {
                return i > 0 ? Path.Combine(root, Path.Combine(components[..i])) : null;
            }
        }
        return dirCount > 0 ? Path.Combine(root, Path.Combine(components[..dirCount])) : null;
    }

    private static IEnumerable<ReadOnlyMemory<char>> EnumerateDirSegments(string relativePath)
    {
        var start = 0;
        for (var i = 0; i <= relativePath.Length; i++)
        {
            if (i == relativePath.Length || relativePath[i] == Path.DirectorySeparatorChar || relativePath[i] == Path.AltDirectorySeparatorChar)
            {
                if (i > start)
                {
                    yield return relativePath.AsMemory(start, i - start);
                }
                start = i + 1;
            }
        }
    }
}
