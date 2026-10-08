using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TianWen.Lib.Geometry;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The grades a capture FILE's frames were given, kept in a folder so a second stack of the file does not grade it again (#1351): a
/// keep sweep over a 70,000-frame session graded every frame for every arm, though a grade never depends on the keep. What is kept
/// is what is each frame's OWN (<see cref="FrameGrader"/>: its score, whether its planet is cut, its elongation, its brightness); what
/// depends on the run (smeared, dim, the cut frames dropped) is decided again over whatever run the file is read in, so a session reads
/// its files' grades. A file's grades are found by its full path, length and last write time, the estimator's
/// <see cref="IFrameQualityEstimator.CacheKey"/>, the layout its frames are read in, the region and <see cref="Version"/>.
/// </summary>
public sealed class FrameGradeCache
{
    /// <summary>The grading's own version: raised whenever a grade of the same file would come out otherwise.</summary>
    public const int Version = 1;

    private static readonly byte[] Magic = "TWGRADES"u8.ToArray();

    /// <summary>A cache in <paramref name="directory"/>, made when the first grades are written.</summary>
    public FrameGradeCache(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
    }

    /// <summary>The folder the grades are kept in.</summary>
    public string Directory { get; }

    /// <summary>One frame's own grade, before its run is read: what the cache keeps.</summary>
    public readonly record struct OwnGrade(float Score, bool Cut, float Elongation, float Brightness);

    /// <summary>Where <paramref name="capture"/>'s grades by <paramref name="estimatorKey"/> are kept, or null when the file is not there.</summary>
    internal string? EntryFor(string capture, string estimatorKey, PlanetaryFrameLayout layout, PixelRect region)
    {
        var file = new FileInfo(capture);
        if (!file.Exists)
        {
            return null;
        }
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{Version}|{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{estimatorKey}|{layout}|{region.X},{region.Y},{region.Width},{region.Height}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 16);
        return Path.Combine(Directory, $"{Path.GetFileNameWithoutExtension(file.Name)}.{hash}.grades");
    }

    /// <summary>The grades kept at <paramref name="entry"/>, or null when there are none, they are short, or not <paramref name="frames"/> of them.</summary>
    internal static OwnGrade[]? Read(string entry, int frames)
    {
        try
        {
            var bytes = File.ReadAllBytes(entry);
            const int head = 8 + 4 + 4;
            if (bytes.Length != head + (frames * 13) || !bytes.AsSpan(0, 8).SequenceEqual(Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != Version
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)) != frames)
            {
                return null;
            }
            var grades = new OwnGrade[frames];
            for (var i = 0; i < frames; i++)
            {
                var at = bytes.AsSpan(head + (i * 13));
                grades[i] = new OwnGrade(BinaryPrimitives.ReadSingleLittleEndian(at), at[4] != 0,
                    BinaryPrimitives.ReadSingleLittleEndian(at[5..]), BinaryPrimitives.ReadSingleLittleEndian(at[9..]));
            }
            return grades;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps <paramref name="grades"/> at <paramref name="entry"/>: written under a name of its own and renamed into place, so a reader in
    /// another process never sees half a file. A cache that cannot be written is no failure: the next stack grades again.
    /// </summary>
    internal void Write(string entry, ReadOnlySpan<OwnGrade> grades)
    {
        const int head = 8 + 4 + 4;
        var bytes = new byte[head + (grades.Length * 13)];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), grades.Length);
        for (var i = 0; i < grades.Length; i++)
        {
            var at = bytes.AsSpan(head + (i * 13));
            BinaryPrimitives.WriteSingleLittleEndian(at, grades[i].Score);
            at[4] = grades[i].Cut ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteSingleLittleEndian(at[5..], grades[i].Elongation);
            BinaryPrimitives.WriteSingleLittleEndian(at[9..], grades[i].Brightness);
        }
        var staged = $"{entry}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(staged, bytes);
            File.Move(staged, entry, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(staged);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The staged file stays; it is never read, being no entry's name.
            }
        }
    }

    /// <summary>
    /// The capture files <paramref name="stream"/> reads, each with the index of its first frame and its frame count, in the stream's
    /// order; null when the stream reads no file it can name (frames in memory), which is then graded as it always was.
    /// </summary>
    internal static List<(string Path, int Start, int Count)>? FilesOf(IPlanetaryFrameStream stream)
    {
        switch (stream)
        {
            case SerFrameStream { SourcePath: { } path }:
                return [(path, 0, stream.FrameCount)];
            case PlanetaryFrameSequence sequence:
                var files = new List<(string Path, int Start, int Count)>(sequence.PartCount);
                for (var part = 0; part < sequence.PartCount; part++)
                {
                    if (sequence.PartAt(part) is not SerFrameStream { SourcePath: { } partPath })
                    {
                        return null;
                    }
                    files.Add((partPath, sequence.StartOf(part), sequence.CountOf(part)));
                }
                return files;
            default:
                return null;
        }
    }
}
