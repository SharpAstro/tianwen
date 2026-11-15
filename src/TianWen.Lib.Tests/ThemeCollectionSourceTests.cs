using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A source guard (#2169): a test class that calls <c>GuiTheme.Apply</c>, <c>ToggleNight</c> or <c>CycleTheme</c> is in
/// <see cref="ThemeCollection"/>. The theme is process-wide and every render test reads it, so a class that flips it in any other
/// collection races the render tests of the collections that run beside it.
/// <para>A file is cut into one segment per top-level type (its attributes and body); a segment that calls one of the three must
/// carry <c>[Collection(ThemeCollection.Name)]</c> in its own attributes, so an unattributed class after a themed one in the same
/// file is judged on its own. The collection must also stay <c>DisableParallelization = true</c>: the race is timing-dependent, so
/// no test can show the fix directly, and this guard plus that flag are what hold it.</para>
/// </summary>
[Trait("Category", "Invariant")]
public class ThemeCollectionSourceTests
{
    private static readonly Regex Call = new(@"\bGuiTheme\s*\.\s*(Apply|ToggleNight|CycleTheme)\s*\(", RegexOptions.Compiled);
    private static readonly Regex CollectionAttr = new(@"^[ \t]*\[Collection\(([^)]*)\)\]", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex LineComment = new(@"//[^\r\n]*", RegexOptions.Compiled);
    // A top-level type: declared at column 0 (file-scoped namespace), so a nested type never cuts a segment.
    private static readonly Regex TypeDecl = new(@"^(?:(?:public|internal|sealed|static|abstract|partial|file|readonly)\s+)*(?:class|record|struct)\s", RegexOptions.Compiled);

    /// <summary>One segment per top-level type, from its first attribute line to the next type's first attribute line.</summary>
    private static List<(int Start, string Text)> Segments(string text)
    {
        var lines = text.Split('\n');
        var offsets = new int[lines.Length];
        for (var i = 1; i < lines.Length; i++)
        {
            offsets[i] = offsets[i - 1] + lines[i - 1].Length + 1;
        }

        var starts = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TypeDecl.IsMatch(lines[i]))
            {
                continue;
            }

            var first = i;
            // Walk back over attribute lines and the blanked doc-comment lines between them.
            while (first > 0 && (lines[first - 1].StartsWith('[') || lines[first - 1].Trim().Length == 0))
            {
                first--;
            }

            starts.Add(offsets[first]);
        }

        var result = new List<(int, string)>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : text.Length;
            result.Add((starts[i], text[starts[i]..end]));
        }

        return result;
    }

    [Fact]
    public void ThemeCollectionStaysSerial()
    {
        var root = FindTestProject().ShouldNotBeNull("the test project's source was not found above the test binary");
        File.ReadAllText(Path.Combine(root, "ThemeCollection.cs")).ShouldContain("DisableParallelization = true");
    }

    private static string? FindTestProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TianWen.Lib" + ".Tests");
            if (File.Exists(Path.Combine(candidate, "TianWen.Lib.Tests.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public void EveryClassThatChangesTheThemeIsInTheThemeCollection()
    {
        var root = FindTestProject().ShouldNotBeNull("the test project's source was not found above the test binary: the guard would pass over nothing");

        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}")
                && Path.GetFileName(f) != nameof(ThemeCollectionSourceTests) + ".cs")
            .ToArray();
        files.Length.ShouldBeGreaterThan(100, "the scan found next to no source: the root is wrong");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            // Comments blanked so a remark that NAMES a call is not a call.
            var text = LineComment.Replace(File.ReadAllText(file), m => new string(' ', m.Length));
            if (!Call.IsMatch(text))
            {
                continue;
            }

            foreach (var (start, segment) in Segments(text))
            {
                if (Call.Match(segment) is not { Success: true } call)
                {
                    continue;
                }

                var attr = CollectionAttr.Match(segment);
                if (!attr.Success || !attr.Groups[1].Value.Contains("ThemeCollection.Name", StringComparison.Ordinal))
                {
                    var line = text[..(start + call.Index)].Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line}");
                }
            }
        }

        offenders.ShouldBeEmpty("a class that calls GuiTheme.Apply, ToggleNight or CycleTheme must be [Collection(ThemeCollection.Name)] (tianwen #2169)");
    }
}
