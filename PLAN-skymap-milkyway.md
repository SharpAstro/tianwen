# Plan: Milky Way Background for Sky Map

Render a diffuse Milky Way background on the sky map, matching the visual
quality of Stellarium's approach but with freely-licensed imagery.

## Stellarium's Approach (Reference)

- Single 2048x1024 equirectangular PNG (Mellinger All-Sky Panorama, ~980 KB)
- Full-screen quad (4 vertices, no sky sphere mesh)
- Fragment shader: invert projection -> J2000 unit vector -> spherical angles -> texture UV
- Additive blend (`GL_ONE, GL_ONE`) on top of sky background color
- Brightness modulated by Bortle index / light pollution / atmosphere
- Drawn first (before grid, stars, constellations)
- Per-project licensed from Axel Mellinger (not reusable)

## Texture Source Options

| Source | Resolution | License | Quality | Notes |
|--------|-----------|---------|---------|-------|
| ESA Gaia DR3 all-sky | 4096x2048+ | CC BY-SA 4.0 | Excellent | Best optical band option; color composite from BP/RP photometry |
| NASA 2MASS J-band | 4096x2048 | Public domain | Good | Near-infrared, shows dust lanes and bulge well |
| NASA COBE/DIRBE | 1024x512 | Public domain | Low-res | Far-infrared, iconic but blurry |
| Self-generated from Tycho-2 | Any | None needed | Moderate | Gaussian-blur unresolved starlight from our own catalog; no licensing, deterministic |
| ESO/S. Brunier panorama | 4096x2048 | CC BY 4.0 | Excellent | Real astrophotography, widely used |

**Recommendation**: Start with ESA Gaia DR3 (best quality + clear license), fall back
to self-generated from Tycho-2 if Gaia is too large or complex to obtain as a
pre-rendered equirectangular.

## Implementation

### Phase 1: Shader + Full-Screen Quad

- [ ] Add a `_milkyWayPipeline` to `VkSkyMapPipeline` (separate from star/line pipelines)
- [ ] Vertex shader: pass through NDC quad corners (same pattern as `_horizonFillPipeline`)
- [ ] Fragment shader:
  - Read `gl_FragCoord` -> normalize to viewport UV
  - Apply inverse stereographic projection using the UBO view matrix to get a J2000 unit vector
  - Convert unit vector to equirectangular UV: `u = atan2(y, x) / (2*PI)`, `v = acos(z) / PI`
  - Sample the Milky Way texture
  - Multiply by brightness uniform
  - Output with additive blending
- [ ] Draw order: after sky background fill, before grid lines (so the Milky Way
      shows through the grid but behind stars)
- [ ] `GL_TEXTURE_WRAP_S = REPEAT` for seamless longitude wrap

### Phase 2: Texture Pipeline

- [ ] Download/generate the equirectangular texture
- [ ] Ship as an embedded resource or lazy-download on first use (like star catalogs)
- [ ] Support multiple resolutions: 1024x512 (fast load), 2048x1024 (default), 4096x2048 (high quality)
- [ ] Texture loaded via `VulkanContext.CreateTexture` with mipmaps for smooth appearance at any zoom

### Phase 3: Brightness + Atmosphere Integration

- [ ] Brightness uniform driven by:
  - User toggle (`[W]` key or settings)
  - Bortle index from weather/site data (brighter sky = less visible Milky Way)
  - Sun altitude (Milky Way invisible during day, fades during twilight)
  - Current `SkyBackgroundColorForSunAltitude` already models this
- [ ] Saturation control (optional, Stellarium uses HSV in-shader)
- [ ] Fade at horizon when horizon clipping is enabled

### Phase 4: Self-Generated Fallback

If we want zero external dependencies:
- [ ] At startup (or as a build-time tool), render Tycho-2 stars onto a 2048x1024
      equirectangular buffer using a wide Gaussian PSF per star (magnitude-weighted)
- [ ] Apply a large-radius Gaussian blur to simulate unresolved background starlight
- [ ] Cache the result as a PNG in AppData (rebuild if catalog changes)
- [ ] This gives a "synthetic Milky Way" that's consistent with our star catalog
      and has no licensing concerns

## GLSL Notes

- Keep all GLSL in ASCII (no Unicode) per CLAUDE.md
- The inverse stereographic projection math mirrors `SkyMapProjection.ProjectUnitVec`
  but in reverse: screen coords -> camera-space direction -> J2000 unit vector
- The UBO already contains the view matrix, viewport center, and pixels-per-radian
- For the inverse projection: `rho = sqrt(cx^2 + cy^2)`, `c = 2*atan(rho/2)`,
  then standard stereographic inverse to get the unit vector, then multiply by
  the inverse view matrix to get J2000 coordinates
