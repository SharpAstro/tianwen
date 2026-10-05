using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry.Lunar;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The browser build's emoji face is a SUBSET of the Noto face the desktop bundles, and must carry every glyph
/// the web draws from it.
/// </summary>
/// <remarks>
/// A glyph the subset lacks draws NOTHING and reserves nothing, so no other test and no error says so. It once
/// carried only the atlas's camera: every night hour of the planner's weather band drew no moon, and since the
/// two web tabs share one window's emoji face, a visit to the Sky Atlas then took the sun and the clouds away
/// too (reported 2026-10-05). Every glyph a web-drawn surface takes from the emoji face is listed by its owner,
/// and these ask the subset for each.
/// </remarks>
public class WebEmojiSubsetTests
{
    private static readonly string SubsetPath = Path.Combine(AppContext.BaseDirectory, "TestFonts", "NotoEmoji-Web.ttf");

    /// <summary>
    /// What the web draws from its emoji face: the weather band, the Moon's phase at the top of its curve,
    /// and the atlas's photo mark, one rune each, in code point order.
    /// </summary>
    private static List<Rune> RunesTheWebDrawsFromItsEmojiFace()
    {
        List<string> glyphs = [.. AltitudeChartRenderer.WeatherGlyphs, SkyMapTab<RgbaImage>.PictureMark];

        // Every phase GetPhaseEmoji can answer: each illumination band, waxing and waning, both hemispheres.
        foreach (var illumination in new[] { 0.0, 0.2, 0.5, 0.8, 1.0 })
        {
            foreach (var waxing in new[] { true, false })
            {
                foreach (var southern in new[] { true, false })
                {
                    glyphs.Add(MeeusMoon.GetPhaseEmoji(illumination, waxing, southern));
                }
            }
        }

        return [.. glyphs.SelectMany(glyph => glyph.EnumerateRunes()).Distinct().OrderBy(rune => rune.Value)];
    }

    private static string Spell(IEnumerable<Rune> runes, string separator)
        => string.Join(separator, runes.Select(rune => $"U+{rune.Value:X4}"));

    [Fact]
    public void TheWebEmojiSubsetCarriesEveryGlyphTheWebDrawsFromIt()
    {
        File.Exists(SubsetPath).ShouldBeTrue(SubsetPath);

        var runes = RunesTheWebDrawsFromItsEmojiFace();
        var face = new FontFallbackResolver(SubsetPath, []);
        var missing = runes.Where(rune => face.TryResolveFont(rune) is null).ToList();

        // The failure names the command that fixes it, with the whole set (the subset is cut afresh each time).
        missing.ShouldBeEmpty(
            "regenerate the web subset with: python -m fontTools.subset src/TianWen.UI.Gui/Fonts/Noto-COLRv1.ttf "
            + "--unicodes=" + Spell(runes, ",")
            + " --output-file=src/TianWen.UI.Web/wwwroot/Fonts/NotoEmoji-Web.ttf; missing: " + Spell(missing, " "));
    }

    /// <summary>
    /// Each glyph DRAWS from the subset, and in its own colours, through the rasteriser the browser's colour-glyph
    /// atlas uses (DIR.Lib's <c>ManagedFontRasterizer</c>, which the CPU renderer shares).
    /// </summary>
    /// <remarks>
    /// The cmap only says a glyph is there. A subset that kept the cmap entry but lost the colour layers (a
    /// table dropped by the cut) would pass that and draw nothing, or draw a plain outline in the ink. So each
    /// glyph is drawn in MAGENTA, which none of them use: a colour glyph paints its own palette and ignores the
    /// ink, an outline comes out magenta. Not "is it chromatic": Noto's cloud is grey. The strip is written as
    /// a PNG so the cut can be looked at.
    /// </remarks>
    [Fact]
    public void EveryGlyphDrawsInItsOwnColoursFromTheSubset()
    {
        const int Cell = 48;
        var runes = RunesTheWebDrawsFromItsEmojiFace();
        using var renderer = new RgbaImageRenderer((uint)(Cell * runes.Count), Cell);
        renderer.Surface.Clear(new RGBAColor32(0, 0, 0, 0));

        for (var i = 0; i < runes.Count; i++)
        {
            renderer.DrawText(runes[i].ToString().AsSpan(), SubsetPath, 32f, new RGBAColor32(0xff, 0x00, 0xff, 0xff),
                new RectInt(new PointInt((i + 1) * Cell, Cell), new PointInt(i * Cell, 0)), TextAlign.Center, TextAlign.Center);
        }

        var pixels = renderer.Surface.Pixels;
        var width = (int)renderer.Width;
        var path = Path.Combine(SharedTestData.CreateTempTestOutputDir(), "web_emoji_subset.png");
        File.WriteAllBytes(path, PngWriter.Encode(pixels, width, Cell));

        // Per cell: how many pixels are inked (mostly opaque, so an edge's blend with the clear surface is not
        // read as a colour), and how many of those are not the magenta ink.
        List<string> blank = [];
        for (var i = 0; i < runes.Count; i++)
        {
            int inked = 0, ownColour = 0;
            for (var y = 0; y < Cell; y++)
            {
                for (var x = i * Cell; x < (i + 1) * Cell; x++)
                {
                    var p = (y * width + x) * 4;
                    if (pixels[p + 3] < 128)
                    {
                        continue;
                    }
                    inked++;
                    int r = pixels[p], g = pixels[p + 1], b = pixels[p + 2];
                    var isInk = Math.Abs(r - b) < 24 && g + 24 < Math.Min(r, b);
                    if (!isInk)
                    {
                        ownColour++;
                    }
                }
            }
            if (inked < 50 || ownColour < inked / 2)
            {
                blank.Add($"U+{runes[i].Value:X4} ({inked} inked, {ownColour} in its own colours)");
            }
        }

        blank.ShouldBeEmpty($"strip at {path}");
    }
}
