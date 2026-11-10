using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
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

    /// <summary>Every <c>.cs</c> source of every test project under <paramref name="root"/>, build output left out.</summary>
    private static IEnumerable<string> TestSources(string root)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateDirectories(root, "*.Tests*")
            .SelectMany(project => FileEnumeration.EnumerateFiles(project, ".cs", recursive: true))
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal));
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
        var offenders = TestSources(root)
            .Where(static f => !Path.GetFileName(f).Equals(Definition, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(bare, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty("a test's temporary folder comes from TempFolders, whose root is swept of what a delete missed");
    }

    /// <summary>
    /// The dated output folders and the <see cref="TempFolders"/> root are the ONLY places a test writes under the tests' own
    /// temp folder, because only they are removed when the test ends (#2035; <see cref="TestOutput"/>). A test that
    /// builds a path beside them by hand leaves what it wrote for the sweep a day later, and the CI step that lists what
    /// a green run left behind fails on it.
    /// </summary>
    [Fact]
    public void NoTestBuildsAFolderUnderTheTestsTempFolderByHand()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        // Built from parts, so this file does not match its own search.
        var folderName = "\"TianWen.Lib" + ".Tests\"";
        var offenders = TestSources(root)
            .Where(static f => !Path.GetFileName(f).Equals(Definition, StringComparison.Ordinal)
                && !Path.GetFileName(f).Equals("TestOutput.cs", StringComparison.Ordinal)
                && !Path.GetFileName(f).Equals("TestRunTemp.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(folderName, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty("a test's output folder comes from SharedTestData.CreateTempTestOutputDir, which removes it when the test ends");
    }

    /// <summary>
    /// The date in a dated output folder's name is <see cref="TestOutput"/>'s to format (#2035). A test that formats its own
    /// <c>yyyyMMdd</c> into a path, or starts from the root <see cref="TestOutput"/> keeps in its <c>DefaultRoot</c>, builds a dated folder
    /// whose end nothing owns, and the guard above never sees it when the tests' folder name reaches it through a constant or a
    /// variable rather than a literal.
    /// </summary>
    [Fact]
    public void NoTestFormatsADateIntoItsOutputPathByHand()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        // Built from parts, so this file does not match its own search.
        var dateFormat = "\"yyyy" + "MMdd\"";
        var defaultRoot = "TestOutput." + "DefaultRoot";
        string[] owners = ["TestOutput.cs", "SharedTestData.cs"];
        var offenders = TestSources(root)
            .Where(f => !owners.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains(dateFormat, StringComparison.Ordinal) || text.Contains(defaultRoot, StringComparison.Ordinal);
            })
            .Select(f => Path.GetRelativePath(root, f))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty("a dated output folder comes from SharedTestData.CreateTempTestOutputDir, which names it by date and removes it when the test ends");
    }

    // #1626: a planetary capture's path is read for its telescope folder by folder outward, so a random folder name holding a digit beside a
    // letter ("4in" read as a 102 mm telescope) failed ViewerAutoViewTests on a CI leg. A folder TempFolders makes names none.
    [Fact]
    public void AFoldersRandomNameNamesNoTelescope()
    {
        using var temp = new TempFolders();
        for (var i = 0; i < 500; i++)
        {
            var folder = temp.Create("t");
            folder.Name[1..].ShouldAllBe(c => char.IsAsciiLetterLower(c), "letters only, so no digit stands beside a unit's word");
            PlanetaryCaptureName.TelescopeOfPath(Path.Combine(folder.FullName, "capture.ser")).ApertureMm.ShouldBeNull(folder.FullName);
        }
    }
}
