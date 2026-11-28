using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Holds the reader half of #2554: a reader of the shown image's steps takes the view and the selection once, through
/// <c>TrySelected</c>, and indexes the array it checked; indexing by a fresh read of the selection is what went out of range.
/// </summary>
public sealed class WorkbenchSelectionSourceTests
{
    [Fact]
    [Trait("Category", "Invariant")]
    public void NoWorkbenchActionIndexesByAFreshReadOfTheSelection()
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } root)
        {
            return;
        }

        var files = Directory.GetFiles(Path.Combine(root, "TianWen.UI.Abstractions"), "WorkbenchActions*.cs");
        files.ShouldNotBeEmpty();
        var offenders = files
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line.Trim())))
            .Where(l => !l.Text.StartsWith("//") && l.Text.Contains("[state.SelectedStep]"))
            .Select(l => $"{l.File}:{l.Line}: {l.Text}")
            .ToArray();

        offenders.ShouldBeEmpty("index the array TrySelected checked, never state.SelectedStep read again");
    }
}
