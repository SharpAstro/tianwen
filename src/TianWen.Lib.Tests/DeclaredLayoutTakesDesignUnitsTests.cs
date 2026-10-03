using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The unit rule of viewer-layout-engine.md: a declared tree is authored in DESIGN units, and the measure context scales it by
/// <c>DpiScale</c>. A widget's own <c>X => BaseX * DpiScale</c> property is already device pixels, so handed to a node it is scaled
/// twice. That has shipped twice: the tone popover at 3.85x its 1x width on a 2x display, and the histogram's LOG label drawn too
/// large for its box, reading "L..." at 2x (2026-10-02). A viewer test at 1x cannot see it, since squaring the scale is the identity
/// there, so this reads the SOURCE.
/// </summary>
/// <remarks>
/// A statement that builds a layout node (a <c>Layout.Builder</c> call, or a sizing method on a node) must not name a property of its
/// own class whose body multiplies by <c>DpiScale</c>, unless the statement itself arranges at <c>DesignScale.One</c> (saying the
/// tree is in device pixels), or the file arranges that way throughout and is listed below with its reason. It matches by pattern,
/// so a device-pixel value carried through a local gets past it; the <c>chrome-review</c> skill reads a diff for what a pattern
/// cannot.
/// </remarks>
public class DeclaredLayoutTakesDesignUnitsTests
{
    /// <summary>Files whose declared trees are all arranged at <c>DesignScale.One</c>, so a device-pixel size is right there.</summary>
    private static readonly Dictionary<string, string> ArrangedInDevicePixels = new(StringComparer.Ordinal)
    {
        ["ImageRendererBase.Toolbar.cs"] =
            "the toolbar run is arranged at DesignScale.One, because every measurement in the file is already device pixels",
    };

    private static readonly Regex ClassName = new(@"\bclass\s+(\w+)", RegexOptions.Compiled);

    private static readonly Regex DevicePixelProperty = new(
        @"\b(?:private|protected|internal|public)\s+(?:static\s+)?float\s+(\w+)\s*=>[^;]*\bDpiScale\b[^;]*;", RegexOptions.Compiled);

    private static readonly Regex BuildsANode = new(
        @"Layout\.Builder\.|\.(?:Pad|PadX|RowH|WFixed|HFixed|WithGap|WithLineGap)\(", RegexOptions.Compiled);

