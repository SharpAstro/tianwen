using System;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using WebGl.Renderer;

namespace TianWen.UI.Web.SkyMap
{
    /// <summary>
    /// Object pictures as WebGL textures, for the browser atlas's info panel: the
    /// <see cref="ObjectPictureCache{TLoaded, THandle}"/> policy over the browser's own fetch.
    /// </summary>
    /// <remarks>
    /// <para>The browser fetches, caches (its HTTP cache) and decodes the thumbnail natively, off the main thread,
    /// through <see cref="WebGlRenderer.LoadTextureAsync"/>; Wikimedia's image server answers with
    /// <c>Access-Control-Allow-Origin: *</c>, so the texture is not tainted. There is no desktop store and no MD5 on
    /// this side: the URL is built from the hash prefix the bake recorded.</para>
    /// <para>The picture is a textured quad in its own small pipeline, placed in clip space from the slot's pixel
    /// rect. It draws in painter order with the panel around it, because every primitive the renderer emits
    /// switches back to its own program.</para>
    /// </remarks>
    internal sealed class WebGlObjectPictures : IDisposable
    {
        // Position in clip space and a texture coordinate, per vertex; six vertices make the quad.
        private const string VertexSource = """
            #version 300 es
            precision highp float;
            layout(location = 0) in vec2 aPosition;
            layout(location = 1) in vec2 aTexCoord;
            out vec2 vTexCoord;
            void main() {
                vTexCoord = aTexCoord;
                gl_Position = vec4(aPosition, 0.0, 1.0);
            }
            """;

        private const string FragmentSource = """
            #version 300 es
            precision highp float;
            uniform sampler2D uTexture;
            uniform vec4 uColor;
            in vec2 vTexCoord;
            out vec4 FragColor;
            void main() {
                FragColor = texture(uTexture, vTexCoord) * uColor;
            }
            """;

        private sealed record LoadedTexture(TextureHandle Handle);

        private readonly WebGlRenderer _renderer;
        private readonly ObjectPictureCache<LoadedTexture, TextureHandle> _cache;
        private readonly float[] _quad = new float[24];
        private PipelineHandle? _pipeline;
        private GpuBufferHandle? _buffer;

        /// <summary>How many times a picture that was in hand has been drawn.</summary>
        public int Drawn { get; private set; }

        public WebGlObjectPictures(WebGlRenderer renderer, Action requestRedraw)
        {
            _renderer = renderer;
            _cache = new ObjectPictureCache<LoadedTexture, TextureHandle>(
                load: LoadAsync,
                adopt: loaded => loaded.Handle,
                release: renderer.DestroyTexture,
                requestRedraw: requestRedraw);
        }

        private async Task<LoadedTexture?> LoadAsync(ObjectArticleImage image, int pixels)
            => new LoadedTexture(await _renderer.LoadTextureAsync(image.ThumbnailUrl(pixels)));

        /// <summary>
        /// Draws <paramref name="image"/> fitted inside <paramref name="rect"/> once the browser has it. The width
        /// asked of Wikimedia is the slot's own backing-pixel width, so a high-DPI screen gets a sharper picture.
        /// </summary>
        public void Draw(in ObjectArticleImage image, RectF32 rect)
        {
            if (rect.Width < 1f || rect.Height < 1f
                || !_cache.TryGet(in image, (int)MathF.Ceiling(rect.Width), out var texture))
            {
                return;
            }

            float width = _renderer.Width;
            float height = _renderer.Height;
            if (width == 0f || height == 0f)
            {
                return;
            }

            // The thumbnail keeps the original's aspect ratio, which the table records.
            var fitted = ObjectPictureCache<LoadedTexture, TextureHandle>.Fit(rect, image.Width, image.Height);
            var left = (fitted.X / width * 2f) - 1f;
            var right = ((fitted.X + fitted.Width) / width * 2f) - 1f;
            var top = 1f - (fitted.Y / height * 2f);
            var bottom = 1f - ((fitted.Y + fitted.Height) / height * 2f);

            // Two triangles; texture row 0 is the picture's top row, so v runs down the screen.
            ReadOnlySpan<float> quad =
            [
                left, top, 0f, 0f,
                right, top, 1f, 0f,
                right, bottom, 1f, 1f,
                left, top, 0f, 0f,
                right, bottom, 1f, 1f,
                left, bottom, 0f, 1f,
            ];
            quad.CopyTo(_quad);

            _pipeline ??= _renderer.RegisterPipeline(new CustomPipelineDescriptor(
                VertexSource, FragmentSource,
                Attribs: [new VertexAttrib(0, 2), new VertexAttrib(1, 2)],
                Blend: PipelineBlend.AlphaOver));
            if (_buffer is { } buffer)
            {
                _renderer.UpdateBuffer(buffer, _quad);
            }
            else
            {
                _buffer = buffer = _renderer.CreateBuffer(_quad);
            }

            _renderer.UsePipeline(_pipeline.Value);
            _renderer.SetPipelineColor(new RGBAColor32(0xFF, 0xFF, 0xFF, 0xFF));
            _renderer.BindTexture(texture);
            _renderer.DrawBuffer(buffer, 0, 6);
            Drawn++;
        }

        public void Dispose()
        {
            _cache.Clear();
            if (_buffer is { } buffer)
            {
                _renderer.DestroyBuffer(buffer);
            }
        }
    }
}
