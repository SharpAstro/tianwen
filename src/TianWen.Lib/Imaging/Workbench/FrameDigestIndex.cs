using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// The digest of every frame a project's sets of frames hold, remembered beside the step store under each file's
/// path, size and last write time, so the key of a night is not the reading of a night on every status
/// (docs/plans/stacking-workbench.md, W1; #2467, the owner's call of 2026-11-23).
/// </summary>
/// <remarks>
/// <para><b>The key is still made of content.</b> <see cref="KeyOf"/> digests a set from each frame's place in it and
/// the digest of that frame's bytes. The index only says which frames need not be read again to know theirs: one whose
/// size and time are the ones it was digested under. A frame written again with the same bytes under a new time costs
/// one read and changes no key.</para>
/// <para><b>A stamp is not a checksum</b>, as <see cref="FitsHeaderIndex"/> says of itself, which made the same trade
/// for the same disk: bytes changed in place under an unchanged size and time are served stale. Asking with
/// <c>verify</c> reads every frame and repairs the index.</para>
/// <para><b>Why it exists:</b> the archive's USB disk gives a night of 30 GB at about 100 MB a second, so a key read
/// from the bytes is minutes on every status and paid again by the run that then stacks them.</para>
/// <para>One runner uses an instance at a time. Two programs on one project are safe: the file is updated under its
/// folder's lock (<see cref="SharedFile.UpdateJsonAsync"/>), and both would write the same digest for the same
/// frame.</para>
/// </remarks>
public sealed class FrameDigestIndex
{
    /// <summary>The index's file inside the step store's folder.</summary>
    public const string FileName = "frames.json";

    // Part of every set's key text; another layout is another number, so no key of the old layout meets a new one.
    private const string Layout = "tianwen-frames/1";

    private readonly string _file;
    private readonly Dictionary<string, FrameDigestEntry> _entries;

    // The folders KeyOf walked this time, each with the files it found there: what SaveAsync drops the rest of.
    private readonly Dictionary<string, HashSet<string>> _walked = new(PathComparer);
    private bool _dirty;

    // The key each (folder, verify) was made as, for the life of this instance: two images that name one folder read
    // it once, and a verify's reading serves a plain ask as well.
    private readonly Dictionary<(string Root, bool Verify), string> _keys = new();

    // The folders the project's images name; null when the caller never said, which prunes nothing.
    private List<string>? _named;

    private FrameDigestIndex(string file, Dictionary<string, FrameDigestEntry> entries)
    {
        _file = file;
        _entries = entries;
    }

    /// <summary>
    /// What a set of frames is made of: every FITS file the stacker's scan reads
    /// (<see cref="FitsFolderFrameSource.FitsExtensions"/>), and the sidecar that tells it a folder's filter
    /// (<see cref="FrameMetaSidecarResolver.FileName"/>). Any other file in the folder is no part of the set.
    /// </summary>
    public static ImmutableArray<string> FrameFileEndings { get; } = [.. FitsFolderFrameSource.FitsExtensions, FrameMetaSidecarResolver.FileName];

    /// <summary>Frames whose digest came from the index, over every set asked for.</summary>
    public int FromIndex { get; private set; }

    /// <summary>True when the last <see cref="SaveAsync"/> could not write the index (the folder is read-only, the disk is
    /// full, a lock outlasted its budget); the run is unaffected and the next save tries again.</summary>
    public bool NotSaved { get; private set; }

    /// <summary>Frames that were read to know their digest.</summary>
    public int Digested { get; private set; }

    /// <summary>Opens the index beside the store in <paramref name="storeFolder"/>. Nothing is created, and an index
    /// that is missing or cannot be read holds nothing: every frame is then read once.</summary>
    public static async Task<FrameDigestIndex> OpenAsync(string storeFolder, CancellationToken cancellationToken = default)
    {
        var file = Path.Combine(Path.GetFullPath(storeFolder), FileName);
        var entries = new Dictionary<string, FrameDigestEntry>(PathComparer);
        await using var stream = await SharedFile.TryOpenReadAsync(file, cancellationToken);
        if (stream is not null)
        {
            try
            {
                var read = await JsonSerializer.DeserializeAsync(stream, WorkbenchJsonContext.Default.FrameDigestIndexFile, cancellationToken);
                foreach (var entry in EntriesOf(read))
                {
                    entries[entry.Path] = entry;
                }
            }
            catch (JsonException)
            {
                // An index that cannot be read knows nothing: every frame is read once and the next save replaces it.
                entries.Clear();
            }
        }

        return new FrameDigestIndex(file, entries);
    }

