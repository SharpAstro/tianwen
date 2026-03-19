namespace TianWen.UI.Console;

/// <summary>
/// ASCII renderer using Unicode half-block characters with VT SGR 24-bit color.
/// Renders an RGBA pixel buffer to terminal output with 2 vertical pixels per cell.
/// </summary>
public static class AsciiRenderer
{
    /// <summary>
    /// Renders RGBA pixel data to the given TextWriter using half-block characters.
    /// Each terminal cell shows 2 vertical pixels: upper half (▀) foreground, lower half background.
    /// Uses SGR 24-bit color (ESC[38;2;r;g;bm for FG, ESC[48;2;r;g;bm for BG).
    /// </summary>
    public static void Render(byte[] pixels, int width, int height, TextWriter output)
    {
        // Process 2 rows at a time (upper pixel = foreground, lower pixel = background)
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x++)
            {
                var topIdx = (y * width + x) * 4;
                var topR = pixels[topIdx];
                var topG = pixels[topIdx + 1];
                var topB = pixels[topIdx + 2];

                if (y + 1 < height)
                {
                    var botIdx = ((y + 1) * width + x) * 4;
                    var botR = pixels[botIdx];
                    var botG = pixels[botIdx + 1];
                    var botB = pixels[botIdx + 2];

                    // Upper half block (▀): foreground = top pixel, background = bottom pixel
                    output.Write($"\x1b[38;2;{topR};{topG};{topB}m\x1b[48;2;{botR};{botG};{botB}m\u2580");
                }
                else
                {
                    // Last row (odd height): just the top pixel
                    output.Write($"\x1b[38;2;{topR};{topG};{topB}m\u2580");
                }
            }
            output.Write("\x1b[0m\n"); // Reset and newline
        }
    }
}
