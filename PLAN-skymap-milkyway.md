# Plan: Milky Way Background for Sky Map

Render a diffuse Milky Way background on the sky map, matching the visual
quality of Stellarium's approach but with freely-licensed imagery and data.

## Stellarium's Approach (Reference)

- Single 2048x1024 equirectangular PNG (Mellinger All-Sky Panorama, ~980 KB)
- Full-screen quad (4 vertices, no sky sphere mesh)
- Fragment shader: invert projection -> J2000 unit vector -> spherical angles -> texture UV
- Additive blend (`GL_ONE, GL_ONE`) on top of sky background color
- Brightness modulated by Bortle index / light pollution / atmosphere
- Drawn first (before grid, stars, constellations)
- Per-project licensed from Axel Mellinger (not reusable)

## Data Sources

We compose our Milky Way from **data we already have or can freely obtain**,
avoiding any per-project licensing:

| Layer | Source | In repo? | Notes |
|-------|--------|----------|-------|
| Stellar luminance | **Tycho-2** (~2.5M stars, V mag + B-V) | ✅ Embedded in TianWen.Lib | `CopyTycho2Stars`; binning gives real galactic plane + bulge + LMC/SMC |
| Dust extinction | **Planck 353 GHz** dust opacity HEALPix | ❌ Download on demand | ESA Planck Legacy Archive, public domain, Nside=512 (~12 MB) |
| Colour | Flux-weighted mean **B-V** per bin | ✅ Derived from Tycho-2 | Natural warm/cool tint (bulge redder, arms bluer) |

Composite at bake time:
```
luminance(x,y) = sum over stars in bin of 10^(-0.4 * VMag)  // flux
blurred       = gaussian_blur(luminance, sigma ~ 1.2 px)
extinction    = exp(-k * tau_353)                           // dust mask
visible       = blurred * extinction
rgb           = bv_to_rgb(weighted_mean_bv) * normalise(sqrt(visible))
```

## Implementation Status

### ✅ Phase 1: Shader + Full-Screen Quad (DONE, commit 412500b)

- Pipeline: `_milkyWayPipeline` in `VkSkyMapPipeline`, inverse stereographic + equirectangular UV + additive blend
- Push constant: `alpha` (sun altitude fade)
- Draw order: after sky background fill, before grid
- `[S]` key toggles `ShowMilkyWay`, FOV-dependent dimming for wide views
- Texture loader: `SkyMapTab.TryLoadMilkyWayTexture` reads `milkyway.bgra.lz` next to executable, calls virtual `OnMilkyWayLoaded`

### ✅ Phase 2: Texture Pipeline (DONE, commit 412500b)

- 8-byte header (int32 LE width + height) + raw BGRA, lzip-compressed
- Shipped as `TianWen.UI.Gui/Resources/milkyway.bgra.lz` (~131 KB at 2048x1024)
- Current content: analytical model from `tools/generate_milkyway.py` — bright
  band at b=0, bulge at l=0, smooth warm/cool tint. **Placeholder only** — no
  real galactic structure.

### 🟡 Phase 3: Bake from Real Data (CURRENT)

Replace the analytical placeholder with a two-step physically-motivated bake:

- [x] **Tool rewrite** — `tools/generate_milkyway.cs` (.NET 10 file-based app,
      `#:project ../src/TianWen.Lib/TianWen.Lib.csproj`) replaces the Python
      version. Reuses runtime catalog loader (`CelestialObjectDB.InitDBAsync`
      + `CopyTycho2Stars`), same B-V → RGB Planckian math as the shader.
      Runs with `dotnet run tools/generate_milkyway.cs`.
- [x] **Tycho-2 luminance binning** — stream all stars, accumulate flux
      `10^(-0.4 * VMag)` (magnitude floor at V=4 so Sirius doesn't blow out one
      pixel) and flux-weighted B-V sum into equirectangular grid. RA wrap-
      aware Gaussian blur in the horizontal pass, pole-clamped vertically.
- [x] **Brightness curve** — percentile clip (p50 = zero, p99.5 = max) + sqrt
      gamma so the bulge doesn't saturate while the outer disk remains visible.