    /// <summary>
    /// Says which folders the project's images name, so <see cref="SaveAsync"/> drops what the index holds under any
    /// other: a project pointed from one night to the next does not keep the old night for good. A folder is named
    /// whether or not it is there or could be walked, so one that is unmounted loses nothing.
    /// </summary>
    public void KeepOnly(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _named = [.. folders.Select(static folder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))];
    }

    /// <summary>
    /// The key of the set of frames in <paramref name="folder"/> and every folder under it: each frame's place in the
    /// set and the digest of its bytes, in the order of the places. Empty, with the reason in words, when a frame
    /// cannot be read or the folder holds no frame.
    /// </summary>
    /// <param name="folder">The set's folder.</param>
    /// <param name="verify">Read every frame, whatever the index holds for it, and correct the index.</param>
    /// <param name="refusal">Why there is no key; null when there is one.</param>
    /// <param name="cancellationToken">Ends the walk between two frames.</param>
    /// <remarks>A frame's place is its path inside the folder, so a frame renamed is another set: the scan reads a
    /// rejection and a suspicion of cloud off a frame's name.</remarks>
    public string KeyOf(string folder, bool verify, out string? refusal, CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (_keys.TryGetValue((root, true), out var known0) || (!verify && _keys.TryGetValue((root, false), out known0)))
        {
            refusal = null;
            return known0;
        }

        var seen = new HashSet<string>(PathComparer);
        var frames = new List<(string Place, string Digest)>();
        var images = 0;
        foreach (var stamp in FileEnumeration.EnumerateFileStamps(root, FrameFileEndings, recursive: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen.Add(stamp.Path);
            var ticks = stamp.LastWriteTimeUtc.Ticks;
            string digest;
            if (!verify && _entries.TryGetValue(stamp.Path, out var known) && known.Bytes == stamp.Length && known.LastWriteUtcTicks == ticks
                && known.Digest.Length > 0)
            {
                digest = known.Digest;
                FromIndex++;
            }
            else
            {
                digest = ContentDigest.OfFile(stamp.Path);
                Digested++;
                if (digest.Length == 0)
                {
                    refusal = $"a frame of it cannot be read: {stamp.Path}";
                    return string.Empty;
                }

                var entry = new FrameDigestEntry { Path = stamp.Path, Bytes = stamp.Length, LastWriteUtcTicks = ticks, Digest = digest };
                if (!_entries.TryGetValue(stamp.Path, out var before) || before != entry)
                {
                    _entries[stamp.Path] = entry;
                    _dirty = true;
                }
            }

            if (!string.Equals(Path.GetFileName(stamp.Path), FrameMetaSidecarResolver.FileName, StringComparison.OrdinalIgnoreCase))
            {
                images++;
            }

            frames.Add((Path.GetRelativePath(root, stamp.Path).Replace('\\', '/'), digest));
        }

        if (images == 0)
        {
            // An unmounted mount point, a tree of junctions or a mistyped folder: not a set, and not a reason to forget
            // what the index holds under it, which is what the disk coming back needs.
            refusal = $"it holds no frame: {root}";
            return string.Empty;
        }

        _walked[root] = seen;
        frames.Sort(static (a, b) => string.CompareOrdinal(a.Place, b.Place));
        var text = new StringBuilder(Layout).Append('\n');
        foreach (var (place, digest) in frames)
        {
            text.Append("frame=").Append(place).Append('\t').Append(digest).Append('\n');
        }

        refusal = null;
        var key = ContentDigest.Format(XxHash128.Hash(Encoding.UTF8.GetBytes(text.ToString())));
        _keys[(root, verify)] = key;
        return key;
    }

    /// <summary>Writes the index when a digest was learned or a frame is gone from a folder that was walked. A
    /// status saves it too: what was read to say where a project stands is not read again to run it.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (root, seen) in _walked)
        {
            foreach (var gone in _entries.Keys.Where(path => IsUnder(path, root) && !seen.Contains(path)).ToList())
            {
                _entries.Remove(gone);
                _dirty = true;
            }
        }

        if (_named is { } named)
        {
            foreach (var orphan in _entries.Keys.Where(path => !named.Any(root => IsUnder(path, root))).ToList())
            {
                _entries.Remove(orphan);
                _dirty = true;
            }
        }

        if (!_dirty)
        {
            return;
        }

        try
        {
            if (Path.GetDirectoryName(_file) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            await SharedFile.UpdateJsonAsync(_file, WorkbenchJsonContext.Default.FrameDigestIndexFile, Merged, logger: null, cancellationToken);
            _dirty = false;
            NotSaved = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index is an optimisation: a read-only folder, a full disk or a lock held past its budget costs the next
            // run a second read of the frames, never the run. _dirty stays set, so the next save tries again.
            NotSaved = true;
        }
    }

    // The file as it is to be: this index's entries, and what another program wrote meanwhile for folders this one did
    // not walk. In the order of the paths, so the file reads the same whoever wrote it.
    private FrameDigestIndexFile Merged(FrameDigestIndexFile? onDisk)
    {
        var merged = new Dictionary<string, FrameDigestEntry>(_entries, PathComparer);
        foreach (var entry in EntriesOf(onDisk))
        {
            if (!merged.ContainsKey(entry.Path) && !_walked.Keys.Any(root => IsUnder(entry.Path, root))
                && (_named is null || _named.Any(root => IsUnder(entry.Path, root))))
            {
                merged[entry.Path] = entry;
            }
        }

        return new FrameDigestIndexFile { Frames = [.. merged.Values.OrderBy(static entry => entry.Path, StringComparer.Ordinal)] };
    }

    private static ImmutableArray<FrameDigestEntry> EntriesOf(FrameDigestIndexFile? file)
        => file is null || file.Frames.IsDefault
            ? []
            : [.. file.Frames.Where(static entry => entry is { Path.Length: > 0, Digest.Length: > 0 })];

    // Two names of one file, as the file system this runs on compares them.
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static bool IsUnder(string path, string root)
        => path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
