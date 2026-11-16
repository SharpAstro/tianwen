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
    // A type declaration at any indent (a block-scoped namespace indents every type); the shallowest indent in the file is top level,
    // so a nested type never cuts a segment.
    private static readonly Regex TypeDecl = new(@"^\s*(?:(?:public|internal|sealed|static|abstract|partial|file|readonly)\s+)*(?:class|record|struct)\s", RegexOptions.Compiled);

    /// <summary>One segment per top-level type, from its first attribute line to the next type's first attribute line.</summary>
    private static List<(int Start, string Text)> Segments(string text)
    {
        var lines = text.Split('\n');
        var offsets = new int[lines.Length];
        for (var i = 1; i < lines.Length; i++)
        {
            offsets[i] = offsets[i - 1] + lines[i - 1].Length + 1;
        }

        var decls = new List<(int Line, int Indent)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (TypeDecl.IsMatch(lines[i]))
            {
                decls.Add((i, lines[i].Length - lines[i].TrimStart().Length));
            }
        }

        var starts = new List<int>();
        if (decls.Count > 0)
        {
            var top = decls.Min(d => d.Indent);
            foreach (var (line, indent) in decls)
            {
                if (indent != top)
                {
                    continue;
                }

                var first = line;
                // Walk back over attribute lines and the blanked doc-comment lines between them.
                while (first > 0 && (lines[first - 1].TrimStart().StartsWith('[') || lines[first - 1].Trim().Length == 0))
                {
                    first--;
                }

                starts.Add(offsets[first]);
            }
        }

        var result = new List<(int, string)>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : text.Length;
            result.Add((starts[i], text[starts[i]..end]));
        }

        return result;
    }

    /// <summary>
    /// The 1-based line of each theme-changing call in <paramref name="source"/> that is not in a segment carrying
    /// <c>[Collection(ThemeCollection.Name)]</c>. A source with a call but no segment is an offender too, so a regex that stops
    /// matching fails loudly instead of passing over the file.
    /// </summary>
    private static List<int> Offenders(string source)
    {
        // Comments blanked so a remark that NAMES a call is not a call.
        var text = LineComment.Replace(source, m => new string(' ', m.Length));
        var result = new List<int>();
        if (!Call.IsMatch(text))
        {
            return result;
        }

        var segments = Segments(text);
        if (segments.Count == 0)
        {
            result.Add(text[..Call.Match(text).Index].Count(c => c == '\n') + 1);
            return result;
        }

        foreach (var (start, segment) in segments)
        {
            if (Call.Match(segment) is not { Success: true } call)
            {
                continue;
            }

            var attr = CollectionAttr.Match(segment);
            if (!attr.Success || !attr.Groups[1].Value.Contains("ThemeCollection.Name", StringComparison.Ordinal))
            {
                result.Add(text[..(start + call.Index)].Count(c => c == '\n') + 1);
            }
        }

        return result;
    }

    [Theory]
    [InlineData("namespace A\n{\n    [Collection(\"UI\")]\n    public class T\n    {\n        void M() => GuiTheme.Apply(x);\n    }\n}\n", 1)]
    [InlineData("namespace A\n{\n    [Collection(ThemeCollection.Name)]\n    public class T\n    {\n        void M() => GuiTheme.Apply(x);\n    }\n}\n", 0)]
    [InlineData("namespace A;\n\n[Collection(ThemeCollection.Name)]\npublic class T\n{\n    void M() => GuiTheme.ToggleNight();\n}\n\n[Collection(\"UI\")]\npublic class U\n{\n    void M() => GuiTheme.CycleTheme();\n}\n", 1)]
    [InlineData("namespace A;\n\n[Collection(ThemeCollection.Name)]\npublic class T\n{\n    class Nested { void M() => GuiTheme.Apply(x); }\n}\n", 0)]
    [InlineData("namespace A;\n\n[Collection(\"UI\")]\npublic class T\n{\n    // GuiTheme.Apply(x) is only named here\n}\n", 0)]
    [InlineData("GuiTheme.Apply(x);\n", 1)]
    public void TheScanJudgesItsOwnCases(string source, int expectedOffenders) => Offenders(source).Count.ShouldBe(expectedOffenders);

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
            // The name is split: TestTempFoldersTests flags the quoted project name, so do not fold it back.
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
            foreach (var line in Offenders(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line}");
            }
        }

        offenders.ShouldBeEmpty("a class that calls GuiTheme.Apply, ToggleNight or CycleTheme must be [Collection(ThemeCollection.Name)] (tianwen #2169)");
    }
}