    // Literals and comments are blanked to the same length, newlines kept, so a match's offset still gives its line.
    private static readonly Regex Literal = new(
        @"'(?:[^'\\\n]|\\.)'|@?\$?""(?:[^""\\\n]|\\.)*""|//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void NoDeclaredNodeIsGivenADevicePixelSize()
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } root)
        {
            return;
        }

        var sources = ChromeMeasuresThroughTheEngineTests.ChromeSourceFiles(root)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToArray();

        Offences(sources).ShouldBeEmpty(
            "a layout node is given a size that is already device pixels, which the engine scales by DpiScale AGAIN. "
            + "Pass the Base* design-unit constant, or arrange the tree at DesignScale.One if all of it is device pixels "
            + "(docs/plans/viewer-layout-engine.md, \"The unit rule\").");
    }

    /// <summary>
    /// A widget's sizes are at ITS scale, never the window's. Widgets in one window do not share a scale: the GUI's chrome and tabs
    /// carry <c>GuiTheme.InterfaceScale</c> over the window's DPI and the image viewers they embed do not (2026-10-03). So a widget
    /// reading the window's DPI straight (<c>Ui.DpiScale</c>) works out its sizes at a scale its declared nodes are not laid out
    /// at; its own <c>DpiScale</c> and <c>Scale</c> include its scale. The <c>chrome-review</c> agent reads a diff for the other
    /// half, one widget's scale carried into another's layout.
    /// </summary>
    [Fact]
    public void NoWidgetReadsTheWindowsDpiStraight()
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } root)
        {
            return;
        }

        var offences = ChromeMeasuresThroughTheEngineTests.ChromeSourceFiles(root)
            .Where(f => Literal.Replace(File.ReadAllText(f), m => new string(' ', m.Length)).Contains("Ui.DpiScale", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        offences.ShouldBeEmpty("a widget reads the WINDOW's DPI, which leaves out its own InterfaceScale: read DpiScale or Scale on the widget");
    }

    // A listed file has to keep earning its place, or the exemption outlives the reason and hides the next mistake there.
    [Fact]
    public void EveryFileListedAsDevicePixelsStillArrangesThatWay()
    {
        if (ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is not { } root)
        {
            return;
        }

        var files = ChromeMeasuresThroughTheEngineTests.ChromeSourceFiles(root).ToArray();
        foreach (var (name, reason) in ArrangedInDevicePixels)
        {
            var file = files.SingleOrDefault(f => Path.GetFileName(f) == name)
                .ShouldNotBeNull($"{name} is listed ({reason}) but no longer exists");
            File.ReadAllText(file).ShouldContain("DesignScale.One", Case.Sensitive,
                $"{name} is listed ({reason}) but no longer arranges at DesignScale.One");
        }
    }

    // The check must catch the line that shipped, and leave alone the two shapes that are right.
    [Fact]
    public void TheCheckCatchesTheLabelThatShippedAndNotTheShapesThatAreRight()
    {
        const string Header = """
            partial class ImageRendererBase<TSurface>
            {
                private float ToolbarFontSize => BaseToolbarFontSize * DpiScale;

            """;

        Offences([("Shipped.cs", Header + """
                private Layout.Node HistogramLogLabel()
                    => Layout.Builder.Text("LOG", ToolbarFontSize, ViewerTheme.Palette.BodyText, hAlign: TextAlign.Center);
            }
            """)]).ShouldHaveSingleItem().ShouldBe("Shipped.cs:4 ToolbarFontSize");

        Offences([("Right.cs", Header + """
                private Layout.Node Label() => Layout.Builder.Text("LOG", BaseToolbarFontSize, colour); // ToolbarFontSize would be wrong
                private void Menu() => RenderLayout(Layout.Builder.Text("x", ToolbarFontSize, colour), viewport, scale: DesignScale.One);
            }
            """)]).ShouldBeEmpty();
    }

    /// <summary>Each statement building a node that names a device-pixel property of its own class, as <c>file:line names</c>.</summary>
    private static List<string> Offences(IEnumerable<(string Name, string Text)> sources)
    {
        var stripped = sources
            .Where(s => !ArrangedInDevicePixels.ContainsKey(s.Name))
            .Select(s => (s.Name, Text: Literal.Replace(s.Text, m => Regex.Replace(m.Value, @"[^\n]", " "))))
            .Select(s => (s.Name, s.Text, Class: ClassName.Match(s.Text) is { Success: true } m ? m.Groups[1].Value : null))
            .ToArray();

        // A partial class spreads its properties over several files, so they are gathered per class before any file is checked.
        var devicePixels = stripped
            .SelectMany(s => s.Class is { } owner
                ? DevicePixelProperty.Matches(s.Text).Select(m => (Class: owner, Property: m.Groups[1].Value))
                : [])
            .GroupBy(p => p.Class, p => p.Property)
            .ToDictionary(g => g.Key, g => g.Distinct().ToArray());

        var offences = new List<string>();
        foreach (var (name, text, @class) in stripped)
        {
            if (@class is null || !devicePixels.TryGetValue(@class, out var properties))
            {
                continue;
            }

            var seen = new HashSet<int>();
            foreach (Match node in BuildsANode.Matches(text))
            {
                var start = new[] { ';', '{', '}' }.Max(c => text.LastIndexOf(c, node.Index)) + 1;
                var end = text.IndexOf(';', node.Index);
                var statement = text[start..(end < 0 ? text.Length : end)];
                if (!seen.Add(start) || statement.Contains("DesignScale.One", StringComparison.Ordinal))
                {
                    continue;
                }

                // A call of the same name (the altitude chart's FontSize(h, 10)) is a method, not the property.
                var used = properties.Where(p => Regex.IsMatch(statement, $@"(?<![\w.]){p}\b(?!\s*\()")).ToArray();
                if (used.Length > 0)
                {
                    var line = text.AsSpan(0, start + statement.Length - statement.TrimStart().Length).Count('\n') + 1;
                    offences.Add($"{name}:{line} {string.Join(", ", used)}");
                }
            }
        }

        return offences;
    }
}
