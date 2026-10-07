using System;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A source guard: a test's temporary folder comes from <see cref="TempFolders"/>, never from a bare
/// <c>Directory.CreateTempSubdirectory</c>, which makes it at the top of the system temp directory where nothing
/// sweeps it (#1197, #1323). A delete that a held file refuses then leaves the folder for ever: the viewer's e2e
/// harness alone left 292 in one day of runs, because a document still held its file when the harness was disposed.
/// Under <see cref="TempFolders.Root"/> the same miss is swept a day later.
/// </summary>
public class TestTempFoldersTests
{
    private const string Definition = "TempFolders.cs";

    /// <summary>
    /// Walks up from the test binary to the repository's <c>src</c>. Null when the sources are not beside the binary
    /// (a packaged run), and the test then skips: not seeing the source is not a regression.
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
    public void NoTestMakesItsFolderAtTheTopOfTheTempDirectory()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        // Built from parts, so this file does not match its own search.
        var bare = "CreateTemp" + "Subdirectory(";
        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateDirectories(root, "*.Tests*")
            .SelectMany(project => FileEnumeration.EnumerateFiles(project, ".cs", recursive: true))
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Where(static f => !Path.GetFileName(f).Equals(Definition, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(bare, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty("a test's temporary folder comes from TempFolders, whose root is swept of what a delete missed");
    }
}
