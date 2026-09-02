using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;

namespace TianWen.Lib.IO
{
    /// <summary>
    /// The one way this repository walks a directory for files. Every production scan (the archive and
    /// dataset walks, the stacker's output folder, the viewer's folder list, the persistence folders) goes
    /// through here rather than through <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not the <see cref="SearchOption"/> overloads.</b> They run with the legacy defaults:
    /// they ENTER every reparse point, so a junction farm such as <c>D:\Astro-Organized\targets</c> is
    /// scanned once per link (6,438 lights read twice and then dropped as duplicates, measured 2026-08-15)
    /// and a scratch junction into the archive turns a scan of the scratch into a scan of the archive;
    /// they abort the whole walk on the first directory the caller may not read; and their pattern casing
    /// is platform-default, so <c>*.fits</c> finds <c>X.FITS</c> on Windows and misses it on Linux.</para>
    ///
    /// <para><b>What this does instead.</b> <see cref="FileSystemEnumerable{TResult}"/> over the directory
    /// index: names, sizes and attributes come back per entry with no per-file open, which is what makes
    /// a walk over a million tiles take a second rather than a stat storm (msys <c>du</c> needed 35 minutes
    /// on the same tree, one open per file). Reparse points are neither listed nor entered
    /// (<see cref="FileAttributes.ReparsePoint"/> in <see cref="EnumerationOptions.AttributesToSkip"/>),
    /// inaccessible directories are skipped rather than fatal, the buffer is 64 KiB (fewer round trips on a
    /// spindle), hidden and system files are INCLUDED (the new API skips them by default; the legacy
    /// overloads never did, and a capture tool may mark a folder however it likes), and extensions match
    /// by ordinal-ignore-case name SUFFIX on every OS. A suffix rather than
    /// <see cref="Path.GetExtension(string)"/> because <c>.fits.gz</c> is one extension here.</para>
    ///
    /// <para><b>The one change a caller might not expect:</b> a symbolic link or junction inside the tree
    /// is invisible. Hard links are not reparse points and are unaffected, which is why the hard-linked
    /// Vela and date trees under <c>D:\Astro-Pics</c> both still scan. Point a walk at the real directory.
    /// A root that IS a link still opens, since the skip applies to entries below it.</para>
    ///
    /// <para>Results are unordered, as the file system hands them out. Callers that need determinism sort
    /// with <see cref="StringComparer.OrdinalIgnoreCase"/>, as the frame source does for reference-frame
    /// selection.</para>
    /// </remarks>
    public static class FileEnumeration
    {
        /// <summary>Directory-listing buffer. 64 KiB is enough that a 3,000-entry tile folder comes back in a
        /// handful of round trips; the framework default is 4 KiB.</summary>
        public const int BufferSize = 64 * 1024;

        /// <summary>The options every walk in this repository uses; see the class remarks for each choice.</summary>
        public static EnumerationOptions Options(bool recursive) => new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            BufferSize = BufferSize,
            ReturnSpecialDirectories = false,
        };

        /// <summary>Full paths of the files under <paramref name="root"/> whose name ends with
        /// <paramref name="extension"/> (ordinal, ignoring case). Unordered.</summary>
        public static IEnumerable<string> EnumerateFiles(string root, string extension, bool recursive)
        {
            ArgumentException.ThrowIfNullOrEmpty(extension);
            return EnumerateFiles(root, [extension], recursive);
        }

        /// <summary>Full paths of the files under <paramref name="root"/> whose name ends with any of
        /// <paramref name="extensions"/> (ordinal, ignoring case). Unordered.</summary>
        public static IEnumerable<string> EnumerateFiles(string root, IReadOnlyList<string> extensions, bool recursive)
        {
            ArgumentNullException.ThrowIfNull(extensions);
            return Enumerate(root, recursive, (ref FileSystemEntry entry) => !entry.IsDirectory && HasExtension(entry.FileName, extensions));
        }

        /// <summary>Full paths of every entry under <paramref name="root"/> that <paramref name="include"/>
        /// accepts. The predicate sees directories too; test <see cref="FileSystemEntry.IsDirectory"/> when
        /// only files are wanted. Unordered.</summary>
        public static IEnumerable<string> Enumerate(string root, bool recursive, FileSystemEnumerable<string>.FindPredicate include)
        {
            ArgumentException.ThrowIfNullOrEmpty(root);
            ArgumentNullException.ThrowIfNull(include);
            return new FileSystemEnumerable<string>(root, static (ref FileSystemEntry entry) => entry.ToFullPath(), Options(recursive))
            {
                ShouldIncludePredicate = include,
            };
        }

        /// <summary>Number of files under <paramref name="root"/> whose name ends with
        /// <paramref name="extension"/>, without allocating a path per file.</summary>
        public static int CountFiles(string root, string extension, bool recursive)
        {
            ArgumentException.ThrowIfNullOrEmpty(root);
            ArgumentException.ThrowIfNullOrEmpty(extension);
            string[] extensions = [extension];
            var counted = new FileSystemEnumerable<int>(root, static (ref FileSystemEntry _) => 1, Options(recursive))
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && HasExtension(entry.FileName, extensions),
            };
            var count = 0;
            foreach (var one in counted)
            {
                count += one;
            }
            return count;
        }

        /// <summary>True when <paramref name="fileName"/> ends with any of <paramref name="extensions"/>,
        /// ordinal and ignoring case. Public because the predicate is the whole contract: a caller composing
        /// its own <see cref="Enumerate"/> filter should test the same way.</summary>
        public static bool HasExtension(ReadOnlySpan<char> fileName, IReadOnlyList<string> extensions)
        {
            for (var i = 0; i < extensions.Count; i++)
            {
                if (fileName.EndsWith(extensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
