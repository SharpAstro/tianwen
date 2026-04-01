using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.View;

/// <summary>
/// CPU-side FITS image renderer for terminal output.
/// Applies stretch via <see cref="Image.StretchValue"/> and renders to an <see cref="RgbaImage"/>.
/// Supports Sixel (via <see cref="SixelRgbaImageRenderer"/>) and ASCII fallback.
/// </summary>
internal sealed class ConsoleImageRenderer
{
    private readonly SixelRgbaImageRenderer _renderer;

    public ConsoleImageRenderer(int width, int height)
    {
        _renderer = new SixelRgbaImageRenderer((uint)width, (uint)height);
    }

    /// <summary>
    /// Wraps an existing renderer — use this to render directly into a Canvas's surface.
    /// </summary>
    public ConsoleImageRenderer(SixelRgbaImageRenderer renderer)
    {
        _renderer = renderer;
    }

    public SixelRgbaImageRenderer Renderer => _renderer;
    public RgbaImage Surface => (RgbaImage)_renderer.Surface;

    /// <summary>
    /// Renders the given image document to the internal RGBA surface, applying stretch
    /// and optional curves boost (to compensate for Sixel quantization loss on faint detail).
    /// </summary>
    public void RenderImage(AstroImageDocument document, ViewerState state, float curvesBoost = 0f)
    {
        var image = document.UnstretchedImage;
        var stretch = document.ComputeStretchUniforms(state.StretchMode, state.StretchParameters);
        var surface = Surface;
        var surfW = surface.Width;
        var surfH = surface.Height;

        // Background level for boost curve midpoint
        var bgLevel = curvesBoost > 0 && document.PerChannelBackground.Length > 0
            ? document.PerChannelBackground[0]
            : 0.25f;

        float scale;
        int drawW, drawH, offsetX, offsetY;
        int srcOffsetX = 0, srcOffsetY = 0;

        if (state.ZoomToFit)
        {
            // Fit: scale image to fill surface, centered
            scale = Math.Min((float)surfW / image.Width, (float)surfH / image.Height);
            drawW = (int)(image.Width * scale);
            drawH = (int)(image.Height * scale);
            offsetX = (surfW - drawW) / 2;
            offsetY = (surfH - drawH) / 2;
        }
        else
        {
            // 1:1: each image pixel = one surface pixel, centered on image center
            scale = 1f;
            drawW = Math.Min(surfW, image.Width);
            drawH = Math.Min(surfH, image.Height);
            offsetX = (surfW - drawW) / 2;
            offsetY = (surfH - drawH) / 2;
            srcOffsetX = Math.Max(0, (image.Width - surfW) / 2);
            srcOffsetY = Math.Max(0, (image.Height - surfH) / 2);
        }

        // Clear surface
        surface.Clear(new RGBAColor32(0, 0, 0, 255));

        // Render stretched image pixels
        var isColor = image.ChannelCount >= 3;
        var pixels = surface.Pixels;

        for (var sy = 0; sy < drawH; sy++)
        {
            var imgY = state.ZoomToFit ? (int)(sy / scale) : sy + srcOffsetY;
            if (imgY >= image.Height) imgY = image.Height - 1;

            for (var sx = 0; sx < drawW; sx++)
            {
                var imgX = state.ZoomToFit ? (int)(sx / scale) : sx + srcOffsetX;
                if (imgX >= image.Width) imgX = image.Width - 1;

                byte r, g, b;
                if (isColor)
                {
                    var rv = image.GetChannelSpan(0)[imgY * image.Width + imgX];
                    var gv = image.GetChannelSpan(1)[imgY * image.Width + imgX];
                    var bv = image.GetChannelSpan(2)[imgY * image.Width + imgX];

                    if (stretch.Mode is StretchMode.Luma)
                    {
                        // Per-channel pedestal subtraction + luma-ratio scaling (matches GPU shader)
                        var prr = rv * stretch.NormFactor - stretch.Pedestal.R;
                        var prg = gv * stretch.NormFactor - stretch.Pedestal.G;
                        var prb = bv * stretch.NormFactor - stretch.Pedestal.B;
                        var lumaNorm = 0.2126f * prr + 0.7152f * prg + 0.0722f * prb;
                        var rescaled = (lumaNorm - (float)stretch.Shadows.R) * (float)stretch.Rescale.R;
                        var stretchedLuma = (float)Image.MidtonesTransferFunction(stretch.Midtones.R, rescaled);
                        var lumaScale = lumaNorm > 1e-7f ? stretchedLuma / lumaNorm : 0f;
                        var maxCh = MathF.Max(prr, MathF.Max(prg, prb));
                        if (lumaScale > 0f && maxCh > 1e-7f) { lumaScale = MathF.Min(lumaScale, 1f / maxCh); }
                        rv = prr * lumaScale;
                        gv = prg * lumaScale;
                        bv = prb * lumaScale;
                    }
                    else if (stretch.Mode is not StretchMode.None)
                    {
                        rv = Image.StretchValue(rv, stretch.NormFactor, stretch.Pedestal.R, stretch.Shadows.R,
                            stretch.Midtones.R, stretch.Rescale.R);
                        gv = Image.StretchValue(gv, stretch.NormFactor, stretch.Pedestal.G, stretch.Shadows.G,
                            stretch.Midtones.G, stretch.Rescale.G);
                        bv = Image.StretchValue(bv, stretch.NormFactor, stretch.Pedestal.B, stretch.Shadows.B,
                            stretch.Midtones.B, stretch.Rescale.B);
                    }

                    if (curvesBoost > 0)
                    {
                        rv = Image.ApplyBoost(rv, curvesBoost, bgLevel);
                        gv = Image.ApplyBoost(gv, curvesBoost, bgLevel);
                        bv = Image.ApplyBoost(bv, curvesBoost, bgLevel);
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

                    if (curvesBoost > 0)
                    {
                        v = Image.ApplyBoost(v, curvesBoost, bgLevel);
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
