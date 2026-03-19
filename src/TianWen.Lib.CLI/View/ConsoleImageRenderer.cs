using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.View;

/// <summary>
/// CPU-side FITS image renderer for terminal output.
/// Applies stretch via <see cref="Image.StretchValue"/> and renders to an <see cref="RgbaImage"/>.
/// Supports Sixel (via <see cref="RgbaImageRenderer"/>) and ASCII fallback.
/// </summary>
internal sealed class ConsoleImageRenderer
{
    private readonly RgbaImageRenderer _renderer;

    public ConsoleImageRenderer(int width, int height)
    {
        _renderer = new RgbaImageRenderer((uint)width, (uint)height);
    }

    public RgbaImageRenderer Renderer => _renderer;
    public RgbaImage Surface => (RgbaImage)_renderer.Surface;

    /// <summary>
    /// Renders the given image document to the internal RGBA surface, applying stretch.
    /// </summary>
    public void RenderImage(AstroImageDocument document, ViewerState state)
    {
        var image = document.UnstretchedImage;
        var stretch = document.ComputeStretchUniforms(state.StretchMode, state.StretchParameters);
        var surface = Surface;
        var surfW = surface.Width;
        var surfH = surface.Height;

        // Compute scale to fit image in the surface
        var scale = Math.Min((float)surfW / image.Width, (float)surfH / image.Height);
        var drawW = (int)(image.Width * scale);
        var drawH = (int)(image.Height * scale);
        var offsetX = (surfW - drawW) / 2;
        var offsetY = (surfH - drawH) / 2;

        // Clear surface
        surface.Clear(new RGBAColor32(0, 0, 0, 255));

        // Render stretched image pixels
        var isColor = image.ChannelCount >= 3;
        var pixels = surface.Pixels;

        for (var sy = 0; sy < drawH; sy++)
        {
            var imgY = (int)(sy / scale);
            if (imgY >= image.Height) imgY = image.Height - 1;

            for (var sx = 0; sx < drawW; sx++)
            {
                var imgX = (int)(sx / scale);
                if (imgX >= image.Width) imgX = image.Width - 1;

                byte r, g, b;
                if (isColor)
                {
                    var rv = image.GetChannelSpan(0)[imgY * image.Width + imgX];
                    var gv = image.GetChannelSpan(1)[imgY * image.Width + imgX];
                    var bv = image.GetChannelSpan(2)[imgY * image.Width + imgX];

                    if (stretch.Mode is not StretchMode.None)
                    {
                        rv = Image.StretchValue(rv, stretch.NormFactor, stretch.Pedestal.R, stretch.Shadows.R,
                            stretch.Midtones.R, stretch.Rescale.R);
                        gv = Image.StretchValue(gv, stretch.NormFactor, stretch.Pedestal.G, stretch.Shadows.G,
                            stretch.Midtones.G, stretch.Rescale.G);
                        bv = Image.StretchValue(bv, stretch.NormFactor, stretch.Pedestal.B, stretch.Shadows.B,
                            stretch.Midtones.B, stretch.Rescale.B);
                    }

                    r = (byte)(Math.Clamp(rv, 0f, 1f) * 255);
                    g = (byte)(Math.Clamp(gv, 0f, 1f) * 255);
                    b = (byte)(Math.Clamp(bv, 0f, 1f) * 255);
                }
                else
                {
                    var v = image.GetChannelSpan(0)[imgY * image.Width + imgX];

                    if (stretch.Mode is not StretchMode.None)
                    {
                        v = Image.StretchValue(v, stretch.NormFactor, stretch.Pedestal.R, stretch.Shadows.R,
                            stretch.Midtones.R, stretch.Rescale.R);
                    }

                    var bv = (byte)(Math.Clamp(v, 0f, 1f) * 255);
                    r = g = b = bv;
                }

                var px = offsetX + sx;
                var py = offsetY + sy;
                var idx = (py * surfW + px) * 4;
                pixels[idx] = r;
                pixels[idx + 1] = g;
                pixels[idx + 2] = b;
                pixels[idx + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Renders the image to a stream as Sixel data.
    /// </summary>
    public void EncodeSixel(Stream output)
    {
        var surface = Surface;
        SixelEncoder.Encode(surface.Pixels, surface.Width, surface.Height, 4, output);
    }
}
