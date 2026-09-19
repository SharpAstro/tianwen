using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// What a bake KNOWS it has done for each session, and on what: the ledger a resume reads instead
/// of asking whether files happen to be there.
/// </summary>
/// <remarks>
/// <para><b>"Are the files there" is the wrong question, and it answered wrong in both directions.</b>
/// A resume used to count a session done when its tiles were still on disk and its PSF record
/// existed. That says nothing about whether the INPUTS still match (a light added to the folder, a
/// dark library refreshed, a frame re-typed by a curation pass) or whether the RECIPE that made the
/// outputs is the one that would make them today (a strategy that now writes a coverage sidecar, a
/// drizzle that now shifts every frame onto one sky). Both happened on the 2026-09-19 store: 37
/// staged masters lack the coverage plane every staged master now carries, and nothing short of
/// deleting their files by hand could make a re-bake touch only them.</para>
/// <para>An entry is appended when a session COMPLETES (tiles written, manifest rows appended, PSF
/// record persisted), keyed on the session id with the last record winning like the other stores.
/// It records the <see cref="SessionLedgerEntry.Fingerprint"/> the session was built from; a resume
/// recomputes it from the archive and the options and re-does the session when it differs. The
/// fingerprint is cheap on purpose (one <c>stat</c> per light, never a read): the archive digest
/// store skips re-hashing a file whose size and mtime are unchanged, and this uses the same rule.</para>
/// <para>What the fingerprint covers, and why each: the RECIPE version (bumped by hand when the bake's
/// outputs change for identical inputs; the <c>SimbadMergeSnapshot.AlgorithmVersion</c> precedent);
/// the options that shape a session's outputs; a digest of the whole calibration LIBRARY (the
/// resolver's choice is a pure function of the lights and that library, and hashing the library is
/// what avoids resolving calibration for every session just to decide whether to skip it: a new dark
/// invalidates everything, which is conservative and rare); and every light's path, size and mtime.
/// What it deliberately does NOT cover is the outputs: <c>RetainedMasterStore</c> answers whether a
/// master's sidecars are complete from the files, so a master baked before a strategy learned to
/// write coverage reads as stale by data, with no version bump.</para>
/// </remarks>
public static class DatasetSessionLedger
{
    /// <summary>The store, under the bake's <c>stats/</c>.</summary>
    public const string FileName = "sessions.jsonl";

    /// <summary>
    /// The version of the bake's recipe. <b>Bump it when a bake's outputs change for identical inputs</b>
    /// (a new sidecar, a different default, a normalisation the strategy did not do before), and only
    /// then: every session in every store then reads as stale on the next resume, which is the point,
    /// and the cost. Started at 1 on 2026-09-20 with the ledger itself.
    /// </summary>
    public const int RecipeVersion = 1;

    /// <summary>One completed session.</summary>
    /// <param name="Fingerprint">What the session was built from; see the class remarks.</param>
    /// <param name="TileCount">Tiles exported, for the summary a resumed session contributes.</param>
    /// <param name="TileDirRelative">The session's tile directory relative to the output root.</param>
    public sealed record SessionLedgerEntry(
        string SessionId,
        string Fingerprint,
        int RecipeVersion,
        DateTimeOffset CompletedUtc,
        int TileCount,
        string TileDirRelative);

    public static Task<Dictionary<string, SessionLedgerEntry>> ReadAsync(
        string path, ILogger? logger = null, CancellationToken cancellationToken = default) =>
        JsonLinesFile.ReadLastPerKeyAsync(
            path, DatasetSessionLedgerJsonContext.Default.SessionLedgerEntry, static e => e.SessionId,
            "session ledger", logger, cancellationToken);

    public static Task AppendAsync(string path, SessionLedgerEntry entry, CancellationToken cancellationToken = default) =>
        JsonLinesFile.AppendRecordAsync(path, entry, DatasetSessionLedgerJsonContext.Default.SessionLedgerEntry, cancellationToken);

    /// <summary>Appends an entry and logs rather than throws on failure: a missing entry costs a
    /// future resume its shortcut for that session, never the session itself.</summary>
    public static Task RecordBestEffortAsync(string path, SessionLedgerEntry entry, ILogger? logger = null, CancellationToken cancellationToken = default) =>
        JsonLinesFile.RecordBestEffortAsync(
            path, entry, DatasetSessionLedgerJsonContext.Default.SessionLedgerEntry, "session ledger", logger, cancellationToken);

    /// <summary>
    /// A digest of the calibration library: every calibration frame's path, size and mtime, in path
    /// order. Computed once per run and folded into every session's fingerprint.
    /// </summary>
    public static string CalibrationLibraryDigest(IEnumerable<FrameInfo> calibrationFrames)
    {
        var paths = new List<string>();
        foreach (var frame in calibrationFrames)
        {
            paths.Add(frame.Path);
        }
        paths.Sort(StringComparer.Ordinal);

        var hash = new XxHash128();
        foreach (var path in paths)
        {
            AppendFile(hash, path);
        }
        return ContentDigest.Format(hash.GetCurrentHash());
    }

    /// <summary>
    /// The fingerprint of one session as it would be built now.
    /// </summary>
    /// <param name="session">The session, whose lights are stat-ed (never read).</param>
    /// <param name="calibrationLibraryDigest">From <see cref="CalibrationLibraryDigest"/>.</param>
    /// <param name="recipe">The options that shape this session's outputs, already reduced to a
    /// string by the caller (<c>DatasetBuildOptions.RecipeKey()</c>), so this file needs no view of
    /// the options type and a new option that changes outputs has one place to be added.</param>
    public static string FingerprintOf(ImagingSession session, string calibrationLibraryDigest, string recipe)
    {
        var hash = new XxHash128();
        Append(hash, "recipe-version:" + RecipeVersion);
        Append(hash, "recipe:" + recipe);
        Append(hash, "calibration:" + calibrationLibraryDigest);
        Append(hash, "session:" + session.Id);

        var paths = new List<string>(session.Lights.Length);
        foreach (var light in session.Lights)
        {
            paths.Add(light.Path);
        }
        paths.Sort(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            AppendFile(hash, path);
        }
        return ContentDigest.Format(hash.GetCurrentHash());
    }

    private static void AppendFile(XxHash128 hash, string path)
    {
        // Size and mtime, the archive digest store's own "unchanged" rule; a file that is gone hashes
        // as gone rather than throwing, so a deleted light changes the fingerprint like an added one.
        var info = new FileInfo(path);
        Append(hash, info.Exists
            ? FormattableString.Invariant($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")
            : path + "|missing");
    }

    private static void Append(XxHash128 hash, string text)
    {
        hash.Append(Encoding.UTF8.GetBytes(text));
        hash.Append("\n"u8);
    }
}

[JsonSerializable(typeof(DatasetSessionLedger.SessionLedgerEntry))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class DatasetSessionLedgerJsonContext : JsonSerializerContext;
