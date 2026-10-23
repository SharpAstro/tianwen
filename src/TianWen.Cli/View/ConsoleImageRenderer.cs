using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.View;

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
    /// Wraps an existing renderer: use this to render directly into a Canvas's surface.
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
    /// Uses the shared CPU pipeline helpers (<see cref="Image.StretchChannelCpu"/>,
    /// <see cref="Image.StretchLumaPixelCpu"/>, <see cref="Image.ApplyCurveLut"/>,
    /// <see cref="Image.ApplyBoost"/>, <see cref="Image.ApplyHdr"/>) which mirror the GLSL
    /// fragment shader exactly.
    /// </summary>
    public void RenderImage(AstroImageDocument document, ViewerState state, float curvesBoost = 0f)
    {
        var image = document.UnstretchedImage;
        var stretch = document.ComputeStretchUniforms(state.StretchMode, state.StretchParameters) with
        {
            BackgroundSky = state.BackgroundSky,
        };
        var surface = Surface;
        var surfW = surface.Width;
        var surfH = surface.Height;

        // The POST-stretch sky, as the GUI's shader is given it (curvesMidpoint): the background reduce's anchor, and, once
        // the reduce has moved it, the level the boost and the finish pivot on. This read the LINEAR background of channel 0,
        // which put the boost's and the finish's pivot a long way below the sky the picture shows.
        var sky = document.PerChannelBackground.Length > 0
            ? stretch.ComputePostStretchBackground(document.PerChannelBackground, document.LumaBackground)
            : 0.25f;
        var reduceCompression = stretch.BackgroundReduceCompression(sky);
        var reduce = reduceCompression < 1f;
        var bgLevel = stretch.ReducedBackground(sky);

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
        var ch0 = image.GetChannelSpan(0);
        var ch1 = isColor ? image.GetChannelSpan(1) : default;
        var ch2 = isColor ? image.GetChannelSpan(2) : default;
        var imgW = image.Width;

        var curveLut = state.CurveData.IsDefaultOrEmpty ? default : state.CurveData.AsSpan();
        var hasLut = state.CurvesMode == 1 && !curveLut.IsEmpty;

        for (var sy = 0; sy < drawH; sy++)
        {
            var imgY = state.ZoomToFit ? (int)(sy / scale) : sy + srcOffsetY;
            if (imgY >= image.Height) imgY = image.Height - 1;

            for (var sx = 0; sx < drawW; sx++)
            {
                var imgX = state.ZoomToFit ? (int)(sx / scale) : sx + srcOffsetX;
                if (imgX >= imgW) imgX = imgW - 1;
                var sampleIdx = imgY * imgW + imgX;

                float rv, gv, bv;
                if (isColor)
                {
                    var rRaw = ch0[sampleIdx];
                    var gRaw = ch1[sampleIdx];
                    var bRaw = ch2[sampleIdx];

                    if (stretch.Mode is StretchMode.Luma)
                    {
                        (rv, gv, bv) = Image.StretchLumaPixelCpu(rRaw, gRaw, bRaw, stretch);
                    }
                    else if (stretch.Mode is StretchMode.None)
                    {
                        // Linear display: apply WhiteBalance directly (mirrors the GLSL / RenderStretchedRgba
                        // None path). Neutral WB == passthrough.
                        rv = MathF.Max(rRaw * stretch.WhiteBalance.R, 0f);
                        gv = MathF.Max(gRaw * stretch.WhiteBalance.G, 0f);
                        bv = MathF.Max(bRaw * stretch.WhiteBalance.B, 0f);
                    }
                    else
                    {
                        rv = Image.StretchChannelCpu(rRaw, 0, stretch);
                        gv = Image.StretchChannelCpu(gRaw, 1, stretch);
                        bv = Image.StretchChannelCpu(bRaw, 2, stretch);
                    }
                }
                else
                {
                    var v = ch0[sampleIdx];
                    rv = stretch.Mode is StretchMode.None ? v : Image.StretchChannelCpu(v, 0, stretch);
                    gv = bv = rv;
                }

                if (reduce)
                {
                    rv = Image.ReduceBackgroundValue(rv, sky, reduceCompression);
                    gv = Image.ReduceBackgroundValue(gv, sky, reduceCompression);
                    bv = Image.ReduceBackgroundValue(bv, sky, reduceCompression);
                }

                if (hasLut)
                {
                    rv = Image.ApplyCurveLut(rv, curveLut);
                    gv = Image.ApplyCurveLut(gv, curveLut);
                    bv = Image.ApplyCurveLut(bv, curveLut);
                }
                else if (curvesBoost > 0)
                {
                    rv = Image.ApplyBoost(rv, curvesBoost, bgLevel);
                    gv = Image.ApplyBoost(gv, curvesBoost, bgLevel);
                    bv = Image.ApplyBoost(bv, curvesBoost, bgLevel);
                }

                if (state.HdrAmount > 0f)
                {
                    rv = Image.ApplyHdr(rv, state.HdrAmount, state.HdrKnee);
                    gv = Image.ApplyHdr(gv, state.HdrAmount, state.HdrKnee);
                    bv = Image.ApplyHdr(bv, state.HdrAmount, state.HdrKnee);
                }

                if (state.FinishContrast > 0f || state.FinishSaturation != 1f)
                {
                    Image.ApplyFinish(ref rv, ref gv, ref bv, state.FinishContrast, state.FinishSaturation, bgLevel, stretch.LumaWeights);
                }

                var r = (byte)(Math.Clamp(rv, 0f, 1f) * 255);
                var g = (byte)(Math.Clamp(gv, 0f, 1f) * 255);
                var b = (byte)(Math.Clamp(bv, 0f, 1f) * 255);

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
