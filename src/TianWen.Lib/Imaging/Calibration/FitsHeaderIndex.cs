using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// The raw FITS headers of one archive root, remembered between scans and keyed on each file's path,
/// size and last write time, so a rescan re-reads only the files that changed.
/// </summary>
/// <remarks>
/// <para><b>Why:</b> a scan reads one header per file, and on a hard disk each read is a seek. The
/// 2026-09-24 re-bake read 35,000 headers from the archive's USB disk at about 15 a second: 26 minutes
/// before its first session, repeated on every bake that could not rely on a warm file cache.</para>
/// <para><b>It stores header BYTES, not parsed frames,</b> and a hit is parsed exactly as a read would
/// be (<see cref="Image.TryReadFitsHeaderFromBytes"/>). So there is still one header parse, a parser
/// that learns a new card reads it out of every cached header too, and nothing about the parse ever
/// has to be invalidated. Only a header that replays is stored: the image in the primary HDU of an
/// uncompressed file, checked on the way in by parsing the stored bytes and comparing the result
/// with the file read's. Anything else is read from the file on every scan, as before.</para>
/// <para><b>A stamp is not a checksum.</b> A file rewritten in place to the same size within the same
/// write-time tick would be served stale; every editor in this repository writes a new file and
/// renames it, which moves the time.</para>
/// </remarks>
public sealed class FitsHeaderIndex
{
    /// <summary>Bumped when the file layout changes; a file of another version is ignored.</summary>
    public const int FormatVersion = 1;

    private static readonly byte[] Magic = "TWHX"u8.ToArray();

    private readonly string _file;
    private readonly Dictionary<string, Entry> _entries;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _dirty;

    private FitsHeaderIndex(string file, Dictionary<string, Entry> entries)
    {
        _file = file;
        _entries = entries;
    }

    /// <summary>Headers served from the index this scan.</summary>
    public int Hits { get; private set; }

    /// <summary>Headers the scan had to read from their files.</summary>
    public int Misses { get; private set; }

    /// <summary>Headers the index holds.</summary>
    public int Count => _entries.Count;

    /// <summary>The index file for an archive root under <paramref name="directory"/>: one file per
    /// root, named for the root's full path, so two roots never share or overwrite one index.</summary>
    public static string PathFor(string directory, string archiveRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archiveRoot)).ToUpperInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
        return Path.Combine(directory, $"{hash}.headers");
    }

    /// <summary>Loads an index, or starts an empty one when the file is missing, of another version or
    /// unreadable: an index is only ever an acceleration.</summary>
    public static FitsHeaderIndex Load(string file, ILogger? logger = null)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!File.Exists(file))
        {
            return new FitsHeaderIndex(file, entries);
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(brotli, Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion)
            {
                logger?.LogInformation("Header index {File} is of another format; starting it afresh", file);
                return new FitsHeaderIndex(file, new Dictionary<string, Entry>(StringComparer.Ordinal));
            }

            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var path = reader.ReadString();
                var length = reader.ReadInt64();
                var ticks = reader.ReadInt64();
                var header = reader.ReadBytes(reader.ReadInt32());
                entries[path] = new Entry(length, ticks, header);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Header index {File} could not be read; starting it afresh", file);
            entries.Clear();
        }

        return new FitsHeaderIndex(file, entries);
    }

    /// <summary>The stored header for <paramref name="stamp"/>, when the file has not changed since
    /// it was stored.</summary>
    public bool TryGet(FileStamp stamp, [NotNullWhen(true)] out byte[]? header)
    {
        _seen.Add(stamp.Path);
        if (_entries.TryGetValue(stamp.Path, out var entry)
            && entry.Length == stamp.Length
            && entry.LastWriteTicks == stamp.LastWriteTimeUtc.Ticks)
        {
            Hits++;
            header = entry.Header;
            return true;
        }

        Misses++;
        header = null;
        return false;
    }

    /// <summary>
    /// Stores the first <paramref name="headerBytes"/> bytes of the file as its header, if parsing
    /// them reproduces <paramref name="parsed"/> (the file read's own answer). A header that does not
    /// replay is simply not stored.
    /// </summary>
    public void Remember(FileStamp stamp, int headerBytes, FrameInfo parsed)
    {
        if (headerBytes <= 0 || headerBytes > stamp.Length)
        {
            return;
        }

        byte[] header;
        try
        {
            header = new byte[headerBytes];
            using var file = new FileStream(stamp.Path, FileMode.Open, FileAccess.Read, FileShare.Read, headerBytes);
            file.ReadExactly(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (Image.TryReadFitsHeaderFromBytes(stamp.Path, header, out var replayed) && SameFrame(replayed, parsed))
        {
            _entries[stamp.Path] = new Entry(stamp.Length, stamp.LastWriteTimeUtc.Ticks, header);
            _dirty = true;
        }
    }

    /// <summary>Two parses of one header are the same frame. Record equality, except for the one array
    /// <see cref="ImageMeta"/> carries, which records compare by reference.</summary>
    internal static bool SameFrame(FrameInfo a, FrameInfo b)
    {
        var matrixA = a.Meta.CameraToSrgbMatrix;
        var matrixB = b.Meta.CameraToSrgbMatrix;
        if ((matrixA is null) != (matrixB is null) || (matrixA is not null && matrixB is not null && !matrixA.SequenceEqual(matrixB)))
        {
            return false;
        }

        return a with { Meta = a.Meta with { CameraToSrgbMatrix = null } } == b with { Meta = b.Meta with { CameraToSrgbMatrix = null } };
    }

    /// <summary>
    /// Writes the index back when it learned anything, dropping entries whose file this scan did not
    /// see and that no longer exists. Written to a temporary file and moved into place, so a crash or a
    /// concurrent reader never sees half an index.
    /// </summary>
    public void Save(ILogger? logger = null)
    {
        var gone = _entries.Keys.Where(p => !_seen.Contains(p) && !File.Exists(p)).ToList();
        foreach (var path in gone)
        {
            _entries.Remove(path);
        }

        if (!_dirty && gone.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file) ?? ".");
            var temporary = _file + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var brotli = new BrotliStream(stream, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(brotli, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(_entries.Count);
                foreach (var (path, entry) in _entries)
                {
                    writer.Write(path);
                    writer.Write(entry.Length);
                    writer.Write(entry.LastWriteTicks);
                    writer.Write(entry.Header.Length);
                    writer.Write(entry.Header);
                }
            }
            File.Move(temporary, _file, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Header index {File} could not be written; the next scan reads every header again", _file);
        }
    }

    private readonly record struct Entry(long Length, long LastWriteTicks, byte[] Header);
}
