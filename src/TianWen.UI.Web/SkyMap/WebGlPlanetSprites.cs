using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
using SharpAstro.Png;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using WebGl.Renderer;

namespace TianWen.UI.Web.SkyMap
{
    /// <summary>
    /// The atlas's planet sprites (<see cref="PlanetSpriteCache"/>) as WebGL textures, one per body (docs/plans/atlas-planet-detail.md,
    /// "A4: the textured disc"), drawn as a textured quad in the object pictures' way (<see cref="WebGlObjectPictures"/>).
    /// </summary>
    /// <remarks>
    /// The renderer takes a texture only from a URL, so a sprite goes over as a PNG data URL, which the page's <c>fetch</c> reads and the
    /// browser decodes off the main thread with <c>premultiplyAlpha: "none"</c>, keeping the sprite's straight alpha. The body's previous
    /// texture is drawn until the new one has loaded, and the page is asked for a frame when it has: the host paints per event.
    /// </remarks>
    internal sealed class WebGlPlanetSprites(WebGlRenderer renderer, Action requestRedraw, ILogger? logger) : IDisposable
    {
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

        private sealed class Slot
        {
            public PlanetSprite? Requested;
            public TextureHandle? Shown;
            public PlanetSprite? ShownSprite;
        }

        private readonly Dictionary<CatalogIndex, Slot> _slots = [];
        private readonly float[] _quad = new float[24];
        private PipelineHandle? _pipeline;
        private GpuBufferHandle? _buffer;

        /// <summary>Draws <paramref name="sprite"/> centred on (x, y), its globe <paramref name="radiusPx"/> on screen. False until a texture of the body has loaded.</summary>
        public bool Draw(PlanetSprite sprite, float x, float y, float radiusPx)
        {
            if (!_slots.TryGetValue(sprite.Body, out var slot))
            {
                _slots[sprite.Body] = slot = new Slot();
            }
            if (!ReferenceEquals(slot.Requested, sprite))
            {
                slot.Requested = sprite;
                _ = LoadAsync(slot, sprite);
            }
            if (slot.Shown is not { } texture || slot.ShownSprite is not { } shown)
            {
                return false;
            }

            float width = renderer.Width;
            float height = renderer.Height;
            if (width == 0f || height == 0f)
            {
                return false;
            }
            var half = (float)(shown.Side * radiusPx / shown.RadiusPx / 2);
            var left = ((x - half) / width * 2f) - 1f;
            var right = ((x + half) / width * 2f) - 1f;
            var top = 1f - ((y - half) / height * 2f);
            var bottom = 1f - ((y + half) / height * 2f);
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

            _pipeline ??= renderer.RegisterPipeline(new CustomPipelineDescriptor(
                VertexSource, FragmentSource,
                Attribs: [new VertexAttrib(0, 2), new VertexAttrib(1, 2)],
                Blend: PipelineBlend.AlphaOver));
            if (_buffer is { } buffer)
            {
                renderer.UpdateBuffer(buffer, _quad);
            }
            else
            {
                _buffer = buffer = renderer.CreateBuffer(_quad);
            }
            renderer.UsePipeline(_pipeline.Value);
            renderer.SetPipelineColor(new RGBAColor32(0xFF, 0xFF, 0xFF, 0xFF));
            renderer.BindTexture(texture);
            renderer.DrawBuffer(buffer, 0, 6);
            return true;
        }

        private async Task LoadAsync(Slot slot, PlanetSprite sprite)
        {
            try
            {
                var url = "data:image/png;base64," + Convert.ToBase64String(PngWriter.Encode(sprite.Rgba, sprite.Side, sprite.Side));
                var texture = await renderer.LoadTextureAsync(url);
                if (!ReferenceEquals(slot.Requested, sprite))
                {
                    // Superseded while it loaded: the newer sprite's load replaces it.
                    renderer.DestroyTexture(texture);
                    return;
                }
                if (slot.Shown is { } old)
                {
                    renderer.DestroyTexture(old);
                }
                slot.Shown = texture;
                slot.ShownSprite = sprite;
                requestRedraw();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Planet sprite for {Body} did not load", sprite.Body);
            }
        }

        public void Dispose()
        {
            foreach (var slot in _slots.Values)
            {
                if (slot.Shown is { } texture)
                {
                    renderer.DestroyTexture(texture);
                }
            }
            _slots.Clear();
            if (_buffer is { } buffer)
            {
                renderer.DestroyBuffer(buffer);
            }
        }
    }
}
