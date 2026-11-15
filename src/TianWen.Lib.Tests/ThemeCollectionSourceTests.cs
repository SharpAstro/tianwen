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
/// <para>A file is cut into segments at each <c>[Collection(...)]</c> attribute; a segment that calls one of the three must carry
/// <c>[Collection(ThemeCollection.Name)]</c>, and so must code before the first attribute (a class with no collection).</para>
/// </summary>
[Trait("Category", "Invariant")]
public class ThemeCollectionSourceTests
{
    private static readonly Regex Call = new(@"\bGuiTheme\s*\.\s*(Apply|ToggleNight|CycleTheme)\s*\(", RegexOptions.Compiled);
    private static readonly Regex CollectionAttr = new(@"^[ \t]*\[Collection\(([^)]*)\)\]", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex LineComment = new(@"//[^\r\n]*", RegexOptions.Compiled);

    private static string? FindTestProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TianWen.Lib.Tests");
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
        if (FindTestProject() is not { } root)
        {
            return;
        }

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

            var starts = CollectionAttr.Matches(text).Select(a => a.Index).Prepend(0).Distinct().ToArray();
            for (var i = 0; i < starts.Length; i++)
            {
                var end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
                var segment = text[starts[i]..end];
                if (Call.Match(segment) is not { Success: true } call)
                {
                    continue;
                }

                var attr = CollectionAttr.Match(segment);
                if (!attr.Success || !attr.Groups[1].Value.Contains("ThemeCollection.Name", StringComparison.Ordinal))
                {
                    var line = text[..(starts[i] + call.Index)].Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line}");
                }
            }
        }

        offenders.ShouldBeEmpty("a class that calls GuiTheme.Apply, ToggleNight or CycleTheme must be [Collection(ThemeCollection.Name)] (tianwen #2169)");
    }
}
