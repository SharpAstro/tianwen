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
