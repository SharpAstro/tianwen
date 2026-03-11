using ImageMagick;
using Silk.NET.OpenGL;

namespace TianWen.UI.OpenGL;

/// <summary>
/// A cached glyph atlas texture for OpenGL text rendering.
/// Rasterises requested glyphs using ImageMagick into a single GPU texture.
/// </summary>
internal sealed class GlFontAtlas : IDisposable
{
    private readonly record struct GlyphKey(string Font, float Size, char Character);

    internal readonly record struct GlyphInfo(float U0, float V0, float U1, float V1, int Width, int Height, float AdvanceX);

    private readonly GL _gl;
    private readonly Dictionary<GlyphKey, GlyphInfo> _glyphs = new();

    private uint _textureHandle;
    private readonly int _atlasWidth;
    private readonly int _atlasHeight;
    private int _cursorX;
    private int _cursorY;
    private int _rowHeight;

    private MagickImage? _staging;

    private int _dirtyX0;
    private int _dirtyY0;
    private int _dirtyX1;
    private int _dirtyY1;

    public uint TextureHandle => _textureHandle;

    public GlFontAtlas(GL gl, int initialWidth = 512, int initialHeight = 512)
    {
        _gl = gl;
        _atlasWidth = initialWidth;
        _atlasHeight = initialHeight;
        _staging = new MagickImage(MagickColors.Transparent, (uint)initialWidth, (uint)initialHeight);
        ResetDirtyRegion();

        _textureHandle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _textureHandle);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        // Use float texture — Magick.NET-Q16-HDRI returns float values natively
        gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f,
            (uint)initialWidth, (uint)initialHeight, 0,
            PixelFormat.Rgba, PixelType.Float, ReadOnlySpan<float>.Empty);
    }

    public GlyphInfo GetGlyph(string fontPath, float fontSize, char character)
    {
        fontSize = MathF.Round(fontSize);
        var key = new GlyphKey(fontPath, fontSize, character);
        if (_glyphs.TryGetValue(key, out var existing))
        {
            return existing;
        }

        return RasterizeGlyph(key);
    }

    public void Flush()
    {
        if (_dirtyX0 >= _dirtyX1 || _dirtyY0 >= _dirtyY1 || _staging is null)
        {
            return;
        }

        var regionW = _dirtyX1 - _dirtyX0;
        var regionH = _dirtyY1 - _dirtyY0;

        _gl.BindTexture(TextureTarget.Texture2D, _textureHandle);

        using var pixels = _staging.GetPixelsUnsafe();
        var rawPixels = pixels.GetArea(_dirtyX0, _dirtyY0, (uint)regionW, (uint)regionH);
        if (rawPixels is null)
        {
            return;
        }

        var channels = (int)_staging.ChannelCount;
        var pixelCount = regionW * regionH;
        var rgbaF = new float[pixelCount * 4];

        // Magick.NET-Q16-HDRI returns float values in [0, Quantum.Max]; normalize to [0, 1]
        var quantumScale = 1f / Quantum.Max;
        for (var i = 0; i < pixelCount; i++)
        {
            var srcOffset = i * channels;
            var dstOffset = i * 4;

            if (channels >= 3)
            {
                rgbaF[dstOffset] = rawPixels[srcOffset] * quantumScale;
                rgbaF[dstOffset + 1] = rawPixels[srcOffset + 1] * quantumScale;
                rgbaF[dstOffset + 2] = rawPixels[srcOffset + 2] * quantumScale;
                rgbaF[dstOffset + 3] = channels >= 4 ? rawPixels[srcOffset + 3] * quantumScale : 1f;
            }
        }

        _gl.TexSubImage2D<float>(TextureTarget.Texture2D, 0,
            _dirtyX0, _dirtyY0, (uint)regionW, (uint)regionH,
            PixelFormat.Rgba, PixelType.Float, rgbaF.AsSpan());

        ResetDirtyRegion();
    }

    public (double Width, double Height) MeasureText(string fontPath, float fontSize, string text)
    {
        fontSize = MathF.Round(fontSize);
        var settings = new MagickReadSettings
        {
            Font = fontPath,
            FontPointsize = fontSize,
            BackgroundColor = MagickColors.Transparent,
            FillColor = MagickColors.White
        };

        using var label = new MagickImage($"label:{text}", settings);
        return (label.Width, label.Height);
    }

    public void Dispose()
    {
        if (_textureHandle != 0)
        {
            _gl.DeleteTexture(_textureHandle);
            _textureHandle = 0;
        }

        _staging?.Dispose();
        _staging = null;
    }

    private GlyphInfo RasterizeGlyph(GlyphKey key)
    {
        if (char.IsWhiteSpace(key.Character))
        {
            var refGlyph = GetGlyph(key.Font, key.Size, 'n');
            var info = new GlyphInfo(0, 0, 0, 0, 0, 0, refGlyph.AdvanceX);
            _glyphs[key] = info;
            return info;
        }

        var settings = new MagickReadSettings
        {
            Font = key.Font,
            FontPointsize = key.Size,
            BackgroundColor = MagickColors.Transparent,
            FillColor = MagickColors.White
        };

        using var glyphImage = new MagickImage($"label:{key.Character}", settings);

        var glyphWidth = (int)glyphImage.Width;
        var glyphHeight = (int)glyphImage.Height;

        if (glyphWidth == 0 || glyphHeight == 0)
        {
            return default;
        }

        if (_cursorX + glyphWidth > _atlasWidth)
        {
            _cursorX = 0;
            _cursorY += _rowHeight + 1;
            _rowHeight = 0;
        }

        if (_cursorY + glyphHeight > _atlasHeight)
        {
            return default;
        }

        _staging?.Composite(glyphImage, _cursorX, _cursorY, CompositeOperator.Over);

        _dirtyX0 = Math.Min(_dirtyX0, _cursorX);
        _dirtyY0 = Math.Min(_dirtyY0, _cursorY);
        _dirtyX1 = Math.Max(_dirtyX1, _cursorX + glyphWidth);
        _dirtyY1 = Math.Max(_dirtyY1, _cursorY + glyphHeight);

        var glyphInfo = new GlyphInfo(
            U0: _cursorX / (float)_atlasWidth,
            V0: _cursorY / (float)_atlasHeight,
            U1: (_cursorX + glyphWidth) / (float)_atlasWidth,
            V1: (_cursorY + glyphHeight) / (float)_atlasHeight,
            Width: glyphWidth,
            Height: glyphHeight,
            AdvanceX: glyphWidth
        );

        _glyphs[key] = glyphInfo;
        _cursorX += glyphWidth + 1;
        _rowHeight = Math.Max(_rowHeight, glyphHeight);

        return glyphInfo;
    }

    private void ResetDirtyRegion()
    {
        _dirtyX0 = _atlasWidth;
        _dirtyY0 = _atlasHeight;
        _dirtyX1 = 0;
        _dirtyY1 = 0;
    }
}
