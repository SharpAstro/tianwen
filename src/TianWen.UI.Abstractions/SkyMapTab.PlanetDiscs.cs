using System;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The planets' textured discs (docs/plans/atlas-planet-detail.md, "A4: the textured disc", #1037): a body whose disc is big enough
    /// on screen is drawn as its sprite (<see cref="PlanetSpriteCache"/>, built off the render thread from <see cref="PlanetTextures"/>)
    /// through <see cref="DrawPlanetSprite"/>, the host's seam; a smaller one stays the dot, at no cost beyond the size test.
    /// </summary>
    public partial class SkyMapTab<TSurface>
    {
        private PlanetTextures? _planetTextures;
        private PlanetSpriteCache? _planetSprites;

        /// <summary>
        /// The colour maps the discs are drawn from. The desktop hosts leave it reading the atlas beside the executable; the browser
        /// sets <see cref="PlanetTextures.AtlasPath"/> to null and hands the atlas over itself.
        /// </summary>
        public PlanetTextures PlanetTextures => _planetTextures ??= new PlanetTextures(
            () => _timeProvider?.GetUtcNow() ?? default, Logger) { RequestRedraw = RequestPlanetFrame };

        /// <summary>The sprites, for the tests that count their builds.</summary>
        internal PlanetSpriteCache PlanetSprites => _planetSprites ??= new PlanetSpriteCache(RequestPlanetFrame);

        private PlanetSpriteCache? _panelSprites;

        /// <summary>
        /// The info panel's disc sprites: a cache of their own, keyed at the PANEL's slot size, so the panel and the map do not
        /// evict each other's sprite for the same body.
        /// </summary>
        internal PlanetSpriteCache PanelSprites => _panelSprites ??= new PlanetSpriteCache(RequestPlanetFrame, PlanetSpriteCache.PanelChannel);

        /// <summary>The info panel's disc slot as the last frame painted it, or null when the panel had none: what the tests read.</summary>
        internal RectF32? LastPanelDiscRect { get; private set; }

        /// <summary>Whether anything about a disc has been touched (its textures or its sprites), for the test of a wide field.</summary>
        internal bool PlanetDiscsTouched => _planetTextures is not null || _planetSprites is not null;

        /// <summary>How many discs have been drawn as sprites, for the tests.</summary>
        internal int PlanetSpritesDrawn { get; private set; }

        /// <summary>
        /// Asks for a frame once a sprite or a texture has landed, from a background thread. The desktop renders on
        /// <see cref="SkyMapState.NeedsRedraw"/>; the browser paints per event and also asks its page (<c>WebSkyMapTab</c>).
        /// </summary>
        protected virtual void RequestPlanetFrame() => State.NeedsRedraw = true;

        /// <summary>
        /// Draws <paramref name="sprite"/> centred on (<paramref name="x"/>, <paramref name="y"/>), its globe
        /// <paramref name="radiusPx"/> on screen (the sprite scaled by <paramref name="radiusPx"/> over its own
        /// <see cref="PlanetSprite.RadiusPx"/>), its straight alpha blended over what is there. False when this host cannot draw it, and
        /// the dot is drawn instead. The base draws on the CPU surface (<see cref="RgbaImage"/>: the tests and the terminal); the GPU
        /// hosts override it.
        /// </summary>
        protected virtual bool DrawPlanetSprite(PlanetSprite sprite, float x, float y, float radiusPx)
        {
            if (Renderer.Surface is not RgbaImage image)
            {
                return false;
            }
            var scale = radiusPx / sprite.RadiusPx;
            var half = sprite.Side * scale / 2;
            var x0 = (int)MathF.Floor((float)(x - half));
            var y0 = (int)MathF.Floor((float)(y - half));
            var x1 = (int)MathF.Ceiling((float)(x + half));
            var y1 = (int)MathF.Ceiling((float)(y + half));
            (x0, y0, x1, y1) = (Math.Max(x0, 0), Math.Max(y0, 0), Math.Min(x1, image.Width), Math.Min(y1, image.Height));
            var rgba = sprite.Rgba;
            for (var py = y0; py < y1; py++)
            {
                var sy = (int)Math.Floor(((py + 0.5 - y) / scale) + (sprite.Side / 2.0));
                if (sy < 0 || sy >= sprite.Side)
                {
                    continue;
                }
                for (var px = x0; px < x1; px++)
                {
                    var sx = (int)Math.Floor(((px + 0.5 - x) / scale) + (sprite.Side / 2.0));
                    if (sx < 0 || sx >= sprite.Side)
                    {
                        continue;
                    }
                    var i = ((sy * sprite.Side) + sx) * 4;
                    if (rgba[i + 3] > 0)
                    {
                        image.BlendPixelAt(px, py, new RGBAColor32(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]));
                    }
                }
            }
            return true;
        }

        /// <summary>How many bodies a host has ray-traced through <see cref="DrawPlanetBody"/>, for the tests.</summary>
        internal int PlanetBodiesTraced { get; private set; }

        /// <summary>
        /// Whether this host ray-traces a body itself (<see cref="DrawPlanetBody"/>): the GPU hosts. The base, the CPU surface, draws the
        /// CPU sprite of the same view (<see cref="PlanetView.ToAspect"/>) instead, keeping <see cref="PlanetaryRender.RenderRgba"/> as the
        /// reference the GPU hosts are held to.
        /// </summary>
        protected virtual bool TracesPlanetBodies => false;

        /// <summary>
        /// Draws the body <paramref name="view"/> describes centred on (<paramref name="x"/>, <paramref name="y"/>), its globe
        /// <paramref name="radiusPx"/> on screen, RAY-TRACED in the view's own frame (docs/plans/atlas-planet-detail.md, A5, "The bodies on
        /// the GPU"): the spheroid, Minnaert's law, Saturn's rings with both shadows, coverage as alpha, the colour from
        /// <paramref name="map"/> (uploaded once per <paramref name="textureVersion"/>) or <paramref name="flat"/>, luminance in
        /// <paramref name="nightTint"/> under Night. Nothing is built per frame, so time running costs nothing here. Asked only where
        /// <see cref="TracesPlanetBodies"/>; false while the body's map is still uploading, and the caller draws the flat dot.
        /// </summary>
        protected virtual bool DrawPlanetBody(in PlanetView view, float x, float y, float radiusPx, PlanetColourMap? map, int textureVersion,
            RGBAColor32 flat, RGBAColor32? nightTint) => false;

        /// <summary>
        /// The info panel's disc: the selected body's sprite in its slot, as large as the slot holds the whole sprite
        /// (<see cref="PlanetDiscSprite.PanelRadiusPx"/>), NORTH UP AND EAST LEFT, as the sky shows it to the eye (the map's own
        /// view may be turned; the panel is not), phased, ringed and Night-tinted as on the map. Built by the panel's own cache, keyed
        /// at the slot's size, so a frame that changes nothing builds nothing; the slot stays dark until the sprite lands.
        /// </summary>
        private void DrawPanelDisc(in PlanetAspect aspect, RectF32 slot)
        {
            PlanetColourMap? map = null;
            var credit = "";
            if (aspect.Planet != CatalogIndex.Sol)
            {
                if (!PlanetTextures.TryGet(aspect.Planet, out var texture, out credit))
                {
                    return;
                }
                map = texture;
            }
            var radius = PlanetDiscSprite.PanelRadiusPx(aspect.Planet, slot.Width, slot.Height);
            var night = GuiTheme.State == UiThemeState.Night ? GuiTheme.Palette.Accent : (RGBAColor32?)null;
            var pole = PlanetDiscSprite.PoleScreenAngle(aspect.PolePositionAngle, screenNorthDeg: -90, mirrored: false);
            if (PanelSprites.Get(aspect, 2 * radius, pole, false, map, credit, SkyMapRenderer.GetPlanetColor(aspect.Planet), night,
                    GuiTheme.PaletteGeneration, PlanetTextures.Version) is { } sprite)
            {
                DrawPlanetSprite(sprite, slot.X + (slot.Width / 2), slot.Y + (slot.Height / 2), (float)radius);
            }
        }

        // The disc in place of the dot, when it is big enough and a sprite is in hand; false draws the dot.
        private bool TryDrawPlanetDisc(in PlanetAspect aspect, double ra, double dec, float sx, float sy, double ppr, float cx, float cy,
            float dotRadius, bool belowHorizon, out float discRadius)
        {
            var diameterPx = PlanetSpriteCache.DiscDiameterPx(aspect, ppr);
            discRadius = (float)(diameterPx / 2);
            if (!PlanetSpriteCache.WantsSprite(diameterPx, dotRadius)
                || !PlanetDiscSprite.TryScreenNorth(ra, dec, State.CurrentViewMatrix, ppr, cx, cy, out var north, out var mirrored))
            {
                return false;
            }

            PlanetColourMap? map = null;
            var credit = "";
            if (aspect.Planet != CatalogIndex.Sol)
            {
                if (!PlanetTextures.TryGet(aspect.Planet, out var texture, out credit))
                {
                    return false;
                }
                map = texture;
            }
            var night = GuiTheme.State == UiThemeState.Night ? GuiTheme.Palette.Accent : (RGBAColor32?)null;
            var pole = PlanetDiscSprite.PoleScreenAngle(aspect.PolePositionAngle, north, mirrored);
            if (PlanetSprites.Get(aspect, diameterPx, pole, mirrored, map, credit, SkyMapRenderer.GetPlanetColor(aspect.Planet), night,
                    GuiTheme.PaletteGeneration, PlanetTextures.Version, belowHorizon) is not { } sprite)
            {
                return false;
            }
            var drawn = DrawPlanetSprite(sprite, sx, sy, discRadius);
            if (drawn)
            {
                PlanetSpritesDrawn++;
            }
            return drawn;
        }
    }
}
