using System;
using System.IO;
using System.Linq;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using TianWen.Cli.Tui;
using TianWen.Lib.IO;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The TUI states what a colour means in ONE place, <see cref="TuiRowPalette"/> (#2126), as <see cref="GuiTheme"/> does for
/// the GUI. Before it, each row named a 16-colour <see cref="SgrColor"/> at its call site: green for an action in one row
/// and for a state in the next, and the brand's orange nowhere.
/// </summary>
public class TuiPaletteSourceTests
{
    // Built from parts, so this file does not match its own search.
    private static readonly string RawColour = "Sgr" + "Color.";

    /// <summary>Walks up from the test binary to the repository's <c>src</c>; null when the sources are not beside it.</summary>
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
    public void NoTuiRowNamesATerminalColourItself()
    {
        if (FindSourceRoot() is not { } root)
        {
            return;
        }

        var cli = Path.Combine(root, "TianWen.Cli");
        var offenders = new[] { "Tui", "Plan", "View" }
            .Select(folder => Path.Combine(cli, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => FileEnumeration.EnumerateFiles(folder, ".cs", recursive: true))
            .Where(file => !string.Equals(Path.GetFileName(file), "RowPen.cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (line, index))
                .Where(l => l.line.Contains(RawColour, StringComparison.Ordinal))
                .Select(l => $"{Path.GetRelativePath(root, file)}:{l.index + 1}: {l.line.Trim()}"))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        offenders.ShouldBeEmpty("a TUI row asks TuiRowPalette for a role; only the palette names a terminal colour");
    }

    [Fact]
    public void TheAccentIsTheGuisAndAnActionIsDrawnInIt()
    {
        TuiRowPalette.AccentColour.ShouldBe(GuiTheme.DarkPalette.Accent);
        TuiRowPalette.Accent.Foreground.ShouldBe(TuiRowPalette.AccentColour);
        TuiRowPalette.ActionFill.Background.ShouldBe(TuiRowPalette.AccentColour);

        // An action is never the green of a state.
        TuiRowPalette.Accent.Foreground.ShouldNotBe(TuiRowPalette.Good.Foreground);
        TuiRowPalette.ActionFill.Background.ShouldNotBe(TuiRowPalette.GoodFill.Background);
    }

    // A terminal with sixteen colours gets the nearest of them (VtStyle), so a pen that reads in 24-bit colour must still
    // read once both its halves are rounded: neither may land on the other, and the accent must not land on the warning's
    // or the error's colour, which would make an action read as one.
    [Fact]
    public void EveryPenStillReadsInSixteenColours()
    {
        (string Name, RowPen Pen)[] pens =
        [
            (nameof(TuiRowPalette.Selected), TuiRowPalette.Selected),
            (nameof(TuiRowPalette.SelectedQuiet), TuiRowPalette.SelectedQuiet),
            (nameof(TuiRowPalette.Body), TuiRowPalette.Body),
            (nameof(TuiRowPalette.Dim), TuiRowPalette.Dim),
            (nameof(TuiRowPalette.SectionHeader), TuiRowPalette.SectionHeader),
            (nameof(TuiRowPalette.Button), TuiRowPalette.Button),
            (nameof(TuiRowPalette.Accent), TuiRowPalette.Accent),
            (nameof(TuiRowPalette.ActionFill), TuiRowPalette.ActionFill),
            (nameof(TuiRowPalette.DangerFill), TuiRowPalette.DangerFill),
            (nameof(TuiRowPalette.Good), TuiRowPalette.Good),
            (nameof(TuiRowPalette.GoodFill), TuiRowPalette.GoodFill),
            (nameof(TuiRowPalette.GoodFillQuiet), TuiRowPalette.GoodFillQuiet),
            (nameof(TuiRowPalette.Info), TuiRowPalette.Info),
            (nameof(TuiRowPalette.InfoFill), TuiRowPalette.InfoFill),
            (nameof(TuiRowPalette.Warn), TuiRowPalette.Warn),
            (nameof(TuiRowPalette.WarnFill), TuiRowPalette.WarnFill),
            (nameof(TuiRowPalette.Error), TuiRowPalette.Error),
            (nameof(TuiRowPalette.ErrorFill), TuiRowPalette.ErrorFill),
        ];

        foreach (var (name, pen) in pens)
        {
            SgrColorExtensions.NearestSgrColor(pen.Foreground)
                .ShouldNotBe(SgrColorExtensions.NearestSgrColor(pen.Background), $"{name} is one colour on itself in a 16-colour terminal");
        }

        var accent = SgrColorExtensions.NearestSgrColor(TuiRowPalette.AccentColour);
        accent.ShouldBe(SgrColor.Yellow, "the dark yellow is the 16-colour orange");
        accent.ShouldNotBe(SgrColorExtensions.NearestSgrColor(TuiRowPalette.Warn.Foreground));
        accent.ShouldNotBe(SgrColorExtensions.NearestSgrColor(TuiRowPalette.Error.Foreground));
        accent.ShouldNotBe(SgrColorExtensions.NearestSgrColor(TuiRowPalette.Good.Foreground));
    }

    // The filled action is the accent under the ink MEASURED for it, as the GUI's go button is.
    [Fact]
    public void TheFilledActionCarriesItsLabel()
        => Contrast(TuiRowPalette.ActionFill.Foreground, TuiRowPalette.ActionFill.Background).ShouldBeGreaterThanOrEqualTo(4.5);

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        static double Lin(byte v)
        {
            var c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Lum(RGBAColor32 c) => (0.2126 * Lin(c.Red)) + (0.7152 * Lin(c.Green)) + (0.0722 * Lin(c.Blue));
        var (hi, lo) = Lum(a) >= Lum(b) ? (Lum(a), Lum(b)) : (Lum(b), Lum(a));
        return (hi + 0.05) / (lo + 0.05);
    }
}
