using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Removes a raw folder once it is <b>truly just a folder of hard links</b>, which is to say once
/// every byte under it has another name somewhere else.
///
/// <para><b>A successful prune frees nothing, by construction.</b> That is not a disappointment, it
/// is the safety property: the condition for removing a name is that the bytes keep another one, so
/// the space was already reclaimed by <see cref="ArchiveLinkSweep"/> when the two names became one
/// file. This pass exists to retire a tree that has become pure indirection, not to recover disk.
/// Anything that reports bytes recovered here is deleting the last copy of something.</para>
///
/// <para><b>"Another name" must be OUTSIDE the folder.</b> A link count of two says a second name
/// exists, not where it is. Two names inside the same doomed folder satisfy every count-based test
/// and still lose the data when the folder goes, so the test is on the link's PATH and nothing
/// else. This is the whole reason the pass reads <see cref="HardLinkProbe.EnumerateLinks"/> rather
/// than <c>LinkCount</c>.</para>
///
/// <para><b>Every file counts, not just the frames.</b> SharpCap writes a
/// <c>*.CameraSettings.txt</c> beside each capture run recording the white balance, the cooler
/// power and the target temperature, and nothing else in the archive records any of them: those
/// sidecars were never curated and so are never linked. Holding every file to the same rule is what
/// stops a prune taking them, which a frames-only rule would have done silently.</para>
///
/// <para>Junctions are neither followed nor removed. The curated archive's <c>targets/</c> tree is
/// a junction farm, and a walk that enters one counts the same file twice; a folder containing one
/// is refused rather than guessed about.</para>
/// </summary>
public static class ArchivePruneSweep
{
    /// <summary>What happened to one folder, or what would happen on a dry run.</summary>
    public enum PruneOutcome
    {
        /// <summary>Every file under it has a name elsewhere, so the folder went and nothing was lost.</summary>
        Pruned,
        /// <summary>At least one file has no other name. Removing the folder would destroy it.</summary>
        WouldOrphan,
        /// <summary>The folder holds a junction, so what a walk would include is ambiguous.</summary>
        HoldsJunction,
        /// <summary>The folder was named through a junction, a symbolic link or an 8.3 short name,
        /// not by its real path. Refused, because "has a name outside this folder" is a test on the
        /// link's REAL path, and against an alias every file's own name looks like one elsewhere.</summary>
        NotItsRealPath,
        /// <summary>Nothing under it at all. Left alone; an empty folder is not this pass's business.</summary>
        Empty,
        /// <summary>The folder or something under it could not be read.</summary>
        Unreadable,
        /// <summary>The delete was attempted and failed part way. The detail says what remains.</summary>
        Failed,
    }

    /// <param name="Folder">The folder considered.</param>
    /// <param name="Outcome">What happened, or would happen on a dry run.</param>
    /// <param name="Detail">Why, naming the files that block it. Empty when it pruned.</param>
    /// <param name="Files">Files found under it.</param>
    /// <param name="Orphans">Files with no name outside the folder. Zero is the condition to prune.</param>
    /// <param name="OrphanBytes">What those files hold, which is what a forced delete would destroy.</param>
    public readonly record struct FolderVerdict(
        string Folder, PruneOutcome Outcome, string Detail, int Files, int Orphans, long OrphanBytes);

    /// <summary>How many blocking files to name in <see cref="FolderVerdict.Detail"/> before
    /// summarising. Enough to see the pattern, short enough to read.</summary>
    private const int NamedBlockers = 5;

    /// <summary>
    /// Decides one folder and, when <paramref name="apply"/> is true and the answer is yes, removes
    /// it. The verdict is identical either way, so a dry run reports exactly what a real run does.
    /// </summary>
    /// <param name="folder">The folder to consider, with everything under it.</param>
    /// <param name="apply">When false (the default) nothing is deleted.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static FolderVerdict Consider(
        string folder, bool apply = false, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(folder);
        if (!Directory.Exists(full))
        {
            return new FolderVerdict(folder, PruneOutcome.Unreadable, "the folder does not exist.", 0, 0, 0);
        }