- [ ] **Planck dust extinction** — add `--dust-opacity <path>` optional arg
      (scaffolded in tool, multiplies `exp(-k * tau)` when provided). Needs a
      separate HEALPix → equirectangular reprojection step to produce the
      float map — see Phase 4 below.
- [ ] Bake + commit the real-data `milkyway.bgra.lz` once Phase 4 ships.

### 🔴 Phase 4: Planck Dust Extinction HEALPix Reader

Needed to produce the `--dust-opacity` input for `generate_milkyway.cs`. Two
options:

**Option A: Separate tool (`tools/reproject_planck_dust.cs`)**

- Download `HFI_CompMap_ThermalDustModel_2048_R1.20.fits` from ESA Planck
  Legacy Archive (Nside=2048, ~200 MB) — or the lower-res Nside=512 variant
  (~12 MB) if bandwidth is a concern. Cache under `tools/data/` (gitignored).
- Read `TAU353` column via `FITS.Lib` BinaryTableHDU (column is `float32`,
  one value per HEALPix pixel, NESTED ordering).
- For each output equirectangular pixel `(u, v)`:
  1. `ra = (u - 0.5) * 2*PI`, `dec = (0.5 - v) * PI`
  2. J2000 RA/Dec → galactic `(l, b)` (same transform as Tycho-2 path, reused)
  3. Galactic (l, b) → HEALPix pixel index via NESTED `ang2pix` (standard
     Górski 2005 math, ~50 lines)
  4. Sample `tau_353[pixel]`
- Write as raw float32 equirectangular file at the target resolution.

**Option B: Bake into `generate_milkyway.cs` directly**

Skip the intermediate file — pass `--planck-fits <path>` and do the reprojection
inline. Simpler for end-users (one command) but couples catalog binning and
dust reading in the same tool.

**Recommendation**: Option A. Keeps concerns separated; the reprojected dust
map is also useful for other future overlays (dust column info on planner,
extinction warnings for deep-sky targets). The file is roughly
`width * height * 4` bytes (~16 MB for 2048x1024) so checking it in as
build-time input is fine, or we cache it in AppData on first run.

### 🔴 Phase 5: Brightness + Atmosphere Integration

Already largely present in Phase 1 (sun altitude fade), but can be extended:

- [ ] Modulate alpha by Bortle index from weather/site data
- [ ] Saturation control (HSV in-shader for "how colorful" slider)
- [ ] Fade at horizon when horizon clipping is enabled

## Tool Usage

```bash
# Tycho-2 density only (no dust) — replaces current analytical model
dotnet run tools/generate_milkyway.cs

# High resolution
dotnet run tools/generate_milkyway.cs -- --width 4096 --height 2048

# With Planck dust extinction (requires Phase 4 reprojection step)
dotnet run tools/reproject_planck_dust.cs -- --fits tools/data/HFI_dust_353.fits --output tools/data/dust_2048.f32
dotnet run tools/generate_milkyway.cs -- --dust-opacity tools/data/dust_2048.f32 --k 1.5
```

## GLSL Notes (Phase 1, shipped)

- All GLSL is ASCII (no Unicode per CLAUDE.md)
- Inverse stereographic mirrors `SkyMapProjection.ProjectUnitVec` in reverse:
  screen coords -> camera-space direction -> J2000 unit vector
- UBO contains view matrix, viewport center, pixels-per-radian
- Inverse projection: `rho = sqrt(cx^2 + cy^2)`, `c = 2*atan(rho/2)`, standard
  stereographic inverse for unit vector, then `transpose(view) *` for J2000

## Rationale

- **Tycho-2 binning first** was the obvious win we were missing — we already
  own the data. The analytical model that shipped in commit 412500b looks fake
  (smooth gradient); real Tycho-2 binning produces the mottled, structured
  appearance users expect, plus LMC/SMC visible for free.
- **Planck dust separately** because the HEALPix reprojection is reusable
  infrastructure and the raw 200 MB FITS has no business in the repo.
- **File-based .NET tool instead of Python** to reuse the exact catalog reader
  the runtime uses (guaranteed consistency) and keep the build win-arm64
  friendly (no Python dependency required).
