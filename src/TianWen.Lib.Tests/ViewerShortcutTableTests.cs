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

    // The resolver matches on `is { Button: { } }`, so a row that declares a ROUTE but names no
    // button is skipped in silence -- the key would simply stop working, with nothing to say why.
    [Fact]
    public void EveryRoutedRowNamesTheButtonItRoutesTo()
    {
        foreach (var s in ViewerShortcuts.All)
        {
            if (s.Route is not ViewerShortcutRoute.None)
            {
                s.Button.ShouldNotBeNull($"'{s.Chord}' declares a route but no button, so it resolves to nothing");
                s.Key.ShouldNotBeNull($"'{s.Chord}' declares a route but no key, so nothing can resolve it");
            }
        }
    }

    // Modifiers are part of the chord, not decoration: Ctrl+Shift+S opens the Save menu while
    // Ctrl+S writes the file, and resolving one as the other would silently swap them.
    [Fact]
    public void ResolutionIsExactOnModifiers()
    {
        ViewerShortcuts.TryResolveRoute(InputKey.S, InputModifier.Ctrl | InputModifier.Shift)
            .ShouldNotBeNull().Button.ShouldBe(ToolbarAction.Save);

        ViewerShortcuts.TryResolveRoute(InputKey.S, InputModifier.Ctrl).ShouldBeNull();
        ViewerShortcuts.TryResolveRoute(InputKey.S, InputModifier.None).ShouldBeNull();
    }

    [Theory]
    [InlineData(InputKey.T, ToolbarAction.StretchToggle, ViewerShortcutRoute.Press)]
    [InlineData(InputKey.Z, ToolbarAction.Zoom, ViewerShortcutRoute.OpenMenu)]
    [InlineData(InputKey.W, ToolbarAction.WhiteBalance, ViewerShortcutRoute.OpenMenu)]
    [InlineData(InputKey.F1, ToolbarAction.Shortcuts, ViewerShortcutRoute.OpenMenu)]
    public void TheRoutedKeysResolveToTheirButtonAndRoute(
        InputKey key, ToolbarAction button, ViewerShortcutRoute route)
    {
        var resolved = ViewerShortcuts.TryResolveRoute(key, InputModifier.None).ShouldNotBeNull();
        resolved.Button.ShouldBe(button);
        resolved.Route.ShouldBe(route);
    }

    // A key with no declared route must NOT resolve, or it would be taken from the switch arm that
    // still implements it.
    [Theory]
    [InlineData(InputKey.G)]
    // D and N name a button but keep their arms, because that button opens a dropdown.
    [InlineData(InputKey.D)]
    [InlineData(InputKey.N)]
    [InlineData(InputKey.P)]
    [InlineData(InputKey.E)]
    [InlineData(InputKey.F)]
    [InlineData(InputKey.R)]
    [InlineData(InputKey.Escape)]
    public void AnUnroutedKeyIsLeftToItsArm(InputKey key)
        => ViewerShortcuts.TryResolveRoute(key, InputModifier.None).ShouldBeNull();

    // The trap that shipped for one commit: Debayer and BackgroundNeutralize have dropdowns, the
    // host's press policy opens a dropdown on a LEFT press, so routing their keys as a Press opened
    // a menu where the key had always acted. The embedded policy the other tests use does not, which
    // is exactly why nothing caught it. This makes the combination undeclarable.
    [Fact]
    public void NoKeyIsRoutedAsAPressToAButtonThatOpensAMenu()
    {
        foreach (var s in ViewerShortcuts.All)
        {
            if (s.Route is ViewerShortcutRoute.Press && s.Button is { } button)
            {
                ViewerShortcuts.ActionsWithADropdown.ShouldNotContain(button,
                    $"'{s.Chord}' would open {button}'s menu instead of doing its action");
            }
        }
    }

    // And the other half: a row that opens a menu must name a button that HAS one.
    [Fact]
    public void EveryOpenMenuRowNamesAButtonThatHasAMenu()
    {
        foreach (var s in ViewerShortcuts.All)
        {
            if (s.Route is ViewerShortcutRoute.OpenMenu && s.Button is { } button)
            {
                ViewerShortcuts.ActionsWithADropdown.ShouldContain(button,
                    $"'{s.Chord}' opens a menu that {button} does not have");
            }
        }
    }
}