        // The test below compares every file's names against this folder's path, and the names come
        // back REAL. Named through an alias (a junction anywhere above it, as in the curated archive's
        // targets/ farm, or a short name), a file's own real name does not start with the alias, so
        // it reads as a name elsewhere and the folder's only copy would be deleted. Refuse unless the
        // path given is the path the file system itself uses. Off Windows there is no answer, but
        // there are no link names either, so every file is an orphan and nothing is ever removed.
        if (OperatingSystem.IsWindows())
        {
            if (HardLinkProbe.TryGetFinalPath(full) is not { } real)
            {
                return new FolderVerdict(folder, PruneOutcome.Unreadable,
                    "its real path could not be resolved, so nothing about it can be decided.", 0, 0, 0);
            }
            if (!string.Equals(
                    real.TrimEnd(Path.DirectorySeparatorChar), full.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new FolderVerdict(folder, PruneOutcome.NotItsRealPath,
                    $"it is named through a link or a short name; its real path is {real}. Name that one.", 0, 0, 0);
            }
        }

        List<string> files;
        try
        {
            if (HoldsAJunction(full, cancellationToken))
            {
                return new FolderVerdict(folder, PruneOutcome.HoldsJunction,
                    "something under it is a junction, so what a walk covers is ambiguous.", 0, 0, 0);
            }
            files = Walk(full, cancellationToken).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderVerdict(folder, PruneOutcome.Unreadable, ex.Message, 0, 0, 0);
        }

        if (files.Count == 0)
        {
            return new FolderVerdict(folder, PruneOutcome.Empty, "nothing under it.", 0, 0, 0);
        }

        var orphans = new List<string>();
        long orphanBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasANameOutside(file, full))
            {
                orphans.Add(file);
                try
                {
                    orphanBytes += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        if (orphans.Count > 0)
        {
            var named = string.Join(", ", orphans.Take(NamedBlockers).Select(Path.GetFileName));
            var more = orphans.Count > NamedBlockers ? $" and {orphans.Count - NamedBlockers} more" : "";
            return new FolderVerdict(folder, PruneOutcome.WouldOrphan,
                $"{orphans.Count} of {files.Count} file(s) have no name outside this folder: {named}{more}.",
                files.Count, orphans.Count, orphanBytes);
        }

        if (!apply)
        {
            return new FolderVerdict(folder, PruneOutcome.Pruned, "", files.Count, 0, 0);
        }

        // Never a recursive delete. The verdict above is a walk that took time, and a recursive delete
        // removes whatever is there NOW: a file that arrived after the walk, or one whose outside name
        // went away in the meantime, would go with no check at all. So each file is asked again at the
        // moment it is deleted, and directories go only once empty, so anything that was not checked
        // keeps its folder.
        var removed = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasANameOutside(file, full))
            {
                return new FolderVerdict(folder, PruneOutcome.Failed,
                    $"{file} lost its name outside this folder after the check, so the pass stopped. " +
                    $"{removed} of {files.Count} names removed, each with another name kept.",
                    files.Count, 1, 0);
            }
            try
            {
                // A read-only file fails here, deliberately: clearing the attribute would change it on
                // EVERY name of the file, the curated one included, since attributes belong to the file.
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FolderVerdict(folder, PruneOutcome.Failed,
                    $"{ex.Message} {removed} of {files.Count} names removed, each with another name kept.",
                    files.Count, 0, 0);
            }
        }

        foreach (var dir in DirectoriesDeepestFirst(full).Append(full))
        {
            try
            {
                Directory.Delete(dir, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FolderVerdict(folder, PruneOutcome.Failed,
                    $"{dir} could not be removed ({ex.Message}); something arrived after the check, or is " +
                    $"held open, and was left alone. All {files.Count} checked names were removed.",
                    files.Count, 0, 0);
            }
        }

        return new FolderVerdict(folder, PruneOutcome.Pruned, "", files.Count, 0, 0);
    }

    /// <summary>Every directory under <paramref name="folder"/>, children before their parents, so a
    /// non-recursive delete in this order empties the tree from the leaves up. Junctions were refused
    /// before this runs, so none is entered.</summary>
    private static IEnumerable<string> DirectoriesDeepestFirst(string folder)
    {
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly).ToList())
        {
            foreach (var child in DirectoriesDeepestFirst(dir))
            {
                yield return child;
            }
            yield return dir;
        }
    }

    /// <summary>True when any name for this file lies outside <paramref name="folder"/>. Reads the
    /// link's PATH rather than the link COUNT, because a count cannot tell a name elsewhere from a
    /// second name in the folder about to be deleted.</summary>
    private static bool HasANameOutside(string file, string folder)
    {
        var links = HardLinkProbe.EnumerateLinks(file);
        if (links.IsDefaultOrEmpty)
        {
            // No answer is not the same as no other name. Refuse rather than guess: the cost of
            // being wrong here is the last copy of a frame.
            return false;
        }

        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        foreach (var link in links)
        {
            var linkFull = Path.GetFullPath(link);
            if (!linkFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HoldsAJunction(string folder, CancellationToken cancellationToken)
    {
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsJunction(dir) || HoldsAJunction(dir, cancellationToken))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsJunction(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>Every file under the folder, not entering a reparse point. Deliberately not the
    /// <c>SearchOption.AllDirectories</c> overload, whose legacy defaults do enter one.</summary>
    private static IEnumerable<string> Walk(string folder, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsJunction(dir))
            {
                continue;
            }
            foreach (var file in Walk(dir, cancellationToken))
            {
                yield return file;
            }
        }
    }
}
