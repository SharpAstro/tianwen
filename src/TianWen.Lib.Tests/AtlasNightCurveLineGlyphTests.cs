using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The marks of the atlas night line (#2059) are drawn from the bundled text face, DejaVu Sans, and a mark that face lacked would
/// draw NOTHING with no error. The star of the rating was first answered by a recorded capture only; these ask the face itself.
/// </summary>
/// <remarks>
/// What this proves is the BUNDLED face (the file the apps ship), asked directly and through the same fallback chain the window
/// builds (<c>BundledFonts</c>: text face, emoji face, per-OS script faces). It does not draw the info box: the window's own face
/// is whatever <c>BundledFonts.Resolve()</c> finds on the host, and the test host has no <c>Fonts</c> folder by design, so a
/// check of the window would measure the machine. The marks are therefore pinned to the one face every host bundles; the window's
/// own face stays proved only by the recorded capture of the info box (<c>pilot.exe attachments TIA2059</c>).
/// <para>The runes come from the lines <see cref="ObjectInfoPanel.NightLine"/> really produces, not from a literal here, so a mark added
/// to that line is asked of the face too. The face is the copy the tests carry under <c>TestFonts</c> (the file the apps bundle).</para>
/// </remarks>
public class AtlasNightCurveLineGlyphTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static readonly string FacePath = Path.Combine(AppContext.BaseDirectory, "TestFonts", "DejaVuSans.ttf");

    private static List<Rune> NonAsciiRunesOfTheLine()
    {
        var curve = new AtlasNightCurve(
            Samples: [],
            Step: TimeSpan.FromMinutes(15),
            AstroDark: new DateTimeOffset(2025, 12, 4, 18, 30, 0, TimeSpan.Zero),
            AstroTwilight: new DateTimeOffset(2025, 12, 5, 4, 30, 0, TimeSpan.Zero),
            MinAltitude: 20,
            Shaded: true,
            UsableTime: TimeSpan.FromMinutes(255),
            WindowStart: new DateTimeOffset(2025, 12, 4, 20, 30, 0, TimeSpan.Zero),
            WindowEnd: new DateTimeOffset(2025, 12, 5, 0, 45, 0, TimeSpan.Zero),
            Rating: 3.5f);

        var shown = ObjectInfoPanel.NightLine(curve, TimeSpan.Zero);
        shown.ShouldNotBeNull();

        // The no-window wording carries the degree sign, so both lines are asked.
        var unreachable = ObjectInfoPanel.NightLine(curve with { WindowStart = null, WindowEnd = null }, TimeSpan.Zero);
        unreachable.ShouldNotBeNull();

        return [.. (shown + unreachable).EnumerateRunes().Where(r => r.Value > 0x7F).Distinct().OrderBy(r => r.Value)];
    }

    [Fact]
    public void TheBundledFaceCarriesEveryMarkOfTheNightLine()
    {
        File.Exists(FacePath).ShouldBeTrue(FacePath);

        var runes = NonAsciiRunesOfTheLine();
        runes.ShouldContain(new Rune(0x2605), "the rating's star is no longer in the line, so this test asks about nothing");

        var face = new FontFallbackResolver(FacePath, []);
        var missing = runes.Where(r => face.TryResolveFont(r) is null).Select(r => $"U+{r.Value:X4}").ToList();
        missing.ShouldBeEmpty("marks DejaVu Sans lacks, which would draw nothing");

        // The window resolves a rune through a chain built over the bundled face (BundledFonts), so the same marks are asked of
        // that chain with no emoji or script face: no face of this machine may be what happens to draw one.
        var chain = FontFallbackResolver.FromRoles(FacePath, emojiFontPath: null, scriptFontPaths: []);
        var chainMissing = runes.Where(r => chain.TryResolveFont(r) is null).Select(r => $"U+{r.Value:X4}").ToList();
        chainMissing.ShouldBeEmpty("marks the bundled face lacks, which only a face of this machine would draw");
    }

    /// <summary>
    /// The cmap only says a glyph is there; each mark is also drawn through the CPU rasteriser and must leave ink, so an entry with
    /// an empty outline cannot pass.
    /// </summary>
    [Fact]
    public void EveryMarkOfTheNightLineDrawsInkFromTheBundledFace()
    {
        const int Cell = 48;
        var runes = NonAsciiRunesOfTheLine();
        using var renderer = new RgbaImageRenderer((uint)(Cell * runes.Count), Cell);
        renderer.Surface.Clear(new RGBAColor32(0, 0, 0, 0));

        for (var i = 0; i < runes.Count; i++)
        {
            renderer.DrawText(runes[i].ToString().AsSpan(), FacePath, 32f, new RGBAColor32(0xff, 0xff, 0xff, 0xff),
                new RectInt(new PointInt((i + 1) * Cell, Cell), new PointInt(i * Cell, 0)), TextAlign.Center, TextAlign.Center);
        }

        var pixels = renderer.Surface.Pixels;
        var width = (int)renderer.Width;
        List<string> blank = [];
        for (var i = 0; i < runes.Count; i++)
        {
            var inked = 0;
            for (var y = 0; y < Cell; y++)
            {
                for (var x = i * Cell; x < (i + 1) * Cell; x++)
                {
                    inked += pixels[((y * width) + x) * 4 + 3] >= 128 ? 1 : 0;
                }
            }

            // A 32 px star or degree sign inks well over 30 pixels; an empty outline inks none.
            if (inked < 30)
            {
                blank.Add($"U+{runes[i].Value:X4} ({inked} inked)");
            }
        }

        blank.ShouldBeEmpty();
    }

    /// <summary>
    /// The window's own resolve, over a folder that bundles the face as the apps do: it takes the bundled face before anything the
    /// machine ships, and the chain it builds draws every mark of the night line.
    /// </summary>
    [Fact]
    public void TheWindowsResolveOverABundlingHostDrawsEveryMarkOfTheNightLine()
    {
        var host = _folders.Create("bundling-host");
        var fonts = Directory.CreateDirectory(Path.Combine(host.FullName, "Fonts"));
        var bundled = Path.Combine(fonts.FullName, "DejaVuSans.ttf");
        File.Copy(FacePath, bundled);

        var set = BundledFonts.ResolveFrom(host.FullName);

        set.Text.ShouldBe(bundled, "a bundled face is taken before the platform's, whose coverage is not known");
        set.Fallback.ShouldNotBeNull();
        set.Fallback.PrimaryFontPath.ShouldBe(bundled);

        var runes = NonAsciiRunesOfTheLine();
        var missing = runes.Where(r => set.Fallback.TryResolveFont(r) is null).Select(r => $"U+{r.Value:X4}").ToList();
        missing.ShouldBeEmpty("marks the window's chain cannot draw, which would show nothing");
    }
}
