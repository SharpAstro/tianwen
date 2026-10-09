using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A source guard: no array is copied by casting what <see cref="Array.Clone"/> returns, whose type is <see cref="object"/>, so the
/// copy is a cast (the owner, 2026-10-09). The cast says nothing about why a copy is made, and most such copies were not needed:
/// a reader takes the array itself or a <see cref="ReadOnlySpan{T}"/>, and a routine that reorders or overwrites its input reads a
/// scratch from <see cref="ArrayPoolHelper"/>. A copy that IS needed is written typed: <c>float[] copy = [.. source];</c> for one
/// dimension, <see cref="ArrayCopyExtensions"/>' <c>Copy()</c> for two. The 99 on main (95 lines) were removed in one pass, each
/// reviewed for whether it was needed at all.
/// </summary>
public class NoCastArrayCloneTests
{
    // An array-typed cast whose statement ends in a clone, or a clone cast with "as". Built from parts, so this file does not
    // match its own search.
    private static readonly Regex CastClone = new(
        @"\(\s*[A-Za-z0-9_.<>?]+(\s*\[[,\s]*\])+\s*\)\s*\(?[^;]*\." + "Clone" + @"\(\)|\." + "Clone" + @"\(\)\s+as\s",
        RegexOptions.Compiled);

    /// <summary>
    /// Walks up from the test binary to the repository's <c>src</c>. Null when the sources are not beside the binary (a packaged
    /// run), and the test then skips: not seeing the source is not a regression.
    /// </summary>
    private static string? FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (File.Exists(Path.Combine(candidate, "TianWen.slnx")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public void NoArrayIsCopiedThroughACastClone()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        var sep = Path.DirectorySeparatorChar;
        var tools = Path.Combine(root, "..", "tools");
        var offenders = new[] { root, tools }
            .Where(Directory.Exists)
            .SelectMany(folder => FileEnumeration.EnumerateFiles(folder, ".cs", recursive: true))
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => CastClone.IsMatch(l.Text))
            .Select(l => $"{Path.GetRelativePath(root, l.File)}:{l.Line}  {l.Text.Trim()}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "an array copied through a cast clone; first ask whether the copy is needed (the array or a ReadOnlySpan for a reader, "
            + "a pooled scratch for a routine that reorders its input), else copy it typed: [.. source], or Copy() for two dimensions");
    }
}
