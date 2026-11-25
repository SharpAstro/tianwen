using System.Collections.Generic;
using System.Text;
using DIR.Lib;
using SdlVulkan.Renderer;

namespace TianWen.UI.Gui;

/// <summary>
/// Rasterises, on other threads, the glyphs of the window's face that this computer has not drawn before.
/// </summary>
/// <remarks>
/// <para>
/// A glyph the atlas has never held is rasterised where it is first needed, and a text is MEASURED (its glyphs'
/// advances and boxes come from the rasterised glyph) before it is drawn, so a new character rasterises on the render
/// thread: 138 ms for the ampersand in an object picture's credit, the first time any object panel showed one,
/// a paint of 78 ms in a minute of otherwise 7 ms ones (2026-11-25). The atlas keeps what it rasterises on disk, so
/// each glyph costs this once per computer, but nothing chose WHICH paint pays it.
/// </para>
/// <para>
/// The printable Latin range and the marks the panels print (degree, middle dot, primes, arrows, dashes) are handed to
/// <see cref="VkRenderer.PreWarmSdfGlyphBatch"/> a few seconds after the window opens: by then the atlas has read its
/// disk cache, so it rasterises, in the background, only what is not there yet, and on later launches nothing.
/// </para>
/// </remarks>
internal static class GlyphWarmUp
{
    private static readonly int[] Marks =
    [
        0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2026, 0x2032, 0x2033,
        0x2190, 0x2191, 0x2192, 0x2193, 0x2212, 0x2248, 0x2264, 0x2265,
    ];

    /// <summary>Queues every glyph of the set for <paramref name="fontPath"/>.</summary>
    public static void Queue(VkRenderer renderer, string? fontPath)
    {
        if (string.IsNullOrEmpty(fontPath))
        {
            return;
        }

        var keys = new List<(string Font, Rune Character, int CharCode, GlyphMapHint Hint)>(256);
        for (var c = 0x21; c <= 0x7E; c++)
        {
            keys.Add((fontPath, new Rune(c), -1, GlyphMapHint.Auto));
        }

        for (var c = 0xA1; c <= 0xFF; c++)
        {
            keys.Add((fontPath, new Rune(c), -1, GlyphMapHint.Auto));
        }

        foreach (var c in Marks)
        {
            keys.Add((fontPath, new Rune(c), -1, GlyphMapHint.Auto));
        }

        renderer.PreWarmSdfGlyphBatch(keys);
    }
}
