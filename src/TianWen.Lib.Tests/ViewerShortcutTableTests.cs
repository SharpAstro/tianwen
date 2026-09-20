using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="ViewerShortcuts"/> is the viewer's ONE keyboard declaration, and these pin the
/// properties that make it worth having.
/// </summary>
/// <remarks>
/// The table replaced three hand-maintained copies of the same fact -- the <c>case InputKey.X</c>
/// arms, the toolbar button table, and a prose array the <c>?</c> panel printed. They had already
/// drifted: a panel titled "All keyboard shortcuts" was missing fifteen bindings. The generated page
/// makes that particular drift impossible, so what is left to pin is that the table itself stays
/// coherent -- no chord bound twice, no row whose chord runs into its own description, and the
/// bindings that were missing actually present.
/// </remarks>
public class ViewerShortcutTableTests
{
    [Fact]
    public void NoChordIsBoundTwice()
    {
        // Only the rows that name a resolvable key can collide; a prose row ("Wheel / Ctrl+Wheel")
        // documents a gesture and binds nothing.
        var bound = ViewerShortcuts.All
            .Where(s => s.Key is not null)
            .Select(s => (s.Key!.Value, s.Modifiers))
            .ToList();

        bound.Distinct().Count().ShouldBe(bound.Count,
            "a chord bound twice means one of the two never runs, and which one is an ordering accident");
    }

    // The constant is asserted rather than trusted: a longer chord added later would otherwise run
    // into its own description with nothing to catch it but a screenshot.
    [Fact]
    public void EveryChordFitsItsColumn()
    {
        foreach (var s in ViewerShortcuts.All)
        {
            s.Chord.Length.ShouldBeLessThan(ViewerShortcuts.ChordColumn,
                $"'{s.Chord}' does not fit the chord column, so its description starts inside it");
        }
    }

    [Fact]
    public void EveryRowIsPrintedOnThePage()
    {
        var lines = ViewerShortcuts.HelpLines();

        lines.Length.ShouldBe(ViewerShortcuts.All.Length);
        foreach (var s in ViewerShortcuts.All)
        {
            lines.ShouldContain(l => l.StartsWith(s.Chord) && l.EndsWith(s.Description));
        }
    }

    // The fifteen the prose array had lost. Named individually rather than counted, because a count
    // passes again the moment somebody deletes a row and adds another.
    [Theory]
    [InlineData("T")]
    [InlineData("S")]
    [InlineData("C")]
    [InlineData("D")]
    [InlineData("A / Shift+A")]
    [InlineData("P")]
    [InlineData("E")]
    [InlineData("F")]
    [InlineData("R")]
    [InlineData("F1")]
    [InlineData("+ / -")]
    [InlineData("Ctrl+0")]
    [InlineData("Ctrl+1")]
    [InlineData("Space / Tab")]
    public void TheBindingsTheProseArrayHadLostAreDocumented(string chord)
        => ViewerShortcuts.All.ShouldContain(s => s.Chord == chord);

    // A row that names a toolbar button is the half that makes a key and a button ONE declaration.
    // Pinned so the link cannot be quietly dropped when a row is edited.
    [Fact]
    public void TheButtonBackedRowsNameTheirButton()
    {
        IReadOnlyDictionary<string, ToolbarAction> expected = new Dictionary<string, ToolbarAction>
        {
            ["T"] = ToolbarAction.StretchToggle,
            ["Z"] = ToolbarAction.Zoom,
            ["W"] = ToolbarAction.WhiteBalance,
            ["N"] = ToolbarAction.BackgroundNeutralize,
            ["D"] = ToolbarAction.Debayer,
            ["F1"] = ToolbarAction.Shortcuts,
            ["Ctrl+Shift+S"] = ToolbarAction.Save,
        };

        foreach (var (chord, action) in expected)
        {
            var row = ViewerShortcuts.All.Single(s => s.Chord == chord);
            row.Button.ShouldBe(action, $"'{chord}' presses the {action} button");
        }
    }
}
